using System;
using System.IO;
using Iptv.Core.Xtream;

namespace Iptv.App;

/// <summary>
/// The diagnostic log.
/// </summary>
/// <remarks>
/// <para>
/// Lifted out of the window so the entry point can write to it too. A GUI has nowhere to
/// print, and an install hook runs before any window exists, so a failure there would
/// otherwise leave nothing behind at all.
/// </para>
/// <para>
/// Everything goes through the credential scrubber. Stream URLs carry the account's
/// username and password in the path, and this file is exactly the sort of thing that
/// gets pasted into a bug report.
/// </para>
/// </remarks>
public static class AppLog
{
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IptvPlayer",
        "logs",
        "app.log");

    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);

            File.AppendAllText(
                Path,
                $"{DateTimeOffset.Now:HH:mm:ss.fff}  {CredentialScrubber.Scrub(message)}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // Diagnostics must never take the app down.
        }
        catch (UnauthorizedAccessException)
        {
            // Nor must they take an installation down. An install hook can run before the
            // per-user directory is writable.
        }
    }
}
