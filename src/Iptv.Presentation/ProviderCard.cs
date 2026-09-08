using Iptv.Core.Sources;

namespace Iptv.Presentation;

/// <summary>One provider, phrased for the settings list.</summary>
/// <remarks>
/// The wording lives here rather than in XAML so it can be tested. What a provider row
/// needs to say is not obvious — a disabled one, one whose credentials cannot be read on
/// this machine, and one that has never synced all look the same in a table of columns.
/// </remarks>
public sealed class ProviderCard
{
    private ProviderCard(int id, string title, string detail, string enableLabel)
    {
        Id = id;
        Title = title;
        Detail = detail;
        EnableLabel = enableLabel;
    }

    public int Id { get; }

    public string Title { get; }

    /// <summary>Host, channel count, connection limit and last sync, in one line.</summary>
    public string Detail { get; }

    /// <summary>What the enable button should say, given the current state.</summary>
    public string EnableLabel { get; }

    public static ProviderCard From(ProviderInfo info, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(info);

        var parts = new List<string> { info.BaseUrl };

        if (!info.HasCredentials)
        {
            // First, because nothing else about the row matters until it is fixed. A DPAPI
            // blob is bound to the machine that wrote it, so this is what a copied database
            // looks like.
            parts.Add("credentials unreadable on this machine — re-add to fix");
        }

        if (!info.Enabled)
        {
            parts.Add("disabled");
        }

        parts.Add(info.LiveChannels == 0
            ? "no channels — never synced"
            : $"{info.LiveChannels:N0} channels");

        if (info.MaxConnections is { } connections)
        {
            parts.Add(connections == 1 ? "1 connection" : $"{connections} connections");
        }

        parts.Add(info.LastSyncUtc is { } synced ? $"synced {Ago(now - synced)}" : "never synced");

        return new ProviderCard(
            info.Id,
            info.Name,
            string.Join("  ·  ", parts),
            info.Enabled ? "Disable" : "Enable");
    }

    /// <summary>
    /// A rough age.
    /// </summary>
    /// <remarks>
    /// Rough on purpose: the question a sync time answers is "is this stale", and "3 days
    /// ago" answers it where a timestamp makes the reader do the arithmetic.
    /// </remarks>
    private static string Ago(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            // A clock change, or a database from a machine running ahead. "In the future"
            // is at least honest.
            return "in the future";
        }

        if (elapsed < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (elapsed < TimeSpan.FromHours(1))
        {
            return $"{(int)elapsed.TotalMinutes}m ago";
        }

        if (elapsed < TimeSpan.FromDays(1))
        {
            return $"{(int)elapsed.TotalHours}h ago";
        }

        return $"{(int)elapsed.TotalDays}d ago";
    }
}
