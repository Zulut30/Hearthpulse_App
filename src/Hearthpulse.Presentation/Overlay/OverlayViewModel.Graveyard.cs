using System.Globalization;
using Hearthpulse.Core;
using Hearthpulse.Core.CardPool;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Tracking;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Overlay;
#else
namespace Hearthpulse.UI.Overlay;
#endif

/// <summary>
/// The Graveyard panel (<c>docs/graveyard.md</c>): the minions that died this game, the player's and the opponent's,
/// as tracker rows. Deaths on the board only, which both players see.
/// </summary>
public sealed partial class OverlayViewModel
{
    /// <summary>
    /// The most rows a side shows; the rest is "+N". Seven: with both sides full the panel at its default spot stays
    /// above End Turn (<c>docs/graveyard.md</c>).
    /// </summary>
    internal const int GraveyardRows = 7;

    private bool _showGraveyard;
    private string _graveyardKey = string.Empty;
    private string _graveyardLocalTitle = string.Empty;
    private string _graveyardOpponentTitle = string.Empty;
    private IReadOnlyList<PoolListRow> _graveyardLocal = [];
    private IReadOnlyList<PoolListRow> _graveyardOpponent = [];
    private string _graveyardLocalMore = string.Empty;
    private string _graveyardOpponentMore = string.Empty;

    public bool ShowGraveyard { get => _showGraveyard; private set => Set(ref _showGraveyard, value); }

    /// <summary>"Yours · 3".</summary>
    public string GraveyardLocalTitle { get => _graveyardLocalTitle; private set => Set(ref _graveyardLocalTitle, value); }

    public string GraveyardOpponentTitle { get => _graveyardOpponentTitle; private set => Set(ref _graveyardOpponentTitle, value); }

    public IReadOnlyList<PoolListRow> GraveyardLocal { get => _graveyardLocal; private set => Set(ref _graveyardLocal, value); }

    public IReadOnlyList<PoolListRow> GraveyardOpponent { get => _graveyardOpponent; private set => Set(ref _graveyardOpponent, value); }

    /// <summary>"+4 more" under a side's rows, or empty; "None yet" for a side with no deaths.</summary>
    public string GraveyardLocalMore { get => _graveyardLocalMore; private set => Set(ref _graveyardLocalMore, value); }

    public string GraveyardOpponentMore { get => _graveyardOpponentMore; private set => Set(ref _graveyardOpponentMore, value); }

    /// <summary>Rebuilt only when a death or the language changed: a fresh view comes on every publish.</summary>
    private void UpdateGraveyard(ConstructedView? view, AppSettings settings)
    {
        var graveyard = settings.ShowGraveyard ? view?.Graveyard : null;
        ShowGraveyard = graveyard is not null;
        var key = graveyard is null ? string.Empty
            : $"{Loc.Instance.Language}|{Rows(graveyard.Local)}|{Rows(graveyard.Opponent)}";
        if (key == _graveyardKey)
        {
            return;
        }

        _graveyardKey = key;
        GraveyardLocalTitle = graveyard is null ? string.Empty : Loc.F("Related.Graveyard.Mine", graveyard.LocalTotal.ToString(CultureInfo.CurrentCulture));
        GraveyardOpponentTitle = graveyard is null ? string.Empty : Loc.F("Related.Graveyard.Enemy", graveyard.OpponentTotal.ToString(CultureInfo.CurrentCulture));
        GraveyardLocal = RowsOf(graveyard?.Local);
        GraveyardOpponent = RowsOf(graveyard?.Opponent);
        GraveyardLocalMore = More(graveyard?.Local);
        GraveyardOpponentMore = More(graveyard?.Opponent);

        static string Rows(IReadOnlyList<MatchListRow> rows) => string.Join(',', rows.Select(r => $"{r.CardId}:{r.Count}"));

        static IReadOnlyList<PoolListRow> RowsOf(IReadOnlyList<MatchListRow>? rows) =>
            rows?.Take(GraveyardRows)
                .Select(r => new PoolListRow(r.CardId, r.Name, r.Cost, r.Rarity, r.Count > 1 ? "×" + r.Count.ToString(CultureInfo.CurrentCulture) : string.Empty))
                .ToList() ?? [];

        static string More(IReadOnlyList<MatchListRow>? rows) =>
            rows is null ? string.Empty
            : rows.Count == 0 ? Loc.T("Related.Graveyard.Empty")
            : rows.Count > GraveyardRows ? Loc.F("Related.Graveyard.More", rows.Count - GraveyardRows)
            : string.Empty;
    }
}
