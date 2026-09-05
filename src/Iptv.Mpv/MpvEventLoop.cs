using System.Runtime.InteropServices;
using System.Threading.Channels;
using Iptv.Mpv.Native;

namespace Iptv.Mpv;

/// <summary>
/// Pumps one mpv handle's events onto a channel.
/// </summary>
/// <remarks>
/// <para>
/// <c>mpv_wait_event</c> blocks, so this owns a dedicated long-running thread rather than
/// a pool task, exactly as the PRD requires: a blocked pool thread starves everything else
/// scheduled behind it, and the pool grows to compensate in a way that looks like a leak.
/// </para>
/// <para>
/// Nothing here touches XAML. Events are copied into plain records and published to a
/// <see cref="Channel{T}"/>; the ViewModel layer consumes them and hops to the UI thread
/// once. Scattering dispatcher calls through the interop layer is what the PRD prohibits.
/// </para>
/// </remarks>
public sealed class MpvEventLoop : IDisposable
{
    /// <summary>
    /// mpv owns an event's memory only until the next wait call on that handle.
    /// </summary>
    /// <remarks>
    /// Every field is copied out before publishing. Handing a consumer a pointer would
    /// produce a use-after-free whose timing depends on how fast the consumer runs, which
    /// is close to undebuggable.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeEvent
    {
        public MpvEventKind EventId;
        public int Error;
        public ulong ReplyUserData;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeProperty
    {
        public IntPtr Name;
        public MpvFormat Format;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeLogMessage
    {
        public IntPtr Prefix;
        public IntPtr Level;
        public IntPtr Text;
        public int LogLevel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeEndFile
    {
        public int Reason;
        public int Error;
    }

    private readonly MpvHandle _handle;
    private readonly Channel<MpvEvent> _events;
    private readonly CancellationTokenSource _shutdown = new();
    private Thread? _thread;

    public MpvEventLoop(MpvHandle handle, int capacity = 512)
    {
        ArgumentNullException.ThrowIfNull(handle);

        _handle = handle;

        // Dropping the oldest rather than blocking. mpv's thread must never be stalled by
        // a slow consumer - it is the same thread that decodes - and a stale time-pos is
        // worth nothing anyway.
        _events = Channel.CreateBounded<MpvEvent>(new BoundedChannelOptions(capacity)
        {
            SingleWriter = true,
            SingleReader = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
    }

    /// <summary>Events marshalled off mpv's thread.</summary>
    public ChannelReader<MpvEvent> Events => _events.Reader;

    /// <summary>Starts pumping. Idempotent.</summary>
    public void Start()
    {
        if (_thread is not null)
        {
            return;
        }

        _thread = new Thread(Pump)
        {
            Name = "mpv-events",
            IsBackground = true,
        };

        _thread.Start();
    }

    /// <summary>
    /// Asks mpv to report changes to a property.
    /// </summary>
    /// <remarks>
    /// <see cref="MpvFormat.String"/> for anything displayed, <see cref="MpvFormat.Double"/>
    /// for time and cache values, <see cref="MpvFormat.Flag"/> for booleans. Requesting the
    /// wrong format gives an event whose data pointer cannot be read as expected, which
    /// surfaces as a null value rather than an error.
    /// </remarks>
    public void ObserveProperty(string name, MpvFormat format)
        => _handle.ObserveProperty(name, format);

    private void Pump()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            // A timeout rather than an indefinite wait, so shutdown is observed without
            // needing mpv_wakeup to be delivered reliably during teardown.
            var pointer = _handle.WaitEvent(0.1);
            if (pointer == IntPtr.Zero)
            {
                continue;
            }

            var native = Marshal.PtrToStructure<NativeEvent>(pointer);
            if (native.EventId == MpvEventKind.None)
            {
                continue;
            }

            var translated = Translate(native);
            if (translated is not null)
            {
                _events.Writer.TryWrite(translated);
            }

            if (native.EventId == MpvEventKind.Shutdown)
            {
                break;
            }
        }

        _events.Writer.TryComplete();
    }

    private static MpvEvent? Translate(NativeEvent native) => native.EventId switch
    {
        MpvEventKind.PropertyChange => TranslateProperty(native.Data),
        MpvEventKind.LogMessage => TranslateLog(native.Data),
        MpvEventKind.EndFile => TranslateEndFile(native.Data),
        _ => new MpvSimpleEvent(native.EventId),
    };

    private static MpvEvent? TranslateProperty(IntPtr data)
    {
        if (data == IntPtr.Zero)
        {
            return null;
        }

        var property = Marshal.PtrToStructure<NativeProperty>(data);
        var name = Marshal.PtrToStringUTF8(property.Name);
        if (name is null)
        {
            return null;
        }

        // A null data pointer means the property became unavailable, which is meaningful
        // rather than an error: hwdec-current reads that way before the first frame.
        object? value = null;
        if (property.Data != IntPtr.Zero)
        {
            value = property.Format switch
            {
                MpvFormat.String or MpvFormat.OsdString =>
                    Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(property.Data)),
                MpvFormat.Flag => (long)Marshal.ReadInt32(property.Data),
                MpvFormat.Int64 => Marshal.ReadInt64(property.Data),
                MpvFormat.Double => BitConverter.Int64BitsToDouble(Marshal.ReadInt64(property.Data)),
                _ => null,
            };
        }

        return new MpvPropertyChanged(name, property.Format, value);
    }

    private static MpvEvent? TranslateLog(IntPtr data)
    {
        if (data == IntPtr.Zero)
        {
            return null;
        }

        var message = Marshal.PtrToStructure<NativeLogMessage>(data);
        return new MpvLogMessage(
            Marshal.PtrToStringUTF8(message.Prefix) ?? string.Empty,
            Marshal.PtrToStringUTF8(message.Level) ?? string.Empty,
            (Marshal.PtrToStringUTF8(message.Text) ?? string.Empty).TrimEnd('\n'));
    }

    private static MpvEvent TranslateEndFile(IntPtr data)
    {
        if (data == IntPtr.Zero)
        {
            return new MpvEndFile(0, 0);
        }

        var end = Marshal.PtrToStructure<NativeEndFile>(data);
        return new MpvEndFile(end.Reason, end.Error);
    }

    public void Dispose()
    {
        _shutdown.Cancel();

        // Waking mpv shortens the join to the current timeout rather than a full one.
        if (!_handle.IsDisposed)
        {
            _handle.Wakeup();
        }

        _thread?.Join(TimeSpan.FromSeconds(2));
        _shutdown.Dispose();
    }
}
