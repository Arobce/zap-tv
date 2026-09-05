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
