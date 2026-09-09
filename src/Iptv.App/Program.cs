using System;
using System.Threading;
using Microsoft.UI.Dispatching;
using Velopack;

namespace Iptv.App;

/// <summary>
/// The entry point, replacing the one the XAML compiler generates.
/// </summary>
/// <remarks>
/// <para>
/// Written by hand only because Velopack needs the first line. Installing, updating and
/// uninstalling all work by re-running this executable with Velopack's own arguments, and
/// the handler for those has to run before any window exists — the generated Main starts
/// the UI immediately, so an install hook would flash a window at someone who was
/// installing rather than launching.
/// </para>
/// <para>
/// Everything after that line is what the generated Main does, kept deliberately
/// identical rather than improved.
/// </para>
/// </remarks>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // First. Not "early" - first. Run() may never return: on an install or update hook
        // it does its work and exits the process.
        VelopackApp.Build()
            .SetArgs(args)
            .OnFirstRun(FirstRun)
            .Run();

        WinRT.ComWrappersSupport.InitializeComWrappers();

        // The callback parameter is named rather than discarded: a discard here is the
        // lambda's own parameter, and assigning to it below silently binds to that.
        Microsoft.UI.Xaml.Application.Start(parameters =>
        {
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());

            SynchronizationContext.SetSynchronizationContext(context);

            // Constructed for its side effect: Application's constructor registers it as
            // the running application, and OnLaunched follows from that.
            _ = new App();
        });
    }

    /// <summary>Runs once, the first time a freshly installed build starts.</summary>
    /// <remarks>
    /// Nothing is created here. The database, the schema and the settings are all made on
    /// demand by the code that needs them, and doing any of it here would mean a second
    /// path that has to stay in step with the first. It is a log line so that a support
    /// question about a broken install has a timestamp to anchor to.
    /// </remarks>
    private static void FirstRun(SemanticVersion version)
    {
        try
        {
            AppLog.Write($"first run of version {version}");
        }
        catch (Exception)
        {
            // A log line is not worth failing an installation over.
        }
    }
}
