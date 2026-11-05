using System.IO;
using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Tracking;
using Hearthpulse.Core.Tracking.Counters;

namespace Hearthpulse.App;

/// <summary><c>--render-ui</c>: the Battlegrounds moments on the overlay, and a check of the overlay alone.</summary>
public partial class App
{
    /// <summary>
    /// Render group <c>overlay-opacity</c>: one frame, <c>overlay-opacity.png</c> - the Constructed panels and counters
    /// with the data copy's theme and panel background opacity (R06: text stays opaque at any setting).
    /// </summary>
    private async Task RenderOpacityCheckAsync(string folder)
    {
        var snapshot = LiveSnapshot.Empty with
        {
            Scene = "GAMEPLAY",
            HearthstoneRunning = true,
            InGame = true,
            GameType = "GT_RANKED",
            Constructed = CounterScene.Synthetic(_cards),
        };
        _overlayViewModel.Update(snapshot, _cards, _settings, null, null);
        await _overlay.RenderToFileAsync(Path.Combine(folder, "overlay-opacity.png"), 1920, 1080, 0);
    }

    /// <summary>
    /// Render group <c>overlay</c>: only the overlay - the Battlegrounds moments of the newest kept log, the Constructed
    /// panels and layout mode - not the main window or the other overlay scenes.
    /// </summary>
    private async Task RenderOverlayCheckAsync(string folder)
    {
        await RenderBattlegroundsMomentsAsync(folder);
        await RenderConstructedOverlayAsync(folder);
        _overlayViewModel.Update(LiveSnapshot.Empty, _cards, _settings, null, null);
        _overlayViewModel.LayoutEditing = true;
        await _overlay.RenderToFileAsync(Path.Combine(folder, "overlay-layout.png"), 1920, 1080, 0);
    }

    /// <summary>
    /// <c>overlay-pick</c>, <c>-trinket</c>, <c>-discover</c>, <c>-game</c> (with the third place's board open) and
    /// <c>-bg-counters</c>: moments replayed from the newest kept Battlegrounds log; a moment it never reaches is skipped.
    /// </summary>
    private async Task RenderBattlegroundsMomentsAsync(string folder)
    {
        var rating = _history.RatingHistory().LastOrDefault()?.Rating;
        var moments = new (string Name, Func<BattlegroundsView, bool> When, int Hover)[]
        {
            ("overlay-pick", v => v is { IsHeroSelection: true, HeroOptions.Count: > 1 }, 0),
            ("overlay-trinket", v => v is { IsHeroSelection: false, TrinketOptions.Count: > 1 }, 0),
            ("overlay-discover", v => v is { IsHeroSelection: false, DiscoverOptions.Count: > 1 }, 0),
            ("overlay-game", v => v is { InCombat: false, Turn: >= 6, Shop.Count: >= 3 } && v.Opponents.Count(o => o.LastBoard.Count > 0) >= 3, 3),
            ("overlay-bg-counters", v => v is { IsHeroSelection: false, HeroCardId: not null, InCombat: false, Turn: >= 2, Counters.Count: > 0 }, 0),
        };
        foreach (var (name, when, hover) in moments)
        {
            var view = await Task.Run(() => ReplayLatestBattlegrounds(when));
            if (view is null)
            {
                continue;
            }

            var advisor = new BattlegroundsAdvisor { Meta = _meta };
            var snapshot = LiveSnapshot.Empty with
            {
                Scene = "GAMEPLAY",
                HearthstoneRunning = true,
                InGame = true,
                GameType = "GT_BATTLEGROUNDS",
                Battlegrounds = advisor.Enrich(view, rating, _cards),
                Rating = rating is { } r ? new Core.Memory.BattlegroundsRating(r, 0) : null,
            };
            _overlayViewModel.Update(snapshot, _cards, _settings, null, null);
            await _overlay.RenderToFileAsync(Path.Combine(folder, name + ".png"), 1920, 1080, hover);
        }
    }
}
