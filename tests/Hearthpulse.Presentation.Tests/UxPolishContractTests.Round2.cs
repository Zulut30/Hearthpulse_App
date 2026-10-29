using System.Windows;
using System.Windows.Controls;
using Hearthpulse.App.Overlay;
using Hearthpulse.Core;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Tracking;
using AvaMain = Hearthpulse.UI.Main.MainViewModel;
using WpfMain = Hearthpulse.App.Main.MainViewModel;
using WpfOverlay = Hearthpulse.App.Overlay.OverlayViewModel;

namespace Hearthpulse.Presentation.Tests;

/// <summary>
/// The second usability pass (TASK-2026-10-29-ux-round2) in the WPF window laid out off screen with the real theme and
/// fonts, and in both hosts' view models.
/// </summary>
public sealed partial class UxPolishContractTests
{
    [Fact]
    public void Setup_banner_wraps_a_long_warning_inside_its_card() => Sta(() =>
    {
        var model = new WpfMain(History(), new AppSettings());
        // The narrowest window and every log missing: the warning is far longer than one line.
        var (window, root) = Open(model, "Home", width: 1040);
        model.Apply(LiveSnapshot.Empty with
        {
            LogConfigMissing = ["Power", "LoadingScreen", "Decks", "Arena", "Achievements", "FullScreenFX", "Zone", "Asset", "Net", "Bob", "Gameplay", "Store"],
        });
        Settle(root);

        var banner = Named<Border>(window, "SetupBanner");
        var text = Named<TextBlock>(window, "SetupWarningText");
        Assert.Equal(Visibility.Visible, banner.Visibility);
        Assert.Equal(model.SetupWarning, text.Text);
        Assert.Equal(TextWrapping.Wrap, text.TextWrapping);
        // Inside the card's padding, right of the glyph: measured against its column, not at full length.
        var inner = banner.ActualWidth - banner.Padding.Left - banner.Padding.Right - banner.BorderThickness.Left - banner.BorderThickness.Right;
        Assert.True(text.ActualWidth <= inner + 0.5, $"text {text.ActualWidth} in {inner}");
        Assert.True(text.TranslatePoint(new Point(text.ActualWidth, 0), banner).X <= banner.ActualWidth - banner.Padding.Right + 0.5);
        Assert.True(text.ActualHeight >= 1.8 * text.FontSize, $"one line only: {text.ActualHeight}");
        window.Close();
    });

    [Fact]
    public void Overlay_size_and_opacity_sit_right_under_the_master_switch() => Sta(() =>
    {
        var model = new WpfMain(History(), new AppSettings());
        var (window, _) = Open(model, "Settings", "Overlay");
        var master = Named<CheckBox>(window, "OverlaySetting");
        var switches = Named<StackPanel>(window, "OverlayPanelSwitches");
        var card = Assert.IsType<StackPanel>(master.Parent);
        var blocks = card.Children.Cast<UIElement>().ToList();

        // They act on the whole overlay, so they come before the panels' switches, not after all of them.
        foreach (var path in new[] { "OverlayScale", "OverlayOpacity" })
        {
            var slider = Logical<Slider>(card).Single(s => System.Windows.Data.BindingOperations.GetBinding(s, System.Windows.Controls.Primitives.RangeBase.ValueProperty)?.Path?.Path == path);
            Assert.DoesNotContain(slider, Logical<Slider>(switches));
            var block = blocks.Single(b => b == slider || Logical<Slider>(b).Contains(slider));
            Assert.True(blocks.IndexOf(master) < blocks.IndexOf(block) && blocks.IndexOf(block) < blocks.IndexOf(switches), path);
        }

        window.Close();
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Constructed_meta_says_unavailable_instead_of_blaming_the_filters(bool wpf)
    {
        Loc.Instance.SetLanguage(Loc.English);
        dynamic model = wpf ? new WpfMain(History(), new AppSettings()) : new AvaMain(History(), new AppSettings());
        model.ShowConstructedFormat("FT_STANDARD");
        AssertStates(model, loading: true, unavailable: false, empty: false, status: false);

        // No network: the page says the stats are unavailable, not that the filters left nothing.
        model.SetConstructedMeta(null);
        AssertStates(model, loading: false, unavailable: true, empty: false, status: false);
        Assert.Equal(Loc.T("Main.Meta.Unavailable"), (string)model.ConstructedMetaStatus);

        // A filter change loads again.
        model.SetMetaRank.Execute("legend");
        AssertStates(model, loading: true, unavailable: false, empty: false, status: false);

        // Stats came, nothing passes the filters: now the filters' empty states and the source line show.
        model.SetConstructedMeta(EmptyMeta());
        AssertStates(model, loading: false, unavailable: false, empty: true, status: true);

        static void AssertStates(dynamic m, bool loading, bool unavailable, bool empty, bool status)
        {
            Assert.Equal(loading, (bool)m.MetaLoading);
            Assert.Equal(unavailable, (bool)m.MetaUnavailable);
            Assert.Equal(empty, (bool)m.MetaEmpty);
            Assert.Equal(empty, (bool)m.MetaDecksEmpty);
            Assert.Equal(empty, (bool)m.MetaMatchupsEmpty);
            Assert.Equal(status, (bool)m.MetaStatusShown);
        }
    }

    [Fact]
    public void Constructed_meta_tabs_show_one_state_at_a_time() => Sta(() =>
    {
        var model = new WpfMain(History(), new AppSettings());
        var (window, root) = Open(model, "Standard");
        var view = Logical<Hearthpulse.App.Main.Meta.ConstructedMetaView>(window).Single();
        // The view merges its own copy of the main look: its empty-state style, not the window's.
        var empty = view.FindResource("EmptyState");
        var panels = new[] { "MetaTiersPanel", "MetaDecksPanel", "MetaMatchupsPanel" }.Select(n => (FrameworkElement)view.FindName(n)).ToList();

        model.ShowConstructedFormat("FT_STANDARD");
        Settle(root);
        AssertTabs("Main.Meta.Loading");

        model.SetConstructedMeta(null);
        Settle(root);
        AssertTabs("Main.Meta.Unavailable");
        window.Close();

        // On every tab: exactly one empty state shows, with this text, and the status line under it is hidden.
        void AssertTabs(string key)
        {
            foreach (var panel in panels)
            {
                var shown = Logical<ContentControl>(panel).Where(c => ReferenceEquals(c.Style, empty) && c.Visibility == Visibility.Visible).ToList();
                Assert.Equal(Loc.T(key), Assert.Single(shown).Content);
                var status = Logical<TextBlock>(panel).Where(t => BindingPath(t) == "ConstructedMetaStatus").ToList();
                Assert.NotEmpty(status);
                Assert.All(status, t => Assert.Equal(Visibility.Collapsed, t.Visibility));
            }
        }
    });

    /// <summary>Stats of the standard format with no archetype in them (Core builds these from the API; its constructor is internal).</summary>
    private static ConstructedMeta EmptyMeta() => (ConstructedMeta)Activator.CreateInstance(
        typeof(ConstructedMeta), System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null,
        ["standard", "legend", "past_week", new DateTime(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc), 0, Array.Empty<MetaArchetype>(), Array.Empty<MetaDeck>()],
        null)!;

    [Fact]
    public void Compact_deck_row_keeps_its_name_size() => Sta(() =>
    {
        var window = OpenOverlay(new WpfOverlay());
        var row = new ContentPresenter
        {
            Content = new DeckTile("CS2_029", "Fireball", 4, "2", true, false, "COMMON", string.Empty, false),
            ContentTemplate = (DataTemplate)window.FindResource("DeckTileTemplate"),
        };
        var root = (Panel)window.Content;
        root.Children.Add(row);
        row.Measure(new Size(240, 30));
        row.Arrange(new Rect(0, 0, 240, 30));
        row.UpdateLayout();
        var full = Descendants<TextBlock>(row).First(t => t.Name == "CardName").FontSize;

        // Compact only drops the art and makes the row short: the name reads the same, the gem is 18 as in every compact list.
        MovablePanel.SetCompact(row, true);
        row.UpdateLayout();
        Assert.Equal(full, Descendants<TextBlock>(row).First(t => t.Name == "CardName").FontSize);
        Assert.Equal(12.5, full);
        var gem = Descendants<FrameworkElement>(row).First(e => e.Name == "Gem");
        Assert.Equal(18, gem.Width);
        Assert.Equal(18, gem.Height);
        Assert.Equal(18, Descendants<Grid>(row).First(g => g.Name == "RowRoot").ActualHeight, 0.5);
        Assert.Equal(Visibility.Collapsed, Descendants<Image>(row).First(i => i.Name == "Art").Visibility);
        window.Close();
    });
}
