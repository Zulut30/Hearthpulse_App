#if HEARTHPULSE_WPF
using Hearthpulse.App.Main.Decks;
#else
using Hearthpulse.UI.Main.Decks;
#endif

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main.Share;
#else
namespace Hearthpulse.UI.Main.Share;
#endif

/// <summary>A number on a share picture: its label, the value, a line under it; <paramref name="Sign"/> colours the value
/// (+1 positive, -1 negative, 0 plain).</summary>
public sealed record ShareStat(string Label, string Value, string Detail = "", int Sign = 0)
{
    public bool HasDetail => Detail.Length > 0;
}

/// <summary>A Battlegrounds place chip on the recap: the place, gold-edged in the top half of the lobby.</summary>
public sealed record SharePlace(string Text, bool Top);

/// <summary>One mode of the session recap: its title, a portrait (class hero or Battlegrounds hero) and its numbers.</summary>
public sealed record RecapSection(string Title, string HeroCardId, IReadOnlyList<ShareStat> Stats, IReadOnlyList<SharePlace> Places)
{
    public bool HasPlaces => Places.Count > 0;
}

/// <summary>The session recap picture.</summary>
public sealed record SessionRecapCard(string When, IReadOnlyList<RecapSection> Sections, string Empty)
{
    public bool IsEmpty => Sections.Count == 0;
}

/// <summary>
/// The deck picture in both layouts: the list rows (as on the deck page), the curve, the class portrait and the facts line.
/// The cover splits the rows into two columns.
/// </summary>
public sealed record DeckPictureCard(
    string Name,
    string ClassName,
    string HeroCardId,
    string Facts,
    string Code,
    IReadOnlyList<DeckListRow> Rows,
    IReadOnlyList<DeckListRow> FirstColumn,
    IReadOnlyList<DeckListRow> SecondColumn,
    IReadOnlyList<ManaCurveBar> Curve);

/// <summary>The year in review: its title, the tiles, and the portraits the picture leads with.</summary>
public sealed record YearCard(string Title, string Subtitle, IReadOnlyList<ShareStat> Stats, string ClassHeroCardId, string HeroCardId, string TopCardId, string Empty)
{
    public bool IsEmpty => Stats.Count == 0;

    public bool HasHero => HeroCardId.Length > 0;

    public bool HasTopCard => TopCardId.Length > 0;
}
