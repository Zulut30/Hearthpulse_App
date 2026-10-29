using System.Globalization;
using Hearthpulse.Core;
using Hearthpulse.Core.Tracking;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Overlay;
#else
namespace Hearthpulse.UI.Overlay;
#endif

/// <summary>
/// Badges beside the Battlegrounds leaderboard tiles: every opponent's tavern tier and, once a fight showed their
/// board, its main tribe and count. They come from <see cref="LobbyRows"/>, so they say exactly what the lobby panels
/// say; the view places them with <see cref="ArrangeLeaderboard"/> on the calibrated tiles. The player's own tile has
/// none: the game shows the player their own tier and board.
/// </summary>
public sealed partial class OverlayViewModel
{
    private bool _showBgBadges;
    private IReadOnlyList<BgBadge> _bgBadges = [];
    private bool _bgBadgesOn;
    private double _surfaceWidth;
    private double _surfaceHeight;
    private AppSettings? _leaderboardSettings;
    private BadgeInputs _badgeInputs;

    public bool ShowBgBadges { get => _showBgBadges; private set => Set(ref _showBgBadges, value); }

    public IReadOnlyList<BgBadge> BgBadges { get => _bgBadges; private set => Set(ref _bgBadges, value); }

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
            _leaderboardSettings?.LeaderboardWidth, IsDuos, _bgBadgesOn, _fingerprints.GetValueOrDefault(nameof(LobbyRows)));
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
                // No tier yet (not seen in a live game) would be an empty shield: no badge until there is one.
                if (row.You || row.Place is < 1 or > 8 || row.Tier < 1)
                {
                    continue;
                }

                var (top, height) = LeaderboardGeometry.Tile(row.Place - 1, _surfaceHeight, settings, IsDuos);
                badges.Add(new BgBadge(row.Place, left, top, height, row.Tier, row.Tribe, row.TribeCount, row.Dead));
            }
        }

        ShowBgBadges = badges.Count > 0;
        Replace(nameof(BgBadges), badges, Print, () => BgBadges = badges);

        static string Print(BgBadge b) => string.Create(CultureInfo.InvariantCulture,
            $"{b.Place}:{b.Left:0.#}:{b.Top:0.#}:{b.Height:0.#}:{b.Tier}:{b.Tribe?.Key}:{b.Tribe?.Glyph}:{b.TribeCount}:{b.Dead}");
    }

    /// <summary>Everything the badges are built from; <c>Rows</c> is the fingerprint of <see cref="LobbyRows"/>.</summary>
    private readonly record struct BadgeInputs(double Width, double Height, double? Top, double? Slot, double? TileWidth, bool Duos, bool On, string? Rows);
}
