using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Hearthpulse.Core;
using Hearthpulse.Core.Localization;
using Hearthpulse.UI.Infrastructure;
using Hearthpulse.UI.Main;

namespace Hearthpulse.Tests;

/// <summary>The Settings search: matching rules, both languages and keywords, and the Avalonia page's index and jump.</summary>
public sealed class SettingsSearchTests
{
    [Theory]
    [InlineData("Ёлка, ПАНЕЛИ!", "елка панели")]
    [InlineData("  Size  (overlay) ", "size overlay")]
    [InlineData("", "")]
    public void Text_is_normalized_for_matching(string text, string expected) => Assert.Equal(expected, SettingsSearch.Normalize(text));

    [Theory]
    [InlineData("раз", true)]
    [InlineData("размер обор", false)]
    [InlineData("разм оверл", true)]
    [InlineData("мер", false)]
    [InlineData("   ", false)]
    public void Every_query_word_starts_a_word_of_the_option(string query, bool matches) =>
        Assert.Equal(matches, SettingsSearch.Matches(query, SettingsSearch.Normalize("Размер оверлея в игре")));

    [Fact]
    public void An_option_is_found_by_its_label_in_both_languages_and_by_keywords()
    {
        // By its key, without switching the shared interface language (other test classes run at the same time).
        var size = SettingsSearch.Haystack(Loc.Table(Loc.Russian)["Settings.Overlay.Size"], "Settings.Overlay.Size");
        Assert.True(SettingsSearch.Matches("размер", size));
        Assert.True(SettingsSearch.Matches("size", size));
        Assert.True(SettingsSearch.Matches("масштаб", size));
        Assert.True(SettingsSearch.Matches("zoom", size));
        Assert.False(SettingsSearch.Matches("discord", size));
    }

    [Fact]
    public void Results_put_labels_that_start_with_the_query_first_and_stop_at_the_limit()
    {
        var items = new[] { "Overlay size", "Size", "Interface size" }.Concat(Enumerable.Range(0, 20).Select(i => $"Size {i}")).ToList();
        var found = SettingsSearch.Find("size", items, x => x, x => SettingsSearch.Normalize(x));
        Assert.Equal(SettingsSearch.MaxResults, found.Count);
        Assert.Equal("Size", found[0]);
        Assert.Empty(SettingsSearch.Find(" ", items, x => x, x => x));
    }

    [Fact]
    public void The_avalonia_page_indexes_every_tab_and_a_jump_opens_the_tab_of_the_match() => LocBindingTests.Headless.Run(() =>
    {
        Loc.Instance.SetLanguage(Loc.English);
        var theme = new StyleInclude(new Uri("avares://Hearthpulse.UI/")) { Source = new Uri("avares://Hearthpulse.UI/Themes/Theme.axaml") };
        var app = Avalonia.Application.Current!;
        app.Styles.Add(theme);
        var model = new MainViewModel(new Core.Data.HistoryStore(Path.Combine(AppPaths.Root, $"hp-search-{Guid.NewGuid():N}.db")), new AppSettings(), TestDataFolder.App);
        var window = new Window { Width = 1260, Height = 840 };
        try
        {
            var view = new MainView { DataContext = model };
            window.Content = view;
            Dispatcher.UIThread.RunJobs();

            model.SettingsQuery = "calibration";
            var hit = Assert.Single(model.SettingsHits, h => h.Tab == "Overlay" && h.Label == Loc.T("Settings.Leaderboard.Title"));
            Assert.True(model.SettingsSearching);
            Assert.False(view.FindControl<StackPanel>("SettingsTabsArea")!.IsVisible);

            model.JumpToSetting.Execute(hit);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(string.Empty, model.SettingsQuery);
            Assert.Equal("Overlay", model.Settings.SettingsTab);
            Assert.True(view.FindControl<UI.Main.SettingsTabs.OverlaySettings>("SettingsOverlay")!.IsVisible);
            // The calibration lives under "Advanced", which the jump opens.
            Assert.True(view.FindControl<UI.Main.SettingsTabs.OverlaySettings>("SettingsOverlay")!.FindControl<ToggleButton>("AdvancedToggle")!.IsChecked);

            model.SettingsQuery = "accent";
            Assert.Contains(model.SettingsHits, h => h.Tab == "Appearance");
            model.SettingsQuery = "zzzz";
            Assert.True(model.SettingsNoHits);
        }
        finally
        {
            window.Close();
            app.Styles.Remove(theme);
            Dispatcher.UIThread.RunJobs();
        }
    });
}
