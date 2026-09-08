using System.Text;
using Iptv.Core.Xtream;
using Microsoft.Data.Sqlite;

namespace Iptv.Core.Sources;

/// <summary>Encrypts and decrypts a secret at rest.</summary>
/// <remarks>
/// An interface rather than a direct DPAPI call, because <see cref="ProviderCredentialStore"/>
/// lives in the platform-neutral assembly and DPAPI does not exist off Windows. The build
/// enforces that neutrality (IPTV0002), so the platform detail is supplied rather than
/// referenced.
/// </remarks>
public interface ISecretProtector
{
    byte[] Protect(byte[] plaintext);

    /// <summary>Returns null when the blob cannot be read back.</summary>
    /// <remarks>
    /// Null rather than throwing. A DPAPI blob is bound to the user and machine that wrote
    /// it, so a copied database decrypts to nothing — which is the store working correctly,
    /// and should read as "no credentials" rather than a crash on startup.
    /// </remarks>
    byte[]? Unprotect(byte[] ciphertext);
}

/// <summary>
/// Reads and writes a provider's credentials, encrypted at rest.
/// </summary>
/// <remarks>
/// The PRD requires DPAPI-encrypted blobs and never plaintext. Until now the harness held
/// credentials in memory for a run and the app had none at all, which is why the app could
/// browse the library but never ask the provider anything.
/// </remarks>
public static class ProviderCredentialStore
{
    /// <summary>Stores credentials for a provider, replacing any already there.</summary>
    public static async Task SaveAsync(
        SqliteConnection connection,
        long providerId,
        XtreamCredentials credentials,
        ISecretProtector protector,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(protector);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE providers
               SET base_url = @base_url,
                   username = @username,
                   password = @password
             WHERE id = @id;
            """;

        command.Parameters.AddWithValue("@id", providerId);
        command.Parameters.AddWithValue("@base_url", credentials.BaseUrl.ToString());
        command.Parameters.AddWithValue("@username", protector.Protect(Encoding.UTF8.GetBytes(credentials.Username)));
        command.Parameters.AddWithValue("@password", protector.Protect(Encoding.UTF8.GetBytes(credentials.Password)));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Loads a provider's credentials, or null when there are none to load.</summary>
    public static async Task<XtreamCredentials?> LoadAsync(
        SqliteConnection connection,
        long providerId,
        ISecretProtector protector,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(protector);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT base_url, username, password FROM providers WHERE id = @id AND enabled = 1;";
        command.Parameters.AddWithValue("@id", providerId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        if (reader.IsDBNull(1) || reader.IsDBNull(2))
        {
            // A provider row written before credentials were stored. Not an error: the
            // library still browses, and the next sync fills them in.
            return null;
        }

        var username = Decode(protector, (byte[])reader.GetValue(1));
        var password = Decode(protector, (byte[])reader.GetValue(2));

        if (username is null || password is null)
        {
            return null;
        }

        return new XtreamCredentials(new Uri(reader.GetString(0)), username, password);
    }

    private static string? Decode(ISecretProtector protector, byte[] blob)
    {
        var plaintext = protector.Unprotect(blob);
        return plaintext is null ? null : Encoding.UTF8.GetString(plaintext);
    }
}
