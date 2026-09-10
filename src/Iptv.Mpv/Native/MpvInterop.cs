
using System.Runtime.InteropServices;

namespace Iptv.Mpv.Native;

/// <summary>mpv error codes. Zero and above are success.</summary>
public enum MpvError
{
    Success = 0,
    EventQueueFull = -1,
    NoMemory = -2,
    Uninitialized = -3,
    InvalidParameter = -4,
    OptionNotFound = -5,
    OptionFormat = -6,
    OptionError = -7,
    PropertyNotFound = -8,
    PropertyFormat = -9,
    PropertyUnavailable = -10,
    PropertyError = -11,
    Command = -12,
    LoadingFailed = -13,
    AudioInitFailed = -14,
    VideoInitFailed = -15,
    NothingToPlay = -16,
    UnknownFormat = -17,
    Unsupported = -18,
    NotImplemented = -19,
    Generic = -20,
}

/// <summary>
/// Raw P/Invoke surface for libmpv.
/// </summary>
/// <remarks>
/// <para>
/// <c>LibraryImport</c> source generators rather than <c>DllImport</c>, per the
/// conventions: marshalling is generated at compile time, so there is no reflection at
/// startup and the marshalling code is inspectable.
/// </para>
/// <para>
/// The library is resolved through <see cref="MpvLibrary"/> rather than by letting the
/// runtime search, so the load path is deterministic and a missing binary produces a
/// clear error instead of a probe across the whole PATH.
/// </para>
/// </remarks>
internal static partial class MpvInterop
{
    internal const string LibraryName = "libmpv-2.dll";

    /// <summary>
    /// Installs the import resolver before any P/Invoke in this class can run.
    /// </summary>
    /// <remarks>
    /// A static constructor rather than a <c>ModuleInitializer</c>: the initializer would
    /// run at an unpredictable point for anything referencing this assembly (CA2255),
    /// whereas a static constructor is guaranteed to run before the first member access
    /// and no earlier, which is exactly the requirement.
    /// </remarks>
    static MpvInterop() => MpvLibrary.RegisterResolver(typeof(MpvInterop).Assembly);

    // --- Client API ---------------------------------------------------------------

    /// <summary>Returns the client API version the loaded binary was built against.</summary>
    [LibraryImport(LibraryName)]
    internal static partial ulong mpv_client_api_version();

    /// <summary>Creates an uninitialised handle. Options may be set before initialising.</summary>
    [LibraryImport(LibraryName)]
    internal static partial IntPtr mpv_create();

    [LibraryImport(LibraryName)]
    internal static partial int mpv_initialize(IntPtr ctx);

    [LibraryImport(LibraryName)]
    internal static partial void mpv_terminate_destroy(IntPtr ctx);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int mpv_set_option_string(IntPtr ctx, string name, string data);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int mpv_set_property_string(IntPtr ctx, string name, string data);

    /// <summary>
    /// Returns a property as a UTF-8 string owned by mpv.
    /// </summary>
    /// <remarks>
    /// The caller must free the result with <see cref="mpv_free"/>. Returned as
    /// <see cref="IntPtr"/> rather than a marshalled string precisely so that ownership
    /// stays visible at the call site.
    /// </remarks>
    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr mpv_get_property_string(IntPtr ctx, string name);

    [LibraryImport(LibraryName)]
    internal static partial void mpv_free(IntPtr data);

    /// <summary>Returns a static, never-freed description of an error code.</summary>
    [LibraryImport(LibraryName)]
    internal static partial IntPtr mpv_error_string(int error);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int mpv_request_log_messages(IntPtr ctx, string minLevel);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int mpv_observe_property(IntPtr ctx, ulong replyUserdata, string name, int format);

    [LibraryImport(LibraryName)]
    internal static partial IntPtr mpv_wait_event(IntPtr ctx, double timeout);

    [LibraryImport(LibraryName)]
    internal static partial void mpv_wakeup(IntPtr ctx);

    /// <summary>Issues a command as a NULL-terminated argument array.</summary>
    [LibraryImport(LibraryName)]
    internal static partial int mpv_command(IntPtr ctx, IntPtr args);

    // --- Render API ---------------------------------------------------------------
    // Spike 0.1: the only backends are "opengl" and "sw". There is no D3D11 entry point.

    [LibraryImport(LibraryName)]
    internal static partial int mpv_render_context_create(out IntPtr res, IntPtr mpv, IntPtr parameters);

    [LibraryImport(LibraryName)]
    internal static partial int mpv_render_context_render(IntPtr ctx, IntPtr parameters);

    [LibraryImport(LibraryName)]
    internal static partial ulong mpv_render_context_update(IntPtr ctx);

    [LibraryImport(LibraryName)]
    internal static partial void mpv_render_context_free(IntPtr ctx);

    /// <summary>Describes an error code for a message.</summary>
    internal static string DescribeError(int error)
        => Marshal.PtrToStringUTF8(mpv_error_string(error)) ?? $"mpv error {error}";
}
