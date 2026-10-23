using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Hearthlens.Core;
using Hearthlens.Core.Battlegrounds;
using Hearthlens.Core.Hearthstone;
using Hearthlens.Core.Power;
using Hearthlens.Core.Tracking;

namespace Hearthlens.App;

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

            _overlayViewModel.Update(LiveSnapshot.Empty, _cards, _settings, null, null);
            _overlayViewModel.LayoutEditing = true;
            await _overlay.RenderToFileAsync(Path.Combine(folder, "overlay-layout.png"), 1920, 1080, 0);
        }
        finally
        {
            Quit();
        }
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
