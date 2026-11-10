using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
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
    private static readonly string[] Parts = ["FilterRanks", "FilterPeriods", "FilterClasses", "MinGamesPicker", "TierCard", "ArchetypeCard"];

    /// <summary>
    /// In the app the build ahead starts a third of a second after the page shows; the tests run the dispatcher's jobs
    /// instead of waiting on the clock, so here it starts at once.
    /// </summary>
    [ModuleInitializer]
    internal static void StartTheBuildAheadAtOnce() => ConstructedMetaView.PrebuildDelay = TimeSpan.Zero;

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
        Assert.False(Named<Control>(meta, "MetaLists").IsVisible);

        // A part a turn: the four filter bars (each followed by its segments, a few a turn), then each card's frame and
        // each block inside it, the side card's sections one by one with their rows; no part twice.
        var all = meta.PrebuildTurns.Select(t => t.Step).ToList();
        var turns = all.Where(t => t != "rows").ToList();
        Assert.Equal(["FilterRanks", "FilterPeriods", "FilterClasses", "MinGamesPicker", "TierCard"], turns.Take(5));
        Assert.Equal("rows", all[1]);
        Assert.True(all.Count(t => t == "rows") >= 4 + 4 + 5, $"{all.Count(t => t == "rows")} turns of rows");
        Assert.True(turns.IndexOf("MetaHead") > turns.IndexOf("TierCard"));
        Assert.True(turns.IndexOf("ArchetypeSections") > turns.IndexOf("ArchetypeCard"));
        Assert.True(turns.Count >= 20, $"{turns.Count} turns");
        Assert.DoesNotContain("MetaLists", turns);
        Assert.Equal(5, turns.Count(t => t is "FilterRanks" or "TierCard" or "ArchetypeCard" or "ArchetypeSections" or "MetaHead"));

        // The side card was built ahead whole: the guide's twelve cards and the five best lists of the chosen archetype.
        Assert.Equal(12, Named<Control>(meta, "ArchetypeSections").GetVisualDescendants().OfType<Border>().Count(b => b.DataContext is MulliganRow && b.Classes.Contains("row")));
        Assert.Equal(5, Named<ItemsControl>(meta, "BestLists").GetRealizedContainers().Count(c => c.IsVisible));

        // Another meta tab first: the tier list's rows are still not built under it.
        view.ShowConstructedTab("Top");
        PageRowsPanelAvaloniaTests.Settle(window);
        Assert.True(meta.IsEffectivelyVisible);
        Assert.Equal(segments, Segments(meta));
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

    /// <summary>
    /// A window built anew where the last one was (back from the tray, another theme) sets the tab the page had, "My
    /// stats", after it showed the page: that neither stops the build ahead nor keeps it from starting.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_window_built_anew_on_my_stats_builds_the_meta_tabs_ahead_too(bool pageFirst) => Run((window, view, model) =>
    {
        model.SetConstructedMeta(MetaTierScene.States());
        if (pageFirst)
        {
            view.ShowPage("Standard");
            view.ConstructedTab = "Decks";
        }
        else
        {
            view.ConstructedTab = "Decks";
            view.ShowPage("Standard");
        }

        PageRowsPanelAvaloniaTests.Settle(window);
        var meta = view.GetVisualDescendants().OfType<ConstructedMetaView>().Single();

        Assert.False(meta.IsVisible);
        Assert.Equal((1, true), (meta.Opacity, meta.IsHitTestVisible));
        Assert.True(Segments(meta).Count >= 8 + 6 + 6 + 2, $"{Segments(meta).Count} segments built ahead");
        Assert.True(meta.PrebuildTurns.Count >= 20);
    });

    [Fact]
    public void Stats_that_come_after_the_build_ahead_show_without_building_the_fixed_part_again() => Run((window, view, model) =>
    {
        // Built ahead on an empty page: the filters that do not depend on the stats, the cards' frames, the heads.
        view.ShowPage("Standard");
        PageRowsPanelAvaloniaTests.Settle(window);
        var meta = view.GetVisualDescendants().OfType<ConstructedMetaView>().Single();
        var list = Named<ItemsControl>(meta, "MetaTierList");
        Assert.False(meta.IsVisible);
        string[] bars = ["FilterRanks", "FilterPeriods", "MinGamesPicker"];
        var fixedBars = bars.SelectMany(name => Segments(Named<Control>(meta, name))).ToList();
        Assert.True(fixedBars.Count >= 8 + 6 + 6, $"{fixedBars.Count} segments built ahead");
        var turns = meta.PrebuildTurns.Count;

        // The stats arrive: the view stays hidden, and the build ahead does not run again.
        model.SetConstructedMeta(MetaTierScene.States());
        PageRowsPanelAvaloniaTests.Settle(window);
        Assert.False(meta.IsVisible);
        Assert.Equal(turns, meta.PrebuildTurns.Count);
        Assert.Empty(list.GetRealizedContainers());

        // The click: the rows of the stats, the chosen archetype in the side card, the same fixed segments.
        view.ShowConstructedTab("Tiers");
        PageRowsPanelAvaloniaTests.Settle(window);
        Assert.Equal(12, list.GetRealizedContainers().Count());
        Assert.Equal(fixedBars, bars.SelectMany(name => Segments(Named<Control>(meta, name))));
        Assert.True(Segments(Named<Control>(meta, "FilterClasses")).Count >= 7, "a segment per class of the stats");
        Assert.Contains(Named<Control>(meta, "ArchetypeCard").GetVisualDescendants().OfType<TextBlock>(), t => t.Text == model.SelectedArchetype!.Name && t.IsEffectivelyVisible);
        Assert.Equal(12, Named<Control>(meta, "ArchetypeSections").GetVisualDescendants().OfType<Border>().Count(b => b.DataContext is MulliganRow && b.IsEffectivelyVisible && b.Classes.Contains("row")));
    });

    [Theory]
    [InlineData("Tiers", "MetaTierList")]
    [InlineData("Table", "MetaTable")]
    public void A_click_or_Enter_on_a_row_chooses_its_archetype_and_the_side_card_shows_it(string mode, string listName) => Run((window, view, model) =>
    {
        window.Activate();
        view.ShowPage("Standard");
        model.SetConstructedMeta(MetaTierScene.States());
        model.SetMetaView.Execute(mode);
        view.ShowConstructedTab("Tiers");
        PageRowsPanelAvaloniaTests.Settle(window);
        var meta = view.GetVisualDescendants().OfType<ConstructedMetaView>().Single();
        var rows = Named<ItemsControl>(meta, listName).GetVisualDescendants().OfType<Button>().Where(b => b.DataContext is MetaArchetypeRow).ToList();
        Assert.Equal(7, rows.Count);
        var first = (MetaArchetypeRow)rows[0].DataContext!;
        Assert.Same(first, model.SelectedArchetype);
        Assert.Contains("selected", rows[0].Classes);

        // Every row is named for a screen reader by its archetype.
        Assert.All(rows, r => Assert.Equal(((MetaArchetypeRow)r.DataContext!).Name, AutomationProperties.GetName(r)));

        // A click on the third row.
        var third = (MetaArchetypeRow)rows[2].DataContext!;
        rows[2].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PageRowsPanelAvaloniaTests.Settle(window);
        Assert.Same(third, model.SelectedArchetype);
        Assert.Contains("selected", rows[2].Classes);
        Assert.DoesNotContain("selected", rows[0].Classes);
        Assert.Same(third, model.DetailArchetype);
        Assert.Contains(Named<Control>(meta, "ArchetypeCard").GetVisualDescendants().OfType<TextBlock>(), t => t.Text == third.Name && t.IsEffectivelyVisible);

        // Enter on the fifth row, from the keyboard.
        var fifth = (MetaArchetypeRow)rows[4].DataContext!;
        Assert.True(rows[4].Focus(NavigationMethod.Tab));
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        PageRowsPanelAvaloniaTests.Settle(window);
        Assert.Same(fifth, model.SelectedArchetype);
        Assert.Contains("selected", rows[4].Classes);
        Assert.DoesNotContain("selected", rows[2].Classes);
        Assert.Same(fifth, model.DetailArchetype);
        Assert.Contains(Named<Control>(meta, "ArchetypeCard").GetVisualDescendants().OfType<TextBlock>(), t => t.Text == fifth.Name && t.IsEffectivelyVisible);
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
