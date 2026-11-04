using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Hearthpulse.Core;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Tracking;
using Hearthpulse.UI.Main;
using Hearthpulse.UI.Overlay;

namespace Hearthpulse.Tests;

public sealed partial class LocBindingTests
{
    /// <summary>
    /// Settings → Stream, the live bar's streamer button and the main model stay in step (docs/streaming.md): each change
    /// tells the host once, a window closed by hand unticks its switch, and the key background adds its line to the help.
    /// </summary>
    [Fact]
    public void Stream_tab_live_bar_button_and_model_stay_in_step() => Headless.Run(StreamTab);

    private static void StreamTab()
    {
        Loc.Instance.SetLanguage(Loc.English);
        var app = Application.Current!;
        var theme = Theme();
        var window = new Window { Width = 1260, Height = 840 };
        var settings = new AppSettings();
        var model = new MainViewModel(new HistoryStore(Path.Combine(Path.GetTempPath(), $"hp-stream-{Guid.NewGuid():N}.db")), settings, TestDataFolder.App);
        try
        {
            var view = new MainView { DataContext = model };
            window.Content = view;
            Dispatcher.UIThread.RunJobs();
            var told = 0;
            model.StreamSettingsChanged += () => told++;

            view.ShowSettingsTab("Stream");
            Assert.Equal("Stream", settings.SettingsTab);
            Assert.True(view.FindControl<Control>("SettingsStream")!.IsVisible);
            Assert.False(view.FindControl<Control>("SettingsOverlay")!.IsVisible);

            var page = Named<CheckBox>(view, "StreamerModeSetting")!;
            var button = view.FindControl<ToggleButton>("StreamerModeButton")!;
            Assert.False(page.IsChecked);
            Assert.False(button.IsChecked);
            Assert.Equal(Loc.T("Stream.Mode.Off"), ToolTip.GetTip(button));

            // The live bar's button: the setting, the page's switch, the tip, and the host told once.
            button.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(settings.StreamerMode);
            Assert.True(page.IsChecked);
            Assert.Equal(Loc.T("Stream.Mode.On"), ToolTip.GetTip(button));
            Assert.Equal(1, told);

            // The tray flips the model: both follow.
            model.StreamerMode = false;
            Dispatcher.UIThread.RunJobs();
            Assert.False(page.IsChecked);
            Assert.False(button.IsChecked);
            Assert.Equal(2, told);

            // A window's switch, and the window closed by hand.
            var deck = Named<CheckBox>(view, "StreamDeckSetting")!;
            deck.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(settings.StreamDeckWindow);
            Assert.Equal(3, told);
            model.SetStreamWindow("deck", false);
            Dispatcher.UIThread.RunJobs();
            Assert.False(deck.IsChecked);
            Assert.False(settings.StreamDeckWindow);

            // The key background: the help gains its filter line, named after the colour.
            var keyLine = Named<TextBlock>(view, "StreamKeyLine")!;
            Assert.False(keyLine.IsVisible);
            Named<RadioButton>(view, "StreamBackgroundGreenOption")!.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(StreamBackgrounds.Green, settings.StreamWindowBackground);
            Assert.True(keyLine.IsVisible);
            Assert.Equal(Loc.F("Stream.Help.Key", Loc.T("Stream.Background.Green")), keyLine.Text);
            Assert.Equal(5, told);
        }
        finally
        {
            window.Close();
            app.Styles.Remove(theme);
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>
    /// An OBS window's content (StreamView): the overlay's panel while there is something to show, the background alone in a
    /// game of its mode with nothing yet, the quiet tile outside one; a key colour paints the whole window.
    /// </summary>
    [Fact]
    public void Stream_view_shows_the_panel_the_background_or_the_tile() => Headless.Run(StreamViewStates);

    private static void StreamViewStates()
    {
        Loc.Instance.SetLanguage(Loc.English);
        var app = Application.Current!;
        var theme = Theme();
        var window = new Window { Width = StreamWindowSpec.Lobby.Width, Height = StreamWindowSpec.Lobby.Height };
        try
        {
            var model = new OverlayViewModel();
            var settings = new AppSettings { OverlayEnabled = false, StreamLobbyWindow = true };
            var lobby = StreamScene.Lobby(CardDb.Empty);
            var game = LiveSnapshot.Empty with { Scene = "GAMEPLAY", HearthstoneRunning = true, InGame = true, GameType = "GT_BATTLEGROUNDS" };
            var view = new StreamView { Spec = StreamWindowSpec.Lobby, DataContext = model };
            // The headless run has neither Inter nor Montserrat: the default font, so the shown window can measure its text.
            view.Resources["UiFont"] = FontFamily.Default;
            view.Resources["DisplayFont"] = FontFamily.Default;
            window.Content = view;
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("tile", view.Showing);

            model.Update(game with { Battlegrounds = lobby }, CardDb.Empty, settings, null, null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("panel", view.Showing);
            // The overlay's own lobby panel, its rows from the same model.
            var panel = view.GetVisualDescendants().OfType<LobbyPanel>().Single();
            Assert.Equal(8, panel.GetVisualDescendants().OfType<Grid>().Count(g => g.Classes.Contains("lobbyRow")));
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == Loc.T("Overlay.Lobby.Title"));

            model.Update(game with { Battlegrounds = lobby with { IsHeroSelection = true, Opponents = [] } }, CardDb.Empty, settings, null, null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("background", view.Showing);

            var root = view.FindControl<Panel>("Root")!;
            var themed = root.Background;
            view.SetBackground(StreamBackgrounds.Magenta);
            Assert.Equal(Color.FromRgb(0xFF, 0x00, 0xFF), Assert.IsAssignableFrom<ISolidColorBrush>(root.Background).Color);
            view.SetBackground(StreamBackgrounds.Theme);
            Assert.Same(themed, root.Background);

            // Another window kind builds its own panel.
            view.Spec = StreamWindowSpec.Deck;
            Dispatcher.UIThread.RunJobs();
            Assert.Single(view.GetLogicalDescendants().OfType<DeckPanel>());
            Assert.Equal("tile", view.Showing);
        }
        finally
        {
            window.Close();
            app.Styles.Remove(theme);
            Dispatcher.UIThread.RunJobs();
        }
    }
}
