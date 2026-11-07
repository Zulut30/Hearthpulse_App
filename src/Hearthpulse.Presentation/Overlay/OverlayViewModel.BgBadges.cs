using System.Globalization;
#if HEARTHPULSE_WPF
using Hearthpulse.App.Infrastructure;
#else
using Hearthpulse.UI.Infrastructure;
#endif
using Hearthpulse.Core;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Tracking;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Overlay;
#else
namespace Hearthpulse.UI.Overlay;
#endif

/// <summary>
/// Labels beside the Battlegrounds leaderboard tiles, in words: every opponent's "Tavern 5" and, once a fight showed
/// their board and a tribe has two minions on it, "Quilboar: 4 of 7". They come from <see cref="LobbyRows"/>, so they
/// say exactly what the lobby panels say; the view places them with <see cref="ArrangeLeaderboard"/> on the calibrated
/// tiles. The player's own tile and the players who are out have none. While the mouse is on any tile the labels hide:
/// the game draws its own popup right of the leaderboard, where they sit.
/// </summary>
public sealed partial class OverlayViewModel
{
    private bool _showBgBadges;
    private SyncList<BgBadge> _bgBadges = new();
    private bool _bgBadgesOn;
    private double _surfaceWidth;
    private double _surfaceHeight;
    private AppSettings? _leaderboardSettings;
    private BadgeInputs _badgeInputs;
    private bool _leaderboardHovered;
    private bool _bgBadgesPlaced;

    public bool ShowBgBadges { get => _showBgBadges; private set => Set(ref _showBgBadges, value); }

    /// <summary>
    /// Changed in place (<see cref="Sync{T}"/>): a fight or a tavern turn changes one or two badges, and a new list made
    /// the overlay build all seven again (audit, stage 10).
    /// </summary>
    public IReadOnlyList<BgBadge> BgBadges => _bgBadges;

    /// <summary>
    /// The view laid the surface out at <paramref name="surfaceWidth"/> x <paramref name="surfaceHeight"/> overlay units
    /// with the leaderboard calibration in <paramref name="settings"/>. Called every frame; the badges move only when
    /// the size, the calibration or the mode changed.
    /// </summary>
    public void ArrangeLeaderboard(double surfaceWidth, double surfaceHeight, AppSettings settings)
    {
        _surfaceWidth = surfaceWidth;
        _surfaceHeight = surfaceHeight;
        _leaderboardSettings = settings;
        RebuildBgBadges();
    }

    private void UpdateBgBadges(BattlegroundsView? view, AppSettings settings)
    {
        _bgBadgesOn = view is { IsHeroSelection: false } && settings.ShowBgLeaderboardBadges;
        RebuildBgBadges();
    }

    private void RebuildBgBadges()
    {
        // Off (outside Battlegrounds, the hero pick, the setting) with nothing shown: every frame ends here.
        if (!_bgBadgesOn && _bgBadges.Count == 0)
        {
            return;
        }

        // Unchanged size, calibration, mode and lobby rows (their fingerprint): the badges stay as they are.
        var inputs = new BadgeInputs(_surfaceWidth, _surfaceHeight, _leaderboardSettings?.LeaderboardTop, _leaderboardSettings?.LeaderboardSlotHeight,
            _leaderboardSettings?.LeaderboardWidth, IsDuos, _bgBadgesOn, _fingerprints.GetValueOrDefault(nameof(LobbyRows)), Loc.Instance.Language);
        if (inputs == _badgeInputs)
        {
            return;
        }

        _badgeInputs = inputs;
        PlaceBgBadges();
    }

    /// <summary>Builds the badges; kept out of <see cref="RebuildBgBadges"/> so its closure is not allocated every frame.</summary>
    private void PlaceBgBadges()
    {
        var badges = new List<BgBadge>();
        if (_bgBadgesOn && _leaderboardSettings is { } settings && _surfaceHeight > 0)
        {
            var left = LeaderboardGeometry.Beside(_surfaceWidth, _surfaceHeight, settings);
            foreach (var row in LobbyRows)
            {
                // No tier yet (not seen in a live game) would be an empty label: none until there is one.
                if (row.You || row.Dead || row.Place is < 1 or > 8 || row.Tier < 1)
                {
                    continue;
                }

                var (top, height) = LeaderboardGeometry.Tile(row.Place - 1, _surfaceHeight, settings, IsDuos);
                var tribe = row is { Tribe: { } t, TribeCount: >= 2 }
                    ? Loc.F("Overlay.LeaderboardBadge.TribeCount", t.Plural, row.TribeCount, row.BoardCount)
                    : string.Empty;
                badges.Add(new BgBadge(row.Place, left, top, height, row.Tier, tribe));
            }
        }

        _bgBadgesPlaced = badges.Count > 0;
        ShowBgBadges = _bgBadgesPlaced && !_leaderboardHovered;
        Replace(nameof(BgBadges), badges, Print, () => Sync(ref _bgBadges, nameof(BgBadges), badges));

        static string Print(BgBadge b) => string.Create(CultureInfo.InvariantCulture,
            $"{b.Place}:{b.Left:0.#}:{b.Top:0.#}:{b.Height:0.#}:{b.Tier}:{b.Tribe}");
    }

    /// <summary>The mouse is on a leaderboard tile (any, the player's own too) or left it: the labels hide or come back.</summary>
    private void HideBgBadgesWhileHovered(bool hovered)
    {
        _leaderboardHovered = hovered;
        ShowBgBadges = _bgBadgesPlaced && !hovered;
    }

    /// <summary>Everything the badges are built from; <c>Rows</c> is the fingerprint of <see cref="LobbyRows"/>, <c>Language</c> the words'.</summary>
    private readonly record struct BadgeInputs(double Width, double Height, double? Top, double? Slot, double? TileWidth, bool Duos, bool On, string? Rows, string Language);
}
