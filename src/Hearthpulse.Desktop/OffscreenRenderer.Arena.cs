using Avalonia.Controls;
using Hearthpulse.Core;
using Hearthpulse.Core.Arena;
using Hearthpulse.Core.Tracking;
using Hearthpulse.UI;
using Hearthpulse.UI.Main;
using Hearthpulse.UI.Overlay;

namespace Hearthpulse.Desktop;

/// <summary>
/// Render-check scenes of the Arena draft helper: the hero pick and a card pick over a made-up draft from the real
/// scores (<see cref="ArenaDraftScene"/>), at the default and the largest overlay scale, and the Arena page with the
/// deck score and class win rates.
/// </summary>
internal static partial class OffscreenRenderer
{
    private static async Task RenderArenaPageAsync(Window main, MainView view, AppHost host, string folder)
    {
        await host.LoadArenaMetaAsync();
        // Two made-up runs, one with rewards on its chip (docs/arena-rewards.md); the data folder is a copy (render.py).
        foreach (var game in ArenaRunsScene.Games())
        {
            host.History.Add(game);
        }

        host.History.SetArenaRewards(ArenaRunsScene.FinishedKey, ArenaRunsScene.FinishedRewards);
        main.Height = 2000;
        view.ShowPage("Arena");
        await Task.Delay(2500);
        await Save(main, Path.Combine(folder, "main-arena-deck.png"));
        // Its rewards editor open, then closed unsaved.
        host.Main.EditArenaRewards.Execute(ArenaRunsScene.FinishedKey);
        await Save(main, Path.Combine(folder, "main-arena-rewards.png"));
        host.Main.ArenaRuns.FirstOrDefault(r => r.Edit is not null)?.Edit?.Cancel.Execute(null);
        main.Height = 840;
    }

    private static async Task RenderArenaOverlayAsync(AppHost host, AppSettings settings, OverlayView overlayView, Window overlay, string folder)
    {
        var meta = host.ArenaMeta ?? ArenaMeta.Empty;
        var scenes = new (string Name, ArenaDraftView View, double Scale)[]
        {
            ("overlay-arena-hero", ArenaDraftScene.HeroPick(meta, host.Cards), settings.OverlayScale),
            ("overlay-arena-pick", ArenaDraftScene.CardPick(meta, host.Cards), settings.OverlayScale),
            ("overlay-arena-pick-scale160", ArenaDraftScene.CardPick(meta, host.Cards), 1.6),
            ("overlay-arena-heropower", ArenaDraftScene.HeroPowerPick(meta, host.Cards), settings.OverlayScale),
            ("overlay-arena-package", ArenaDraftScene.PackagePick(meta, host.Cards), settings.OverlayScale),
        };
        var (scale, shown) = (settings.OverlayScale, settings.ShowArenaDraft);
        // The helper is a beta that is off by default; the scenes show it on (the settings file is not saved here).
        settings.ShowArenaDraft = true;
        foreach (var (name, view, sceneScale) in scenes)
        {
            settings.OverlayScale = sceneScale;
            host.Overlay.Update(LiveSnapshot.Empty with { Scene = "DRAFT", HearthstoneRunning = true, ArenaDraft = view }, host.Cards, settings, null, null);
            overlayView.Fit(1920, 1080);
            await Save(overlay, Path.Combine(folder, name + ".png"));
        }

        (settings.OverlayScale, settings.ShowArenaDraft) = (scale, shown);
        host.Overlay.Update(LiveSnapshot.Empty, host.Cards, settings, null, null);
        overlayView.Fit(1920, 1080);
    }
}
