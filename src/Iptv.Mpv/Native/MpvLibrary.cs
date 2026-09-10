using System.Reflection;
using System.Runtime.InteropServices;

namespace Iptv.Mpv.Native;

/// <summary>
/// Resolves <c>libmpv-2.dll</c> from a deterministic set of locations.
/// </summary>
/// <remarks>
/// <para>
/// The PRD requires loading the binary explicitly rather than letting the runtime probe,
/// so the load path is predictable. It also means a missing or wrong-architecture binary
/// produces a message that says so, instead of a <c>DllNotFoundException</c> that looks
/// identical whether the file is absent, blocked, or built for the wrong CPU.
/// </para>
/// <para>
/// The architecture case matters in practice: an x64 <c>libmpv-2.dll</c> in an ARM64
/// package fails to load with an error that reads as a missing dependency.
/// </para>
/// </remarks>
public static class MpvLibrary
{
    private static readonly object Gate = new();
    private static bool _registered;

    /// <summary>Where the resolver looked, for diagnostics when it fails.</summary>
    public static IReadOnlyList<string> SearchedPaths => BuildCandidates();

    /// <summary>Installs the import resolver for the given assembly. Idempotent.</summary>
    public static void RegisterResolver(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        lock (Gate)
        {
            if (_registered)
            {
                return;
            }

            NativeLibrary.SetDllImportResolver(assembly, Resolve);
            _registered = true;
        }
    }

    /// <summary>Whether the native library can be located and loaded.</summary>
    /// <remarks>
    /// Used by tests and the harness to skip playback work on a machine without the
    /// binary, rather than failing in a way that looks like a code defect.
    /// </remarks>
    public static bool IsAvailable()
    {
        foreach (var candidate in BuildCandidates())
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
            {
                NativeLibrary.Free(handle);
                return true;
            }
        }

        return false;
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, MpvInterop.LibraryName, StringComparison.OrdinalIgnoreCase))
        {
            return IntPtr.Zero;
        }

        foreach (var candidate in BuildCandidates())
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
            {
                return handle;
            }
        }

        throw new DllNotFoundException(
            $"Could not load {MpvInterop.LibraryName}. Looked in:{Environment.NewLine}" +
            string.Join(Environment.NewLine, BuildCandidates().Select(p => "  " + p)) +
            $"{Environment.NewLine}Ensure the binary matches this process's architecture " +
            $"({RuntimeInformation.ProcessArchitecture}).");
    }

    private static List<string> BuildCandidates()
    {
        var candidates = new List<string>
        {
            // Shipped alongside the app: the only location that matters in production.
            Path.Combine(AppContext.BaseDirectory, MpvInterop.LibraryName),
            Path.Combine(AppContext.BaseDirectory, "runtimes", RuntimeIdentifier(), "native", MpvInterop.LibraryName),
        };

        // Development fallback: the gitignored .local/native tree, found by walking up
        // from the output directory. Keeps a 120MB binary out of the repository without
        // forcing every developer to copy it by hand.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            candidates.Add(Path.Combine(directory.FullName, ".local", "native", "mpv", MpvInterop.LibraryName));
            directory = directory.Parent;
        }

        return candidates;
    }

    private static string RuntimeIdentifier() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "win-x64",
        Architecture.Arm64 => "win-arm64",
        Architecture.X86 => "win-x86",
        _ => "win",
    };
}
