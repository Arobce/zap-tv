using System.Runtime.InteropServices;
using Iptv.Mpv.Native;

namespace Iptv.Mpv;

/// <summary>An mpv call returned an error.</summary>
public sealed class MpvException(string message, MpvError error) : Exception(message)
{
    public MpvError Error { get; } = error;
}

/// <summary>
/// One mpv player instance.
/// </summary>
/// <remarks>
/// <para>
/// Owns the native handle and frees it on dispose. Phase 7 runs two of these - one
/// presenting, one prebuffering - so the lifetime has to be exact: a leaked context holds
/// a decoder and, on a single-connection account, the provider's only slot.
/// </para>
/// <para>
/// Not thread-safe. mpv's own API mostly is, but the intended usage is one owning thread
/// per handle with events marshalled out through a channel, per the PRD.
/// </para>
/// </remarks>
public sealed class MpvHandle : IDisposable
{
    private IntPtr _handle;

    private MpvHandle(IntPtr handle) => _handle = handle;

    /// <summary>The client API version of the loaded binary, as (major, minor).</summary>
    public static (int Major, int Minor) ClientApiVersion
    {
        get
        {
            var version = MpvInterop.mpv_client_api_version();
            return ((int)(version >> 16), (int)(version & 0xFFFF));
        }
    }

    /// <summary>True once the handle has been disposed.</summary>
    public bool IsDisposed => _handle == IntPtr.Zero;

    /// <summary>
    /// The native context, for the render API.
    /// </summary>
    /// <remarks>
    /// Exposed only so a render context can be created against this handle. The render
    /// context must be freed before the handle, which is why both live in this assembly
    /// rather than the pointer being handed to callers.
    /// </remarks>
    internal IntPtr RawHandle
    {
        get
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            return _handle;
        }
    }

    /// <summary>
    /// Issues a command as a NULL-terminated argument vector.
    /// </summary>
    /// <remarks>
    /// The string form (<c>mpv_command_string</c>) splits on whitespace, which corrupts
    /// any argument containing a space - and every stream URL here carries a
    /// provider-supplied path. The vector form has no such ambiguity.
    /// </remarks>
    public void Command(params string[] arguments)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ArgumentNullException.ThrowIfNull(arguments);

        var pointers = new IntPtr[arguments.Length + 1];
        try
        {
            for (var i = 0; i < arguments.Length; i++)
            {
                pointers[i] = Marshal.StringToCoTaskMemUTF8(arguments[i]);
            }

            pointers[^1] = IntPtr.Zero;

            var handle = GCHandle.Alloc(pointers, GCHandleType.Pinned);
            try
            {
                Check(
                    MpvInterop.mpv_command(_handle, handle.AddrOfPinnedObject()),
                    $"mpv_command({arguments[0]})");
            }
            finally
            {
                handle.Free();
            }
        }
        finally
        {
            foreach (var pointer in pointers)
            {
                if (pointer != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(pointer);
                }
            }
        }
    }

    /// <summary>
    /// Creates a handle and applies options, then initialises it.
    /// </summary>
    /// <remarks>
    /// Options are applied between create and initialise because several of them - the
    /// video output in particular - cannot be changed afterwards.
    /// </remarks>
    public static MpvHandle Create(IReadOnlyDictionary<string, string>? options = null)
    {
        var raw = MpvInterop.mpv_create();
        if (raw == IntPtr.Zero)
        {
            throw new MpvException("mpv_create returned null; libmpv could not allocate a context.", MpvError.NoMemory);
        }

        var handle = new MpvHandle(raw);
        try
        {
            if (options is not null)
            {
                foreach (var (key, value) in options)
                {
                    handle.SetOption(key, value);
                }
            }

            handle.Check(MpvInterop.mpv_initialize(raw), "mpv_initialize");
            return handle;
        }
        catch
        {
            // A half-initialised context still holds native memory.
            handle.Dispose();
            throw;
        }
    }

    /// <summary>Sets an option. Most must be set before initialise.</summary>
    public void SetOption(string name, string value)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        Check(MpvInterop.mpv_set_option_string(_handle, name, value), $"mpv_set_option_string({name})");
    }

    /// <summary>Sets a property. Valid after initialise.</summary>
    public void SetProperty(string name, string value)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        Check(MpvInterop.mpv_set_property_string(_handle, name, value), $"mpv_set_property_string({name})");
    }

    /// <summary>Reads a property as a string, or null when unavailable.</summary>
    public string? GetProperty(string name)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        var pointer = MpvInterop.mpv_get_property_string(_handle, name);
        if (pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUTF8(pointer);
        }
        finally
        {
            // The string is mpv's, and leaking one per property read would matter: the
            // player polls time-pos and cache state continuously during playback.
            MpvInterop.mpv_free(pointer);
        }
    }

    /// <summary>Routes mpv log messages at or above a level into the event stream.</summary>
    public void RequestLogMessages(string minimumLevel = "warn")
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        Check(MpvInterop.mpv_request_log_messages(_handle, minimumLevel), "mpv_request_log_messages");
    }

    /// <summary>Asks mpv to raise an event whenever a property changes.</summary>
    public void ObserveProperty(string name, MpvFormat format)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        Check(
            MpvInterop.mpv_observe_property(_handle, 0, name, (int)format),
            $"mpv_observe_property({name})");
    }

    /// <summary>
    /// Blocks until an event arrives or the timeout elapses.
    /// </summary>
    /// <remarks>
    /// Internal because the returned pointer is only valid until the next call on this
    /// handle. <see cref="MpvEventLoop"/> copies everything out before publishing; letting
    /// callers hold the pointer would be a use-after-free waiting to happen.
    /// </remarks>
    internal IntPtr WaitEvent(double timeoutSeconds)
        => IsDisposed ? IntPtr.Zero : MpvInterop.mpv_wait_event(_handle, timeoutSeconds);

    /// <summary>Interrupts a blocked <see cref="WaitEvent"/>.</summary>
    internal void Wakeup()
    {
        if (!IsDisposed)
        {
            MpvInterop.mpv_wakeup(_handle);
        }
    }

    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (handle != IntPtr.Zero)
        {
            MpvInterop.mpv_terminate_destroy(handle);
        }
    }

    private void Check(int result, string operation)
    {
        if (result >= 0)
        {
            return;
        }

        throw new MpvException(
            $"{operation} failed: {MpvInterop.DescribeError(result)}.", (MpvError)result);
    }
}
