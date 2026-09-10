using System.Runtime.ExceptionServices;
using Iptv.Mpv;
using Iptv.Mpv.Native;

namespace Iptv.Mpv.Tests;

/// <summary>
/// Runs a test body on a thread that owns a current OpenGL context.
/// </summary>
/// <remarks>
/// <para>
/// A WGL context belongs to one thread at a time, and xunit runs tests on pool threads it
/// is free to reuse or to move between awaits — so making a context current on one makes
/// the outcome depend on scheduling.
/// </para>
/// <para>
/// One copy. There were four, differing only in which preconditions they forgot: the one
/// that failed on CI checked the driver's capabilities after creating the context, which
/// is a check that never runs on a machine where creating it is what fails.
/// </para>
/// </remarks>
internal static class GlThread
{
    /// <summary>For a body that does not need the context itself, only a current one.</summary>
    internal static void Run(Action body)
    {
        ArgumentNullException.ThrowIfNull(body);
        Run(_ => body());
    }

    internal static void Run(Action<WglContext> body)
    {
        ArgumentNullException.ThrowIfNull(body);

        // Asked before the thread starts, and in this order. Creating a context is how you
        // find out whether one can be created, so it has to precede anything that assumes
        // one — including the capability check that used to come first.
        Requires.LibMpv();
        Requires.OpenGl();

        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                using var context = WglContext.Create();
                context.MakeCurrent();

                Requires.HardwarePath(context);

                body(context);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                WglContext.ClearCurrent();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!thread.Join(TimeSpan.FromMinutes(2)))
        {
            throw new TimeoutException("The GL thread did not finish within two minutes.");
        }

        if (failure is not null)
        {
            // Rethrown with its type intact. A skip that crossed this boundary as a
            // generic failure would report the machine's limitations as a broken build,
            // which is the bug this whole file exists to fix.
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}

/// <summary>
/// What a test needs from the machine it is running on.
/// </summary>
/// <remarks>
/// <para>
/// These tests drive real libmpv through a real OpenGL context, and neither is present
/// everywhere. A build agent has no display driver; a fresh clone has no libmpv until the
/// fetch script runs. Both are reasons a test cannot apply here, not reasons it failed.
/// </para>
/// <para>
/// Skipped rather than returned from. The pattern this replaces wrote a line to the test
/// output and returned, which the runner reports as a pass — so a suite where every
/// GPU test had quietly stopped running looked exactly like one where they all worked.
/// The whole point of running these on CI is to notice that, and it cannot be noticed
/// from a green tick.
/// </para>
/// </remarks>
internal static class Requires
{
    /// <summary>Skips unless libmpv-2.dll can be loaded.</summary>
    internal static void LibMpv()
        => Skip.IfNot(
            MpvLibrary.IsAvailable(),
            "libmpv-2.dll not present. Fetch it: pwsh ./scripts/fetch-libmpv.ps1");

    /// <summary>
    /// Skips unless this machine can create an OpenGL context at all.
    /// </summary>
    /// <remarks>
    /// Checked by creating one and throwing it away. There is no cheaper question to ask:
    /// WGL has no capability query that does not first need a context.
    /// </remarks>
    internal static void OpenGl()
    {
        var available = WglContext.TryCreate(out var context);
        context?.Dispose();

        Skip.IfNot(
            available,
            "No OpenGL on this machine: a build agent or a VM with no display driver.");
    }

    /// <summary>
    /// Skips unless the driver can drive mpv's OpenGL renderer.
    /// </summary>
    /// <remarks>
    /// A context that exists is not enough. Windows ships a software OpenGL 1.1 when no
    /// vendor driver is installed, and it has no WGL_NV_DX_interop2 — which is the whole
    /// mechanism by which a frame reaches a SwapChainPanel without a readback.
    /// </remarks>
    internal static void HardwarePath(WglContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var capabilities = context.Query();

        Skip.IfNot(
            capabilities.SupportsHardwarePath,
            $"Driver cannot support the hardware path: {capabilities.Renderer}, " +
            $"GL {capabilities.Version}, NV_DX_interop2={capabilities.HasDxInterop}.");
    }
}
