using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Hearthpulse.App.Infrastructure;
using Hearthpulse.Core;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Tracking;
using AvaMain = Hearthpulse.UI.Main.MainViewModel;
using AvaOverlay = Hearthpulse.UI.Overlay.OverlayViewModel;
using WpfMain = Hearthpulse.App.Main.MainViewModel;
using WpfOverlay = Hearthpulse.App.Overlay.OverlayViewModel;
using WpfSpec = Hearthpulse.App.Overlay.StreamWindowSpec;
using WpfStreamWindow = Hearthpulse.App.Overlay.StreamWindow;
using WpfWindow = Hearthpulse.App.Main.MainWindow;

namespace Hearthpulse.Presentation.Tests;

/// <summary>
/// The OBS capture windows and streamer mode (docs/streaming.md) in both compiled variants of the shared model, and the WPF
/// window and Settings → Stream loading with the theme (resources resolve only at run time).
/// </summary>
public sealed class StreamWindowContractTests
{
    private static HistoryStore History() => new(Path.Combine(AppPaths.Root, Guid.NewGuid().ToString("N") + ".db"));

    private static LiveSnapshot Game(string type) => LiveSnapshot.Empty with { Scene = "GAMEPLAY", HearthstoneRunning = true, InGame = true, GameType = type };

    [Fact]
    public void Both_uis_give_the_windows_the_same_states_and_hide_the_same_header()
    {
        var snapshots = new[]
        {
            LiveSnapshot.Empty,
            Game("GT_RANKED") with { Constructed = StreamScene.Constructed(CardDb.Empty) },
            Game("GT_BATTLEGROUNDS") with { Battlegrounds = StreamScene.Lobby(CardDb.Empty) },
            Game("GT_BATTLEGROUNDS") with { Battlegrounds = CombatOddsScene.Synthetic() },
        };
        foreach (var streamer in new[] { false, true })
        {
            var settings = new AppSettings { StreamerMode = streamer, OverlayEnabled = false, StreamDeckWindow = true, StreamLobbyWindow = true };
            foreach (var snapshot in snapshots)
            {
                var wpf = new WpfOverlay();
                var ava = new AvaOverlay();
                wpf.Update(snapshot, CardDb.Empty, settings, null, null);
                ava.Update(snapshot, CardDb.Empty, settings, null, null);
                Assert.Equal(
                    (wpf.DeckWindowState.ToString(), wpf.LobbyWindowState.ToString(), wpf.OddsWindowState.ToString(), wpf.OpponentTitle, wpf.OpponentSubtitle, wpf.Deck.Count, wpf.LobbyRows.Count),
                    (ava.DeckWindowState.ToString(), ava.LobbyWindowState.ToString(), ava.OddsWindowState.ToString(), ava.OpponentTitle, ava.OpponentSubtitle, ava.Deck.Count, ava.LobbyRows.Count));
                if (streamer)
                {
                    Assert.DoesNotContain(StreamScene.OpponentBattleTag, wpf.OpponentTitle, StringComparison.Ordinal);
                }
            }
        }
    }

    [Fact]
    public void Both_main_models_switch_the_stream_settings_alike()
    {
        Loc.Instance.SetLanguage(Loc.English);
        var wpf = new WpfMain(History(), new AppSettings(), TestApp.Data);
        var ava = new AvaMain(History(), new AppSettings());
        var told = (Wpf: 0, Ava: 0);
        wpf.StreamSettingsChanged += () => told.Wpf++;
        ava.StreamSettingsChanged += () => told.Ava++;

        foreach (var (w, a) in new (Action, Action)[]
                 {
                     (() => wpf.StreamerMode = true, () => ava.StreamerMode = true),
                     (() => wpf.StreamOddsWindow = true, () => ava.StreamOddsWindow = true),
                     (() => wpf.StreamBackgroundMagenta = true, () => ava.StreamBackgroundMagenta = true),
                     (() => wpf.StreamWindowsOnTop = true, () => ava.StreamWindowsOnTop = true),
                     // The same again is no change; an unknown background is refused.
                     (() => wpf.StreamerMode = true, () => ava.StreamerMode = true),
                     (() => wpf.SetStreamBackground("Blue"), () => ava.SetStreamBackground("Blue")),
                 })
        {
            w();
            a();
        }

        Assert.Equal(4, told.Wpf);
        Assert.Equal(4, told.Ava);
        Assert.Equal(wpf.Settings.ToJson(), ava.Settings.ToJson());
        Assert.Equal(StreamBackgrounds.Magenta, wpf.Settings.StreamWindowBackground);
        Assert.Equal(wpf.StreamKeyNote, ava.StreamKeyNote);
        Assert.Equal(Loc.F("Stream.Help.Key", Loc.T("Stream.Background.Magenta")), wpf.StreamKeyNote);
        Assert.Equal(Loc.T("Stream.Mode.On"), wpf.StreamerModeTip);

        wpf.SetStreamBackground(StreamBackgrounds.Theme);
        Assert.Equal(string.Empty, wpf.StreamKeyNote);
    }

    [Fact]
    public void Wpf_stream_windows_and_the_stream_tab_load_with_the_theme() => OnSta(Check);

    private static void Check()
    {
        Loc.Instance.SetLanguage(Loc.English);
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Clear();
        app.Resources.MergedDictionaries.Add(ThemeEngine.Build(ThemePalette.Resolve(ThemePalette.DefaultName, 0, 0, 1)));

        // The three windows: title for OBS, smallest size, the panel / background / tile by the model's state.
        var overlay = new WpfOverlay();
        var settings = new AppSettings { OverlayEnabled = false, StreamDeckWindow = true, StreamWindowBackground = StreamBackgrounds.Green, StreamWindowsOnTop = true };
        foreach (var spec in WpfSpec.All)
        {
            var window = new WpfStreamWindow(overlay, settings, spec);
            Assert.Equal("Hearthpulse — " + Loc.T(spec.NameKey), window.Title);
            Assert.Equal(spec.MinWidth, window.MinWidth);
            Assert.True(window.Topmost);
            Assert.False(window.ShowActivated);
            Assert.True(window.ShowInTaskbar);
            Assert.Equal(Color.FromRgb(0x00, 0xFF, 0x00), Assert.IsType<SolidColorBrush>(window.Background).Color);
            Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("IdleTile")).Visibility);
            window.CloseForReal();
        }

        var deck = new WpfStreamWindow(overlay, settings, WpfSpec.Deck);
        var root = (FrameworkElement)deck.Content;
        overlay.Update(Game("GT_RANKED") with { Constructed = StreamScene.Constructed(CardDb.Empty) }, CardDb.Empty, settings, null, null);
        Layout(root, WpfSpec.Deck.Width, WpfSpec.Deck.Height);
        Assert.Equal(Visibility.Visible, ((FrameworkElement)deck.FindName("Scaler")).Visibility);
        Assert.Equal(Visibility.Collapsed, ((FrameworkElement)deck.FindName("IdleTile")).Visibility);
        // The overlay's own deck panel with a row per card, scaled to the window's width (the deck fits its height).
        var panel = Assert.Single(Descendants<Hearthpulse.App.Overlay.DeckPanel>(root));
        Assert.Equal(10, Descendants<ItemsControl>(panel).Single(i => i.ItemsSource is not null && i.Items.Count > 0).Items.Count);
        Assert.InRange(panel.ActualWidth, 251, 253);
        settings.StreamWindowBackground = StreamBackgrounds.Theme;
        deck.ApplyLook();
        Assert.Same(app.FindResource("Bg"), deck.Background);
        overlay.Update(Game("GT_RANKED") with { Constructed = StreamScene.Constructed(CardDb.Empty) with { DeckKnown = false } }, CardDb.Empty, settings, null, null);
        Assert.Equal(Visibility.Collapsed, ((FrameworkElement)deck.FindName("Scaler")).Visibility);
        Assert.Equal(Visibility.Collapsed, ((FrameworkElement)deck.FindName("IdleTile")).Visibility);
        deck.CloseForReal();

        // Settings → Stream and the live bar's streamer button: one setting, both follow, the host told.
        var model = new WpfMain(History(), new AppSettings(), TestApp.Data);
        var main = new WpfWindow(model);
        var told = 0;
        model.StreamSettingsChanged += () => told++;
        main.ShowPage("Settings");
        main.ShowSettingsTab("Stream");
        Assert.Equal("Stream", model.Settings.SettingsTab);
        var page = (FrameworkElement)main.Content;
        Layout(page, 1260, 840);
        Assert.Equal(Visibility.Visible, ((FrameworkElement)main.FindName("SettingsStream")).Visibility);
        var pageSwitch = (CheckBox)main.FindName("StreamerModeSetting");
        var button = (ToggleButton)main.FindName("StreamerModeButton");
        Assert.True(button.ActualWidth > 0);
        button.IsChecked = true;
        Assert.True(model.Settings.StreamerMode);
        Assert.True(pageSwitch.IsChecked);
        Assert.Equal(1, told);
        Assert.Equal(Loc.T("Stream.Mode.On"), button.ToolTip);
        ((RadioButton)main.FindName("StreamBackgroundGreenOption")).IsChecked = true;
        Assert.Equal(StreamBackgrounds.Green, model.Settings.StreamWindowBackground);
        Assert.Equal(2, told);
        Assert.False(((RadioButton)main.FindName("StreamBackgroundThemeOption")).IsChecked);
        main.CloseForReal();
    }

    private static void Layout(FrameworkElement root, double width, double height)
    {
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        // Bindings attach at DataBind priority: let them, then lay out again.
        var frame = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        root.UpdateLayout();
    }

    private static void OnSta(Action check)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                check();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new InvalidOperationException("Stream window check failed", failure);
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found)
            {
                yield return found;
            }

            foreach (var nested in Descendants<T>(child))
            {
                yield return nested;
            }
        }
    }
}
