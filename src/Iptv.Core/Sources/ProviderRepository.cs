using Iptv.Core.Xtream;
using Microsoft.Data.Sqlite;

namespace Iptv.Core.Sources;

/// <summary>A configured provider, as the settings view shows it.</summary>
public sealed record ProviderInfo
{
    public required int Id { get; init; }

    public required string Name { get; init; }

    public required string BaseUrl { get; init; }

    /// <summary>Lower wins during failover.</summary>
    public required int Priority { get; init; }

    public required bool Enabled { get; init; }

    /// <summary>From the account, once a sync or a test has read it.</summary>
    public int? MaxConnections { get; init; }

    public DateTimeOffset? LastSyncUtc { get; init; }

    /// <summary>Whether credentials are stored and readable on this machine.</summary>
    /// <remarks>
    /// A DPAPI blob is bound to the user and machine that wrote it, so a database copied
    /// from elsewhere has rows whose credentials cannot be decrypted. The view needs to
    /// say "re-enter these" rather than "no provider".
    /// </remarks>
    public required bool HasCredentials { get; init; }

    public required int LiveChannels { get; init; }
}

/// <summary>Reads and writes the configured providers.</summary>
public static class ProviderRepository
{
    /// <summary>Lists every provider with enough detail to show and manage it.</summary>
    public static async Task<IReadOnlyList<ProviderInfo>> ListAsync(
        SqliteConnection connection,
        ISecretProtector protector,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(protector);

        await using var command = connection.CreateCommand();

        // The channel count is per provider and scoped to live: it is the number that says
        // whether a sync actually worked, which is the first thing anyone checks here.
        command.CommandText =
            """
            SELECT
                pr.id,
                pr.name,
                pr.base_url,
                pr.priority,
                pr.enabled,
                pr.max_connections,
                pr.last_sync_utc,
                pr.username IS NOT NULL AND pr.password IS NOT NULL,
                (SELECT count(DISTINCT s.channel_key) FROM streams s
                  WHERE s.provider_id = pr.id
                    AND s.kind = 'live'
                    AND s.is_active = 1
                    AND s.is_separator = 0)
            FROM providers pr
            ORDER BY pr.priority, pr.name;
            """;

        var results = new List<ProviderInfo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new ProviderInfo
            {
                Id = reader.GetInt32(0),
                Name = reader.GetString(1),
                BaseUrl = reader.GetString(2),
                Priority = reader.GetInt32(3),
                Enabled = reader.GetInt64(4) == 1,
                MaxConnections = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                LastSyncUtc = reader.IsDBNull(6)
                    ? null
                    : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(6)),
                HasCredentials = reader.GetInt64(7) == 1,
                LiveChannels = reader.GetInt32(8),
            });
        }

        return results;
    }

    /// <summary>
    /// The most concurrent streams it is safe to open, across the enabled providers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The smallest limit, not the largest. Failover can open either provider's copy of a
    /// channel, so a limiter set to the more generous account's four would exceed the other
    /// account's one — and exceeding it is what gets an account blocked.
    /// </para>
    /// <para>
    /// One is the answer when nothing is known. A panel that reports zero, or has never
    /// been asked, must not be read as unlimited.
    /// </para>
    /// </remarks>
    public static async Task<int> GetSafeConnectionLimitAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT min(max_connections)
            FROM providers
            WHERE enabled = 1 AND max_connections IS NOT NULL AND max_connections > 0;
            """;

        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is null or DBNull ? 1 : Math.Max(1, Convert.ToInt32(value));
    }

    /// <summary>Adds a provider, or updates the one already on that host and username.</summary>
    /// <returns>The provider's id.</returns>
    /// <remarks>
    /// Matched on base URL rather than name, because the name is the user's label and they
    /// will rename it. Adding the same host twice would give failover two identical
    /// candidates and double every sync.
    /// </remarks>
    public static async Task<int> UpsertAsync(
        SqliteConnection connection,
        string name,
        XtreamCredentials credentials,
        ISecretProtector protector,
        CancellationToken cancellationToken,
        int priority = 0)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var baseUrl = credentials.BaseUrl.ToString();

        await using (var find = connection.CreateCommand())
        {
            find.CommandText = "SELECT id FROM providers WHERE base_url = @url;";
            find.Parameters.AddWithValue("@url", baseUrl);

            if (await find.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long existing)
            {
                await using var update = connection.CreateCommand();
                update.CommandText =
                    "UPDATE providers SET name = @name, priority = @priority WHERE id = @id;";
                update.Parameters.AddWithValue("@id", existing);
                update.Parameters.AddWithValue("@name", name.Trim());
                update.Parameters.AddWithValue("@priority", priority);
                await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                await ProviderCredentialStore
                    .SaveAsync(connection, existing, credentials, protector, cancellationToken)
                    .ConfigureAwait(false);

                return (int)existing;
            }
        }

        await using var insert = connection.CreateCommand();
        insert.CommandText =
            """
            INSERT INTO providers (name, kind, base_url, priority)
            VALUES (@name, 'xtream', @url, @priority)
            RETURNING id;
            """;

        insert.Parameters.AddWithValue("@name", name.Trim());
        insert.Parameters.AddWithValue("@url", baseUrl);
        insert.Parameters.AddWithValue("@priority", priority);

        var id = (long)(await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;

        await ProviderCredentialStore
            .SaveAsync(connection, id, credentials, protector, cancellationToken)
            .ConfigureAwait(false);

        return (int)id;
    }

    /// <summary>Records what the account said, after a test or a sync.</summary>
    public static async Task RecordAccountAsync(
        SqliteConnection connection,
        int providerId,
        int? maxConnections,
        DateTimeOffset? lastSync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE providers
               SET max_connections = COALESCE(@max, max_connections),
                   last_sync_utc   = COALESCE(@synced, last_sync_utc)
             WHERE id = @id;
            """;

        command.Parameters.AddWithValue("@id", providerId);
        command.Parameters.AddWithValue("@max", (object?)maxConnections ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "@synced", (object?)lastSync?.ToUnixTimeSeconds() ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Turns a provider on or off without losing it or its library.</summary>
    /// <remarks>
    /// Disabling rather than deleting is the useful operation: every query already filters
    /// on <c>enabled</c>, so a disabled provider disappears from the lists and from
    /// failover while its streams, favourites and history stay put.
    /// </remarks>
    public static async Task SetEnabledAsync(
        SqliteConnection connection,
        int providerId,
        bool enabled,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE providers SET enabled = @enabled WHERE id = @id;";
        command.Parameters.AddWithValue("@id", providerId);
        command.Parameters.AddWithValue("@enabled", enabled ? 1 : 0);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sets failover order. Lower wins.</summary>
    public static async Task SetPriorityAsync(
        SqliteConnection connection,
        int providerId,
        int priority,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE providers SET priority = @priority WHERE id = @id;";
        command.Parameters.AddWithValue("@id", providerId);
        command.Parameters.AddWithValue("@priority", priority);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes a provider and everything the schema cascades from it.
    /// </summary>
    /// <remarks>
    /// This is destructive and cascades to streams, series and health history — which is
    /// why <see cref="SetEnabledAsync"/> exists and is the operation a settings view should
    /// offer first. Favourites and resume positions survive: they hang off channel_key and
    /// content_key, not off the provider.
    /// </remarks>
    public static async Task DeleteAsync(
        SqliteConnection connection,
        int providerId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM providers WHERE id = @id;";
        command.Parameters.AddWithValue("@id", providerId);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
