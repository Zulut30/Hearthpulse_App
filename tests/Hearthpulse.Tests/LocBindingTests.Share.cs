using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Hearthpulse.Core;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Share;
using Hearthpulse.UI.Main;
using Hearthpulse.UI.Main.Decks;
using Hearthpulse.UI.Main.Share;
using Hearthpulse.UI.Overlay;

namespace Hearthpulse.Tests;

public sealed partial class LocBindingTests
{
    /// <summary>
    /// The share pictures and windows (TASK-2026-11-07-growth-app) load and bind in Avalonia: every kind of picture, the
    /// layout switch, the timestamps window, the Session panel, and the buttons that open them on Home, History and
    /// Settings → Stream.
    /// </summary>
    [Fact]
    public void Share_windows_pictures_and_their_buttons_bind() => Headless.Run(ShareWindows);

    private static void ShareWindows()
    {
        Loc.Instance.SetLanguage(Loc.English);
        var app = Application.Current!;
        var theme = Theme();
        var settings = new AppSettings();
        var model = new MainViewModel(new HistoryStore(Path.Combine(Path.GetTempPath(), $"hp-share-{Guid.NewGuid():N}.db")), settings, TestDataFolder.App);
        var windows = new List<Window>();
        try
        {
            var recap = new SessionRecapCard("November 6", [new RecapSection("Ranked · Standard", "HERO_04", [new ShareStat("Games", "6", "83%", 1)], [])], "empty");
            var share = new ShareViewModel(ShareKind.SessionRecap, recap, "Session recap", "file", settings, "Streamer#1");
            var window = new ShareWindow(share);
            windows.Add(window);
            Show(window);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(Loc.T("Growth.Share.Copy"), Named<Button>(window, "CopyButton").Content);
            Assert.IsType<SessionRecapView>(window.Card);
            Assert.Contains(window.Card.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == "Ranked · Standard");
            Assert.True(Named<CheckBox>(window, "SignSwitch").IsVisible);
            Assert.False(Named<RadioButton>(window, "LayoutCover").IsEffectivelyVisible);

            var row = new DeckListRow("CARD", "Card", 1, "LEGENDARY", "★");
            var deck = new DeckPictureCard("Deck", "Mage", "HERO_08", "30 cards", "CODE", [row], [row], [], [new ManaCurveBar("1", 1, 1)]);
            var deckShare = new ShareViewModel(ShareKind.Deck, deck, "Deck image", "file", settings, null);
            var deckWindow = new ShareWindow(deckShare);
            windows.Add(deckWindow);
            Show(deckWindow);
            Dispatcher.UIThread.RunJobs();
            Assert.IsType<DeckListView>(deckWindow.Card);
            Assert.False(Named<CheckBox>(deckWindow, "SignSwitch").IsVisible);
            deckShare.Cover = true;
            Dispatcher.UIThread.RunJobs();
            Assert.IsType<DeckCoverView>(deckWindow.Card);

            var year = new YearCard("Hearthpulse 2026 in review", "sub", [new ShareStat("Games this year", "73")], "HERO_11", "BG_HERO", "CARD", "empty");
            var yearWindow = new YearReviewWindow(new ShareViewModel(ShareKind.Year, year, "Year", "file", settings, null));
            windows.Add(yearWindow);
            Show(yearWindow);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(yearWindow.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == "Games this year");

            var stamps = new TimestampsWindow(model.Timestamps());
            windows.Add(stamps);
            Show(stamps);
            Dispatcher.UIThread.RunJobs();
            Assert.False(Named<Button>(stamps, "CopyButton").IsEnabled);
            Assert.Equal(Loc.T("Growth.Timestamps.Copy"), Named<Button>(stamps, "CopyButton").Content);

            var overlay = new OverlayViewModel();
            overlay.Update(Core.Tracking.LiveSnapshot.Empty with { StreamSession = StreamSessionView.None(DateTime.Now) }, Core.Cards.CardDb.Empty, settings, null, null);
            var stream = new StreamView { Spec = StreamWindowSpec.Session, DataContext = overlay };
            var streamWindow = new Window { Width = 266, Height = 184, Content = stream };
            windows.Add(streamWindow);
            Show(streamWindow);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("panel", stream.Showing);
            Assert.Contains(stream.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == Loc.T("Stream.Session.Idle"));

            var main = new Window { Width = 1260, Height = 840, Content = new MainView { DataContext = model } };
            windows.Add(main);
            Dispatcher.UIThread.RunJobs();
            var view = (MainView)main.Content!;
            Assert.Equal(Loc.T("Growth.Recap.Button"), Named<Button>(view, "SessionRecapButton").Content);
            view.ShowPage("History");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(Loc.T("Growth.Timestamps.Button"), Named<Button>(view, "TimestampsButton").Content);
            view.ShowSettingsTab("Stream");
            Dispatcher.UIThread.RunJobs();
            var sessionSwitch = Named<CheckBox>(view, "StreamSessionSetting");
            sessionSwitch.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(settings.StreamSessionWindow);
            Assert.True(Named<Button>(view, "StreamSessionReset").IsEffectivelyVisible);
        }
        finally
        {
            foreach (var window in windows)
            {
                window.Close();
            }

            app.Styles.Remove(theme);
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>Shows a window with the default font: the headless run has neither Inter nor Montserrat.</summary>
    private static void Show(Window window)
    {
        window.Resources["UiFont"] = FontFamily.Default;
        window.Resources["DisplayFont"] = FontFamily.Default;
        window.Show();
    }
}
