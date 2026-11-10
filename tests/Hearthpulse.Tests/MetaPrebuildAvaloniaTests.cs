using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Hearthpulse.Core;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Data;
using Hearthpulse.UI.Main;
using Hearthpulse.UI.Main.Meta;

namespace Hearthpulse.Tests;

/// <summary>
/// The meta tabs of the Standard page in the Avalonia main view build their fixed part (the filters, the cards' frames
/// and heads) while the page still shows "My stats", a part a dispatcher turn, so the first click on "Meta tiers" builds
/// the rows alone (the twin of the WPF MetaPrebuildTests). Until it is done the view is laid out but zero high,
/// transparent and closed to the pointer and to Tab; a click, or leaving the page, in the middle of it leaves an
/// ordinary view.
/// </summary>
[Collection(nameof(GlobalLanguage))]
public sealed class MetaPrebuildAvaloniaTests
{
    private static readonly string[] Parts = ["FilterRanks", "FilterPeriods", "FilterClasses", "MinGamesPicker", "TierCard", "ArchetypeCard", "MetaLists"];

    [Fact]
    public void The_fixed_part_of_the_meta_tabs_is_built_before_the_first_click_and_the_rows_at_it() => Run((window, view, model) =>
    {
        view.ShowPage("Standard");
        model.SetConstructedMeta(MetaTierScene.States());
        window.UpdateLayout();
        var meta = view.GetVisualDescendants().OfType<ConstructedMetaView>().Single();
        var list = Named<ItemsControl>(meta, "MetaTierList");

        // Being built: in the layout at its place, taking no room, not seen, not hit, not reached by Tab.
        Assert.True(meta.IsVisible);
        Assert.Equal((0, 0, false), (meta.Bounds.Height, meta.Opacity, meta.IsHitTestVisible));
        Assert.Equal(Avalonia.Input.KeyboardNavigationMode.None, Avalonia.Input.KeyboardNavigation.GetTabNavigation(meta));

        // Built: an ordinary hidden view again, with its filters (every segment has its template) and cards, and no row.
        PageRowsPanelAvaloniaTests.Settle(window);
        Assert.False(meta.IsVisible);
        Assert.Equal((1, true), (meta.Opacity, meta.IsHitTestVisible));
        Assert.True(double.IsNaN(meta.Height));
        Assert.All(Parts, name => Assert.True(Named<Control>(meta, name).IsVisible, name));
        var segments = Segments(meta);
        Assert.True(segments.Count >= 8 + 6 + 6 + 2, $"{segments.Count} segments built ahead");
        Assert.True(Named<Control>(meta, "MetaHead").Bounds.Width > 0);
        Assert.Equal(12, list.ItemCount);
        Assert.Empty(list.GetRealizedContainers());

        // The click: the same segments (nothing of the fixed part is built again), and now the rows.
        view.ShowConstructedTab("Tiers");
        PageRowsPanelAvaloniaTests.Settle(window);
        Assert.True(meta.IsEffectivelyVisible);
        Assert.Equal(segments, Segments(meta));
        Assert.Equal(12, list.GetRealizedContainers().Count());
        Assert.True(meta.Bounds.Height > 300);
    });

    [Fact]
    public void A_click_before_the_fixed_part_is_built_shows_the_whole_tab_at_once() => Run((window, view, model) =>
    {
        view.ShowPage("Standard");
        model.SetConstructedMeta(MetaTierScene.States());
        window.UpdateLayout();
        var meta = view.GetVisualDescendants().OfType<ConstructedMetaView>().Single();
        Assert.Equal(0, meta.Opacity);

        view.ShowConstructedTab("Tiers");
        PageRowsPanelAvaloniaTests.Settle(window);

        Assert.True(meta.IsEffectivelyVisible);
        Assert.Equal((1, true), (meta.Opacity, meta.IsHitTestVisible));
        Assert.True(double.IsNaN(meta.Height));
        Assert.NotEqual(Avalonia.Input.KeyboardNavigationMode.None, Avalonia.Input.KeyboardNavigation.GetTabNavigation(meta));
        Assert.All(Parts, name => Assert.True(Named<Control>(meta, name).Bounds.Height > 0, name));
        Assert.Equal(12, Named<ItemsControl>(meta, "MetaTierList").GetRealizedContainers().Count());
    });

    [Fact]
    public void Leaving_the_page_in_the_middle_of_it_starts_over_at_the_next_visit() => Run((window, view, model) =>
    {
        view.ShowPage("Standard");
        model.SetConstructedMeta(MetaTierScene.States());
        window.UpdateLayout();
        var meta = view.GetVisualDescendants().OfType<ConstructedMetaView>().Single();
        Assert.Equal(0, meta.Opacity);

        // Away before a part was built: an ordinary hidden view, nothing left transparent or zero high.
        view.ShowPage("Overview");
        PageRowsPanelAvaloniaTests.Settle(window);
        Assert.False(meta.IsVisible);
        Assert.Equal((1, true), (meta.Opacity, meta.IsHitTestVisible));
        Assert.True(double.IsNaN(meta.Height));

        // Back: built this time.
        view.ShowPage("Standard");
        PageRowsPanelAvaloniaTests.Settle(window);
        Assert.False(meta.IsVisible);
        Assert.True(Segments(meta).Count >= 8 + 6 + 6 + 2);
    });

    /// <summary>The filter segments whose template is applied.</summary>
    private static List<RadioButton> Segments(Control meta) =>
        meta.GetVisualDescendants().OfType<RadioButton>().Where(r => r.Classes.Contains("segment") && r.GetVisualChildren().Any()).ToList();

    private static T Named<T>(Control meta, string name) where T : Control =>
        meta.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static void Run(Action<Window, MainView, MainViewModel> body) => LocBindingTests.Headless.Run(() =>
    {
        var theme = PageRowsPanelAvaloniaTests.Theme();
        var db = TestDataFolder.Scratch("hp-meta-prebuild") + ".db";
        var model = new MainViewModel(new HistoryStore(db), new AppSettings(), TestDataFolder.App);
        var view = new MainView { DataContext = model };
        var window = new Window { Width = 1280, Height = 800 };
        // The headless platform has no Inter: the app swaps UiFont at startup the same way (HearthpulseApp).
        window.Resources["UiFont"] = Avalonia.Media.FontFamily.Default;
        window.Content = view;
        try
        {
            window.Show();
            PageRowsPanelAvaloniaTests.Settle(window);
            body(window, view, model);
        }
        finally
        {
            window.Close();
            Application.Current!.Styles.Remove(theme);
            Dispatcher.UIThread.RunJobs();
            TestDataFolder.TryDelete(db);
        }
    });
}
