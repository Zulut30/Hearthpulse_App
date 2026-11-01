using System.Globalization;
using Hearthpulse.Core;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Tracking;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Overlay;
#else
namespace Hearthpulse.UI.Overlay;
#endif

/// <summary>One row of the "Likely deck" panel: "2", "1/2", "★" or nothing; <see cref="Done"/> dims it (all played).</summary>
public sealed record LikelyDeckTile(string CardId, string Name, int Cost, string Rarity, string Mark, bool Done);

/// <summary>
/// The "Likely deck" panel (<c>docs/opponent-deck.md</c>): the opponent's likely meta list, the copies it played face up
/// from its starting deck marked, and the played cards the list does not hold. Public meta and face-up plays only.
/// </summary>
public sealed partial class OverlayViewModel
{
    /// <summary>The most rows "Not in the list" shows; the rest is "+N more".</summary>
    internal const int LikelyOffListRows = 4;

    private bool _showLikelyDeck;
    private string _likelyDeckKey = string.Empty;
    private string _likelyDeckName = string.Empty;
    private string _likelyDeckChance = string.Empty;
    private string _likelyDeckNote = string.Empty;
    private string _likelyDeckSource = string.Empty;
    private string _likelyDeckCoreNote = string.Empty;
    private IReadOnlyList<LikelyDeckTile> _likelyDeckRows = [];
    private string _likelyDeckOffTitle = string.Empty;
    private IReadOnlyList<LikelyDeckTile> _likelyDeckOffList = [];
    private string _likelyDeckOffMore = string.Empty;

    public bool ShowLikelyDeck { get => _showLikelyDeck; private set => Set(ref _showLikelyDeck, value); }

    /// <summary>The archetype's name in the interface language.</summary>
    public string LikelyDeckName { get => _likelyDeckName; private set => Set(ref _likelyDeckName, value); }

    /// <summary>"62 %", as the archetype panel.</summary>
    public string LikelyDeckChance { get => _likelyDeckChance; private set => Set(ref _likelyDeckChance, value); }

    /// <summary>The chance's tooltip: how many cards the guess rests on, and the source.</summary>
    public string LikelyDeckNote { get => _likelyDeckNote; private set => Set(ref _likelyDeckNote, value); }

    /// <summary>"Hearthpulse data · Legend · 7 days": the meta page's rank and period.</summary>
    public string LikelyDeckSource { get => _likelyDeckSource; private set => Set(ref _likelyDeckSource, value); }

    /// <summary>Set when the archetype has no lists and the rows are its core cards; else empty.</summary>
    public string LikelyDeckCoreNote { get => _likelyDeckCoreNote; private set => Set(ref _likelyDeckCoreNote, value); }

    public IReadOnlyList<LikelyDeckTile> LikelyDeckRows { get => _likelyDeckRows; private set => Set(ref _likelyDeckRows, value); }

    /// <summary>"Not in the list · 2", or empty without such cards.</summary>
    public string LikelyDeckOffTitle { get => _likelyDeckOffTitle; private set => Set(ref _likelyDeckOffTitle, value); }

    public IReadOnlyList<LikelyDeckTile> LikelyDeckOffList { get => _likelyDeckOffList; private set => Set(ref _likelyDeckOffList, value); }

    public string LikelyDeckOffMore { get => _likelyDeckOffMore; private set => Set(ref _likelyDeckOffMore, value); }

    /// <summary>Rebuilt only when the list, a played copy or the language changed: a fresh view comes on every publish.</summary>
    private void UpdateLikelyDeck(ConstructedView? view, AppSettings settings)
    {
        var deck = settings.ShowLikelyDeck ? view?.OpponentDeck : null;
        ShowLikelyDeck = deck is not null;
        var key = deck is null ? string.Empty
            : $"{Loc.Instance.Language}|{deck.ArchetypeKey}|{deck.Chance:0.000}|{deck.Rank}|{deck.Period}|{deck.Games}|{view!.OpponentArchetype?.SeenCards}|"
              + string.Join(',', deck.Rows.Concat(deck.OffList).Select(r => $"{r.CardId}:{r.Copies}:{r.Played}"));
        if (key == _likelyDeckKey)
        {
            return;
        }

        _likelyDeckKey = key;
        LikelyDeckName = deck is null ? string.Empty : ArchetypeTranslations.Current.Display(deck.ArchetypeKey);
        LikelyDeckChance = deck is null ? string.Empty : SharePercent(deck.Chance);
        LikelyDeckNote = deck is null || view!.OpponentArchetype is not { } odds ? string.Empty : Loc.P("Widgets.Archetype.Seen", odds.SeenCards, odds.Source);
        LikelyDeckSource = deck is null ? string.Empty
            : Loc.F("OpponentDeck.Source", ConstructedMeta.Label(ConstructedMeta.Ranks, deck.Rank), ConstructedMeta.Label(ConstructedMeta.Periods, deck.Period));
        LikelyDeckCoreNote = deck is { CoreOnly: true } ? Loc.T("OpponentDeck.CoreOnly") : string.Empty;
        LikelyDeckRows = deck?.Rows.Select(r => new LikelyDeckTile(r.CardId, r.Name, r.Cost, r.Rarity, Mark(r), r.AllPlayed)).ToList() ?? [];
        LikelyDeckOffTitle = deck is { OffList.Count: > 0 } ? Loc.F("OpponentDeck.OffList", deck.OffList.Count.ToString(CultureInfo.CurrentCulture)) : string.Empty;
        LikelyDeckOffList = deck?.OffList.Take(LikelyOffListRows)
            .Select(r => new LikelyDeckTile(r.CardId, r.Name, r.Cost, r.Rarity, r.Played > 1 ? "×" + r.Played.ToString(CultureInfo.CurrentCulture) : string.Empty, false))
            .ToList() ?? [];
        LikelyDeckOffMore = deck is { OffList.Count: > LikelyOffListRows } ? Loc.F("OpponentDeck.More", deck.OffList.Count - LikelyOffListRows) : string.Empty;

        // "2" untouched, "1/2" and "2/2" once played; one copy: nothing, or "★" for a legendary, as the deck tracker.
        static string Mark(LikelyDeckRow row) =>
            row.Played > 0 && (row.Copies > 1 || row.Played > row.Copies)
                ? $"{row.Played.ToString(CultureInfo.CurrentCulture)}/{row.Copies.ToString(CultureInfo.CurrentCulture)}"
                : row.Copies > 1 ? row.Copies.ToString(CultureInfo.CurrentCulture)
                : row.Rarity == "LEGENDARY" ? "★"
                : string.Empty;
    }
}
