using System.Runtime.InteropServices;

namespace Iptv.Mpv.Native;

/// <summary>
/// Parameter types accepted by the render API.
/// </summary>
/// <remarks>
/// Values taken from <c>render.h</c> in the shipping build, not from documentation or
/// memory. The gaps are real - 8 and 16 are absent - and the SW values start at 17 rather
/// than following on from the DRM ones, so guessing produces a context that fails in ways
/// that look like a marshalling bug.
/// </remarks>
internal enum MpvRenderParamType
{
    Invalid = 0,
    ApiType = 1,
    OpenGlInitParams = 2,
    OpenGlFbo = 3,
    FlipY = 4,
    Depth = 5,
    IccProfile = 6,
    AmbientLight = 7,
    AdvancedControl = 10,
    NextFrameInfo = 11,
    BlockForTargetTime = 12,
    SkipRendering = 13,
    SwSize = 17,
    SwFormat = 18,
    SwStride = 19,
    SwPointer = 20,
}

/// <summary>
/// One entry in the NULL-terminated parameter array passed to the render API.
/// </summary>
/// <remarks>
/// Mirrors <c>mpv_render_param</c>: an enum followed by a pointer. Sequential layout puts
/// the pointer at offset 8 on x64 through natural alignment, which matches the C struct.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct MpvRenderParam
{
    public MpvRenderParamType Type;
    public IntPtr Data;

    public static MpvRenderParam Terminator => new() { Type = MpvRenderParamType.Invalid, Data = IntPtr.Zero };
}
