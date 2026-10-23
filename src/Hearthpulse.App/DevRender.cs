using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Hearthpulse.Core;
using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Hearthstone;
using Hearthpulse.Core.Power;
using Hearthpulse.Core.Tracking;
using Hearthpulse.Core.Tracking.Counters;

namespace Hearthpulse.App;

/// <summary>
/// <c>--render-ui &lt;folder&gt;</c>: renders the dashboard pages and the overlay to PNGs off-screen,
/// using the player's own history and their latest kept Battlegrounds log. Nothing is shown on screen.
/// </summary>
public partial class App
{
    private void PrepareOffscreenRender()
    {
        _overlay.Offscreen = true;
        _overlay.Left = -20000;
        _overlay.Top = -20000;
        _overlay.Show();
        _main.WindowStartupLocation = WindowStartupLocation.Manual;
        _main.Left = -20000;
        _main.Top = -20000;
        _main.ShowInTaskbar = false;
        _main.ShowActivated = false;
        _main.Show();
    }

    private async Task RenderUiAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        await LoadMetaAsync();
        await LoadConstructedMetaAsync();
        await _mainViewModel.RefreshAsync();
        _session.Cards = _cards;
        _mainViewModel.Busy = string.Empty;
        try
        {
            foreach (var page in new[] { "Overview", "Battlegrounds", "Constructed", "History", "Settings" })
            {
                _main.ShowPage(page);
                await Task.Delay(2500);
                _main.UpdateLayout();
                Save(_main, Path.Combine(folder, $"main-{page.ToLowerInvariant()}.png"));
            }

            // The Battlegrounds counters list sits far down the settings page.
            _main.ShowPage("Settings");
            if (_main.FindName("BgCountersHeading") is FrameworkElement heading)
            {
                await Task.Delay(500);
                heading.BringIntoView(new Rect(0, 0, heading.ActualWidth, 640));
                await Task.Delay(500);
                _main.UpdateLayout();
                Save(_main, Path.Combine(folder, "main-settings-counters.png"));
            }

            _main.ShowPage("Constructed");
            foreach (var tab in new[] { "Tiers", "Top" })
            {
                _main.ShowConstructedTab(tab);
                await Task.Delay(2500);
                _main.UpdateLayout();
                Save(_main, Path.Combine(folder, $"main-constructed-{tab.ToLowerInvariant()}.png"));
            }

            _main.ShowConstructedTab("Decks");

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

            await RenderCountersAsync(folder);

            _overlayViewModel.Update(LiveSnapshot.Empty, _cards, _settings, null, null);
            _overlayViewModel.LayoutEditing = true;
            await _overlay.RenderToFileAsync(Path.Combine(folder, "overlay-layout.png"), 1920, 1080, 0);
        }
        finally
        {
            Quit();
        }
    }

    /// <summary>
    /// <c>overlay-counters*.png</c>: the Constructed counters of the latest kept game (or a made-up one) on the overlay,
    /// the panel alone at 2x, the panel with one counter opened by a click, and that counter's tooltip.
    /// </summary>
    private async Task RenderCountersAsync(string folder)
    {
        var installDir = _installDir;
        var view = (installDir is null ? null : await Task.Run(() => CounterScene.FromLatestLog(installDir, _cards))) ?? CounterScene.Synthetic(_cards);
        var snapshot = LiveSnapshot.Empty with { Scene = "GAMEPLAY", HearthstoneRunning = true, InGame = true, GameType = "GT_RANKED", Constructed = view };
        _overlayViewModel.Update(snapshot, _cards, _settings, null, null);
        await _overlay.RenderToFileAsync(Path.Combine(folder, "overlay-counters.png"), 1920, 1080, 0);
        if (_overlay.FindName("CountersCard") is not FrameworkElement card)
        {
            return;
        }

        SaveScaled(card, Path.Combine(folder, "overlay-counters-panel.png"));
        var chips = _overlayViewModel.OpponentCounters.Concat(_overlayViewModel.MyCounters).ToList();
        var shown = chips.FirstOrDefault(c => c.Id == "cthun") ?? chips.FirstOrDefault();
        if (shown is null)
        {
            return;
        }

        if (_overlay.TryFindResource("CounterTipTemplate") is DataTemplate tipTemplate)
        {
            var host = new System.Windows.Controls.ContentControl { Content = shown, ContentTemplate = tipTemplate };
            host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            host.Arrange(new Rect(host.DesiredSize));
            for (var i = 0; i < 8; i++)
            {
                await Task.Delay(500);
                host.UpdateLayout();
            }

            SaveScaled(host, Path.Combine(folder, "overlay-counters-tooltip.png"));
        }

        _overlayViewModel.ToggleCounter.Execute(shown.Id);
        for (var i = 0; i < 8; i++)
        {
            await Task.Delay(500);
            _overlay.UpdateLayout();
        }

        SaveScaled(card, Path.Combine(folder, "overlay-counters-open.png"));
        _overlayViewModel.ToggleCounter.Execute(shown.Id);
    }

    /// <summary>An overlay element alone at twice its size, on the dark stand-in for the game.</summary>
    private static void SaveScaled(FrameworkElement element, string path)
    {
        const double scale = 2;
        const double pad = 12;
        var width = (int)Math.Ceiling((element.ActualWidth + 2 * pad) * scale);
        var height = (int)Math.Ceiling((element.ActualHeight + 2 * pad) * scale);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x24, 0x19, 0x10)), null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new VisualBrush(element) { Stretch = Stretch.Fill }, null,
                new Rect(pad * scale, pad * scale, element.ActualWidth * scale, element.ActualHeight * scale));
        }

        var bitmap = new RenderTargetBitmap(Math.Max(1, width), Math.Max(1, height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void Save(FrameworkElement element, string path)
    {
        var width = (int)Math.Ceiling(element.ActualWidth);
        var height = (int)Math.Ceiling(element.ActualHeight);
        var bitmap = new RenderTargetBitmap(Math.Max(1, width), Math.Max(1, height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private BattlegroundsView? ReplayLatestBattlegrounds(Func<BattlegroundsView, bool> when)
    {
        if (_installDir is null)
        {
            return null;
        }

        var log = HearthstoneInstall.AllLogSessions(_installDir).Reverse()
            .SelectMany(d => new[] { Path.Combine(d, "Power.log"), Path.Combine(d, "Power_old.log") })
            .Where(File.Exists)
            .FirstOrDefault(f => ReadShared(f).Take(30_000).Any(x => x.Contains("GameType=GT_BATTLEGROUNDS", StringComparison.Ordinal)));
        if (log is null)
        {
            return null;
        }

        var parser = new PowerParser(DateOnly.FromDateTime(File.GetLastWriteTime(log)));
        var tracker = new BattlegroundsTracker { Cards = _cards };
        parser.AddListener(tracker);
        parser.AddListener(tracker.Counters);
        var lines = 0;
        foreach (var line in ReadShared(log))
        {
            parser.Feed(line);
            if (++lines % 200 != 0 || parser.Game is not { IsBattlegrounds: true, LocalPlayerId: > 0 } game)
            {
                continue;
            }

            parser.Flush();
            var view = tracker.BuildView(game);
            if (when(view))
            {
                return view;
            }
        }

        return null;
    }

    /// <summary>Reads a log line by line while Hearthstone may still be writing it.</summary>
    private static IEnumerable<string> ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }
}
