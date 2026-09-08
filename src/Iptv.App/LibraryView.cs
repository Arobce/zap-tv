namespace Iptv.App;

/// <summary>Which list the sidebar is showing.</summary>
/// <remarks>
/// Distinct from <see cref="LibraryKind"/>, which says what a row is. The two looked
/// interchangeable while there were three of each; favourites and continue-watching are
/// views over kinds that already exist, not new kinds of thing.
/// </remarks>
public enum LibraryView
{
    Live,
    Films,
    Series,

    /// <summary>Live channels the user marked. The payoff for the B key.</summary>
    Favourites,

    /// <summary>Films and episodes that were started and not finished.</summary>
    Continue,
}
