using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace Iptv.Core.Sources;

/// <summary>
/// Protects secrets with Windows DPAPI, scoped to the current user.
/// </summary>
/// <remarks>
/// <para>
/// The PRD requires DPAPI and never plaintext. CurrentUser rather than LocalMachine: a
/// LocalMachine blob is readable by every account on the box, which for a subscription
/// credential is barely better than storing it in the clear.
/// </para>
/// <para>
/// This lives in the platform-neutral assembly, which needs justifying. IPTV0002 enforces
/// that <c>Iptv.Core</c> targets a neutral TFM so it builds and tests without a Windows
/// workload, and it still does — <c>System.Security.Cryptography.ProtectedData</c> is an
/// ordinary netstandard package, not a workload. The type is attributed and guarded so a
/// non-Windows caller gets a clear failure rather than a confusing one. Keeping it here
/// avoids the alternative, which was the same file copied into both the app and the
/// harness.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector : ISecretProtector
{
    /// <summary>
    /// Ties the blob to this application.
    /// </summary>
    /// <remarks>
    /// Entropy means a blob lifted out of this database cannot be decrypted by another
    /// program running as the same user simply by calling Unprotect on it.
    /// </remarks>
    private static readonly byte[] Entropy = "ZapTV.ProviderCredentials.v1"u8.ToArray();

    public byte[] Protect(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ThrowIfUnsupported();

        return ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
    }

    public byte[]? Unprotect(byte[] ciphertext)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        ThrowIfUnsupported();

        try
        {
            return ProtectedData.Unprotect(ciphertext, Entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException)
        {
            // The blob was written by a different user or on a different machine, which is
            // DPAPI working as intended. Null so the caller treats it as "no credentials"
            // and offers to enter them again, rather than dying on startup.
            return null;
        }
    }

    private static void ThrowIfUnsupported()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "DPAPI is Windows-only. Supply a different ISecretProtector on other platforms.");
        }
    }
}
