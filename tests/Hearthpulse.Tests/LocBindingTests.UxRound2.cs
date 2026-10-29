using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Hearthpulse.Core;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.Tests;

/// <summary>
/// The second usability pass (TASK-2026-10-29-ux-round2) in the Avalonia views. Styles, templates and bindings only:
/// the window is not shown, so nothing measures text (no fonts here).
/// </summary>
public sealed partial class LocBindingTests
{
    [Fact]
    public void Setup_banner_wraps_in_its_card() => Headless.Run(() => WithMainView((view, model) =>
    {
        model.Apply(LiveSnapshot.Empty with { LogConfigMissing = ["Power", "LoadingScreen", "Decks"] });
        Dispatcher.UIThread.RunJobs();

        var banner = view.FindControl<Border>("SetupBanner")!;
        var text = view.FindControl<TextBlock>("SetupWarningText")!;
        Assert.True(banner.IsVisible);
        // The glyph takes what it needs, the text the rest of the card, and it wraps there (as WPF does).
        var grid = Assert.IsType<Grid>(banner.Child);
        Assert.Equal(2, grid.ColumnDefinitions.Count);
        Assert.True(grid.ColumnDefinitions[0].Width.IsAuto && grid.ColumnDefinitions[1].Width.IsStar);
        Assert.Same(grid, text.Parent);
        Assert.Equal(1, Grid.GetColumn(text));
        Assert.Equal(Avalonia.Media.TextWrapping.Wrap, text.TextWrapping);
        Assert.Equal(model.SetupWarning, text.Text);
    }));

    [Fact]
    public void Overlay_size_and_opacity_sit_right_under_the_master_switch() => Headless.Run(() => WithMainView((view, _) =>
    {
        view.ShowSettingsTab("Overlay");
        Dispatcher.UIThread.RunJobs();
        var master = view.FindControl<CheckBox>("OverlaySetting")!;
        var switches = view.FindControl<StackPanel>("OverlayPanelSwitches")!;
        var card = Assert.IsType<StackPanel>(master.Parent);
        var blocks = card.Children.ToList();

        // Told apart by the values bound from the settings (distinct on purpose): they act on the whole overlay, so they
        // come before the panels' switches, not after all of them.
        foreach (var value in new[] { 1.25, 0.55 })
        {
            var slider = card.GetLogicalDescendants().OfType<Slider>().Single(s => Math.Abs(s.Value - value) < 0.001);
            Assert.DoesNotContain(slider, switches.GetLogicalDescendants().OfType<Slider>());
            var block = blocks.Single(b => b == slider || b.GetLogicalDescendants().Contains(slider));
            Assert.True(blocks.IndexOf(master) < blocks.IndexOf(block) && blocks.IndexOf(block) < blocks.IndexOf(switches), value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }, new AppSettings { OverlayScale = 1.25, OverlayOpacity = 0.55 }));

    [Fact]
    public void Constructed_meta_tabs_show_one_state_at_a_time() => Headless.Run(() => WithMainView((_, model) =>
    {
        var view = new Hearthpulse.UI.Main.Meta.ConstructedMetaView { DataContext = model };
        var window = new Window { Content = view };
        try
        {
            model.ShowConstructedFormat("FT_STANDARD");
            Dispatcher.UIThread.RunJobs();
            AssertTabs("Main.Meta.Loading");

            model.SetConstructedMeta(null);
            Dispatcher.UIThread.RunJobs();
            AssertTabs("Main.Meta.Unavailable");
        }
        finally
        {
            window.Close();
        }

        // On every tab: exactly one empty state shows, with this text, and the status line under it is hidden.
        void AssertTabs(string key)
        {
            foreach (var name in new[] { "MetaTiersPanel", "MetaDecksPanel", "MetaMatchupsPanel" })
            {
                var panel = view.FindControl<Control>(name)!;
                var shown = panel.GetLogicalDescendants().OfType<ContentControl>().Where(c => c.Classes.Contains("emptyState") && c.IsVisible).ToList();
                Assert.Equal(Loc.T(key), Assert.Single(shown).Content);
                var status = panel.GetLogicalDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("caption") && t.Text == model.ConstructedMetaStatus).ToList();
                Assert.NotEmpty(status);
                Assert.All(status, t => Assert.False(t.IsVisible));
            }
        }
    }));
}
