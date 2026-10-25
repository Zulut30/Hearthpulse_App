using System.IO;
using System.Windows;
using System.Windows.Controls;
using Hearthpulse.Core;
using Hearthpulse.Core.Arena;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.App;

/// <summary>
/// Render-check scenes of the Arena draft helper: the hero pick and a card pick over a made-up draft from the real
/// scores (<see cref="ArenaDraftScene"/>), at the default and the largest overlay scale, and the Arena page with the
/// deck score and class win rates.
/// </summary>
public partial class App
{
    private async Task RenderArenaAsync(string folder)
    {
        await LoadArenaMetaAsync();
        var meta = _arenaMeta ?? ArenaMeta.Empty;
        var scenes = new (string Name, ArenaDraftView View, double Scale)[]
        {
            ("overlay-arena-hero", ArenaDraftScene.HeroPick(meta, _cards), _settings.OverlayScale),
            ("overlay-arena-pick", ArenaDraftScene.CardPick(meta, _cards), _settings.OverlayScale),
            ("overlay-arena-pick-scale160", ArenaDraftScene.CardPick(meta, _cards), 1.6),
        };
        var (scale, shown) = (_settings.OverlayScale, _settings.ShowArenaDraft);
        // The helper is a beta that is off by default; the scenes show it on (the settings file is not saved here).
        _settings.ShowArenaDraft = true;
        foreach (var (name, view, sceneScale) in scenes)
        {
            try
            {
                _settings.OverlayScale = sceneScale;
                _overlayViewModel.Update(LiveSnapshot.Empty with { Scene = "DRAFT", HearthstoneRunning = true, ArenaDraft = view }, _cards, _settings, null, null);
                await _overlay.RenderToFileAsync(Path.Combine(folder, name + ".png"), 1920, 1080, 0);
            }
            catch (IOException ex)
            {
                Diag.Error($"Render of {name} failed", ex);
            }
        }

        (_settings.OverlayScale, _settings.ShowArenaDraft) = (scale, shown);
        _overlayViewModel.Update(LiveSnapshot.Empty, _cards, _settings, null, null);

        _main.ShowPage("Arena");
        await Task.Delay(2500);
        _main.UpdateLayout();
        if (_main.FindName("PageArena") is ScrollViewer { Content: FrameworkElement content })
        {
            SaveFull(content, Path.Combine(folder, "main-arena-deck.png"));
        }
    }
}
