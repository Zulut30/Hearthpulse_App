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
    IReadOnlyList<ManaCurveBar> Curve)
{
    /// <summary>A deck row's height at least (the list keeps it) and at most (the cover's short decks grow to it).</summary>
    public const double RowHeight = 28;

    /// <inheritdoc cref="RowHeight"/>
    public const double RowMaxHeight = 36;

    /// <summary>The gap under every row (the row's bottom margin).</summary>
    public const double RowGap = 2;

    /// <summary>One list row with its gap.</summary>
    public const double RowPitch = RowHeight + RowGap;

    /// <summary>The cover's list area: 960 × 540 less the margins (40, 32) and the 360 left column.</summary>
    public const double CoverFieldWidth = 520;

    /// <inheritdoc cref="CoverFieldWidth"/>
    public const double CoverFieldHeight = 476;

    /// <summary>The smallest a card name gets, as the picture shows it (after the cover shrinks a long deck).</summary>
    public const double NameMinFontSize = 10;

    /// <summary>Rows in each of the cover's columns (the first holds the odd one): both columns split the height into as many
    /// even rows, so they start and end on the same lines.</summary>
    public int ColumnRows => FirstColumn.Count;

    /// <summary>
    /// One cover row with its gap, in whole pixels (layout rounding would push fractional rows past the field): rows grow
    /// from 28 up to 36 but never past the field, so a short deck stays a compact block instead of a sparse ladder; a deck
    /// over 15 rows a column takes 30 a row and the cover shrinks it as a whole to the field.
    /// </summary>
    public double CoverRowPitch => Math.Clamp(Math.Floor(CoverFieldHeight / Math.Max(1, ColumnRows)), RowPitch, RowMaxHeight + RowGap);

    /// <summary>The height the cover lays its two columns out in, from the field's top.</summary>
    public double CoverListHeight => ColumnRows * CoverRowPitch;

    /// <summary>How much the cover shrinks the list to fit the field (1 up to 15 rows a column).</summary>
    public double CoverScale => CoverListHeight > CoverFieldHeight ? CoverFieldHeight / CoverListHeight : 1;

    /// <summary>The list's layout width: wider by as much as the cover shrinks it, so a long deck still spans the field.</summary>
    public double CoverListWidth => CoverFieldWidth / CoverScale;

    /// <summary>The names' smallest size in the list's own units, so that after the shrink they show at least
    /// <see cref="NameMinFontSize"/> less a point (9 at 40 cards).</summary>
    public double CoverNameMinFontSize => Math.Max(NameMinFontSize, (NameMinFontSize - 1) / CoverScale);
}

/// <summary>The year in review: its title, the tiles, and the portraits the picture leads with.</summary>
public sealed record YearCard(string Title, string Subtitle, IReadOnlyList<ShareStat> Stats, string ClassHeroCardId, string HeroCardId, string TopCardId, string Empty)
{
    public bool IsEmpty => Stats.Count == 0;

    public bool HasHero => HeroCardId.Length > 0;

    public bool HasTopCard => TopCardId.Length > 0;
}
