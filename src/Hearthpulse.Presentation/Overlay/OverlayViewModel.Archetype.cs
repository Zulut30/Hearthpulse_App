#if HEARTHPULSE_WPF
using Hearthpulse.App.Infrastructure;
#else
using Hearthpulse.UI.Infrastructure;
#endif
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

/// <summary>The "opponent-archetype" panel: the opponent's likely archetype and the cards it may still hold.</summary>
public sealed partial class OverlayViewModel
{
    private bool _showArchetype;
    private string _archetypeName = string.Empty;
    private string _archetypeChance = string.Empty;
    private string _archetypeDetail = string.Empty;
    private string _archetypeNote = string.Empty;
    private IReadOnlyList<ArchetypeRow> _archetypeAlternatives = [];
    private IReadOnlyList<LikelyTile> _likelyCards = [];
    private RelayCommand? _dismissArchetype;
    private ConstructedView? _archetypeView;
    private bool _archetypeDismissed;
    private int _archetypeDismissedTurn;
    private string? _archetypeDismissedOpponent;

    public bool ShowArchetype { get => _showArchetype; private set => Set(ref _showArchetype, value); }

    public string ArchetypeName { get => _archetypeName; private set => Set(ref _archetypeName, value); }

    public string ArchetypeChance { get => _archetypeChance; private set => Set(ref _archetypeChance, value); }

    public string ArchetypeDetail { get => _archetypeDetail; private set => Set(ref _archetypeDetail, value); }

    public string ArchetypeNote { get => _archetypeNote; private set => Set(ref _archetypeNote, value); }

    public IReadOnlyList<ArchetypeRow> ArchetypeAlternatives { get => _archetypeAlternatives; private set => Set(ref _archetypeAlternatives, value); }

    public IReadOnlyList<LikelyTile> LikelyCards { get => _likelyCards; private set => Set(ref _likelyCards, value); }

    /// <summary>Hides the panel for the rest of this game; the next game (or another opponent) shows it again.</summary>
    public RelayCommand DismissArchetype => _dismissArchetype ??= new RelayCommand(() =>
    {
        if (_archetypeView is null)
        {
            return;
        }

        _archetypeDismissed = true;
        _archetypeDismissedTurn = _archetypeView.Turn;
        _archetypeDismissedOpponent = _archetypeView.OpponentName;
        ShowArchetype = false;
    });

    private void UpdateArchetype(ConstructedView? view, AppSettings settings)
    {
        _archetypeView = view;
        // The game ended (no view), a new one started (the turn went back) or the opponent's name changed: show again.
        if (_archetypeDismissed && (view is null || view.Turn < _archetypeDismissedTurn
            || (!string.IsNullOrEmpty(_archetypeDismissedOpponent) && !string.IsNullOrEmpty(view.OpponentName) && view.OpponentName != _archetypeDismissedOpponent)))
        {
            _archetypeDismissed = false;
        }
        else if (_archetypeDismissed)
        {
            _archetypeDismissedTurn = Math.Max(_archetypeDismissedTurn, view!.Turn);
            if (string.IsNullOrEmpty(_archetypeDismissedOpponent))
            {
                _archetypeDismissedOpponent = view.OpponentName;
            }
        }

        var odds = settings.ShowOpponentArchetype && !_archetypeDismissed ? view?.OpponentArchetype : null;
        ShowArchetype = odds is not null;
        if (odds is null)
        {
            return;
        }

        // Archetype names come from the meta in English; the label follows the interface language.
        var best = odds.Best;
        ArchetypeName = ArchetypeTranslations.Current.Display(best.Key);
        ArchetypeChance = SharePercent(best.Chance);
        ArchetypeDetail = Loc.F("Widgets.Archetype.WinRate", best.WinRate);
        ArchetypeNote = Loc.P("Widgets.Archetype.Seen", odds.SeenCards, odds.Source);
        Replace(nameof(ArchetypeAlternatives), odds.Archetypes, a => $"{a.Key}{a.Chance:0.00}", () => ArchetypeAlternatives = odds.Archetypes
            .Skip(1)
            .Select(a => new ArchetypeRow(ArchetypeTranslations.Current.Display(a.Key), SharePercent(a.Chance), Math.Clamp(a.Chance, 0.02, 1)))
            .ToList());
        Replace(nameof(LikelyCards), odds.Cards, c => $"{c.CardId}{c.Chance:0.00}", () => LikelyCards = odds.Cards
            .Select(c => new LikelyTile(c.CardId, c.Name, c.Cost, SharePercent(c.Chance)))
            .ToList());
    }

    /// <summary>"62%"; a near-certain share reads "&gt;99%": the odds are an estimate, never a certainty.</summary>
    private static string SharePercent(double share) => share >= 0.995 ? ">99%" : (share * 100).ToString("0", CultureInfo.CurrentCulture) + "%";
}
