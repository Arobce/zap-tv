using Microsoft.Data.Sqlite;

namespace Iptv.Core.Sources;

/// <summary>Which catalogue a category belongs to.</summary>
public enum CategoryKind
{
    Live,
    Vod,

    /// <summary>
    /// Counted against the <c>series</c> table rather than <c>streams</c>.
    /// </summary>
    /// <remarks>
    /// Series are the one catalogue that does not live in <c>streams</c> — they carry no
    /// streams at all until a user opens one and the episodes are fetched — so the count
    /// query for this kind is a different statement, not the same one with a parameter.
    /// </remarks>
    Series,
}

/// <summary>One provider category, with how much is actually in it.</summary>
public sealed record CategoryListItem
{
    public required string Name { get; init; }

    /// <summary>Active, non-separator entries carrying this category.</summary>
    /// <remarks>
    /// Counted rather than assumed. Providers ship categories that are empty, or that
    /// hold nothing but separator rows, and a list of names that lead to empty screens is
    /// worse than no list.
    /// </remarks>
    public required int Count { get; init; }
}

/// <summary>Stores and reads the provider's own grouping of its catalogue.</summary>
public static class CategoryRepository
{
    /// <summary>Writes a provider's categories for one kind, replacing what was there.</summary>
    /// <remarks>
    /// Replace rather than merge, unlike stream sync. A category is provider metadata with
    /// nothing of the user's hanging off it — no favourite, no mapping, no resume position
    /// — so a dropped one is a category the provider stopped publishing, not a row worth
    /// preserving.
    /// </remarks>
    public static async Task<int> ReplaceAsync(
        SqliteConnection connection,
        long providerId,
        CategoryKind kind,
        IEnumerable<(string CategoryId, string Name, int ParentId)> categories,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(categories);

        var stored = ToStorage(kind);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = (SqliteTransaction)transaction;
            delete.CommandText =
                "DELETE FROM categories WHERE provider_id = @provider AND kind = @kind;";
            delete.Parameters.AddWithValue("@provider", providerId);
            delete.Parameters.AddWithValue("@kind", stored);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var written = 0;

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = (SqliteTransaction)transaction;

            // One prepared command reused across rows, per the conventions. There are only
            // hundreds of these rather than hundreds of thousands, but the pattern is the
            // same and the exception would be the thing to explain.
            insert.CommandText =
                """
                INSERT INTO categories (provider_id, kind, category_id, name, parent_id)
                VALUES (@provider, @kind, @id, @name, @parent)
                ON CONFLICT (provider_id, kind, category_id) DO UPDATE SET
                    name      = excluded.name,
                    parent_id = excluded.parent_id;
                """;

            insert.Parameters.AddWithValue("@provider", providerId);
            insert.Parameters.AddWithValue("@kind", stored);
            insert.Parameters.Add("@id", SqliteType.Text);
            insert.Parameters.Add("@name", SqliteType.Text);
            insert.Parameters.Add("@parent", SqliteType.Integer);

            foreach (var (categoryId, name, parentId) in categories)
            {
                if (string.IsNullOrWhiteSpace(categoryId) || string.IsNullOrWhiteSpace(name))
                {
                    // A category with no id cannot be joined to and one with no name cannot
                    // be shown. Skipped rather than stored as a blank row the user would
                    // have to click to discover is useless.
                    continue;
                }

                insert.Parameters["@id"].Value = categoryId;
                insert.Parameters["@name"].Value = name.Trim();
                insert.Parameters["@parent"].Value = parentId;

                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                written++;
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return written;
    }

    /// <summary>
    /// Categories with a non-zero count, grouped by name.
    /// </summary>
    /// <remarks>
    /// Grouped by name rather than by (provider, id): two providers both having a "Sports"
    /// category should be one entry the user picks once, not two identical rows whose
    /// difference is invisible.
    /// <para>
    /// Exposed so the harness can run <c>EXPLAIN QUERY PLAN</c> against exactly what ships,
    /// rather than against a copy that has drifted.
    /// </para>
    /// </remarks>
    public const string CategoryCountSql =
        """
        SELECT name, sum(n)
        FROM (
            SELECT
                c.name AS name,
                (SELECT count(*)
                   FROM streams s
                  WHERE s.provider_id  = c.provider_id
                    AND s.kind         = @kind
                    AND s.category_id  = c.category_id
                    AND s.is_active    = 1
                    AND s.is_separator = 0) AS n
            FROM categories c
            JOIN providers pr ON pr.id = c.provider_id AND pr.enabled = 1
            WHERE c.kind = @kind
        )
        GROUP BY name
        HAVING sum(n) > 0
        ORDER BY name;
        """;

    /// <summary>
    /// The same question against the <c>series</c> table.
    /// </summary>
    /// <remarks>
    /// A separate statement rather than a branch inside the other one. Series have no
    /// <c>is_active</c> and no <c>is_separator</c> — a series is a listing entry, not a
    /// stream — so there is nothing to filter on and the counted set is simply what the
    /// provider published.
    /// </remarks>
    public const string SeriesCategoryCountSql =
        """
        SELECT name, sum(n)
        FROM (
            SELECT
                c.name AS name,
                (SELECT count(*)
                   FROM series s
                  WHERE s.provider_id = c.provider_id
                    AND s.category_id = c.category_id) AS n
            FROM categories c
            JOIN providers pr ON pr.id = c.provider_id AND pr.enabled = 1
            WHERE c.kind = 'series'
        )
        GROUP BY name
        HAVING sum(n) > 0
        ORDER BY name;
        """;

    /// <summary>Lists the categories that actually contain something, with counts.</summary>
    public static async Task<IReadOnlyList<CategoryListItem>> GetCategoriesAsync(
        SqliteConnection connection,
        CategoryKind kind,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using var command = connection.CreateCommand();

        if (kind == CategoryKind.Series)
        {
            command.CommandText = SeriesCategoryCountSql;
        }
        else
        {
            command.CommandText = CategoryCountSql;
            command.Parameters.AddWithValue("@kind", ToStorage(kind));
        }

        var results = new List<CategoryListItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new CategoryListItem
            {
                Name = reader.GetString(0),
                Count = reader.GetInt32(1),
            });
        }

        return results;
    }

    private static string ToStorage(CategoryKind kind) => kind switch
    {
        CategoryKind.Live => "live",
        CategoryKind.Vod => "vod",
        CategoryKind.Series => "series",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unmapped category kind"),
    };
}
