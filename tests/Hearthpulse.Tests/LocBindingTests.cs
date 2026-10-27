using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Hearthpulse.Core.Localization;
using Hearthpulse.UI.Infrastructure;
using Hearthpulse.UI.Main;

namespace Hearthpulse.Tests;

/// <summary>Switches the global language, so it runs alone: other tests read English texts.</summary>
[CollectionDefinition(nameof(GlobalLanguage), DisableParallelization = true)]
public sealed class GlobalLanguage;

/// <summary>
/// The Avalonia <c>{i:Loc Key}</c> binding on a headless platform: it follows a language switch, and it does not keep
/// controls alive through the static <see cref="Loc.Instance"/> (overlay rows are rebuilt on every snapshot).
/// </summary>
[Collection(nameof(GlobalLanguage))]
public sealed class LocBindingTests
{
    [Fact]
    public void Bare_history_button_renders_the_game_row_template_instead_of_record_text()
    {
        Headless.Start();
        var theme = new StyleInclude(new Uri("avares://Hearthpulse.UI/"))
        {
            Source = new Uri("avares://Hearthpulse.UI/Themes/Theme.axaml"),
        };
        var app = Application.Current!;
        app.Styles.Add(theme);
        var window = new Window { Width = 1000, Height = 120 };
        try
        {
            var view = new MainView();
            var template = Assert.IsAssignableFrom<IDataTemplate>(view.Resources["OpenGameRow"]);
            var game = new GameRow("Today", "Ranked", "", "Mage", "Win", "win", "Against Warrior", "+25", 25);
            var button = Assert.IsType<Button>(template.Build(game));
            button.DataContext = game;
            window.Content = button;
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var texts = button.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text).ToArray();
            Assert.Contains(game.Hero, texts);
            Assert.Contains(game.Mode, texts);
            Assert.Contains(game.Detail, texts);
            Assert.Contains(game.Result, texts);
            Assert.Contains(game.Rating, texts);
            Assert.DoesNotContain(game.ToString(), texts);
            var row = Assert.Single(button.GetVisualDescendants().OfType<Border>(), border => border.Classes.Contains("row"));
            Assert.True(row.Bounds.Width > 0);
            Assert.Equal(button.Bounds.Width, row.Bounds.Width + row.Margin.Left + row.Margin.Right);
        }
        finally
        {
            window.Close();
            app.Styles.Remove(theme);
            Dispatcher.UIThread.RunJobs();
        }
    }

    [Fact]
    public void Layout_placeholders_name_the_scene_their_panel_shows_in_and_drop_the_repeated_tag()
    {
        Headless.Start();
        var theme = new StyleInclude(new Uri("avares://Hearthpulse.UI/"))
        {
            Source = new Uri("avares://Hearthpulse.UI/Themes/Theme.axaml"),
        };
        var app = Application.Current!;
        app.Styles.Add(theme);
        var window = new Window { Width = 1920, Height = 1080 };
        try
        {
            var view = new Hearthpulse.UI.Overlay.OverlayView { DataContext = new Hearthpulse.UI.Overlay.OverlayViewModel { LayoutEditing = true } };
            // Templates and styles only: the window is not shown, so nothing measures text (the headless run has no fonts).
            window.Content = view;
            var panels = view.GetLogicalDescendants().OfType<Hearthpulse.UI.Overlay.MovablePanel>().ToDictionary(p => p.Id);
            foreach (var panel in panels.Values)
            {
                panel.ApplyTemplate();
            }

            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Appears in the menus between games", Hint(panels["ladder-session"]));
            Assert.Equal("Appears during the Arena draft", Hint(panels["arena-draft"]));
            Assert.Equal("Appears during matches", Hint(panels["deck"]));
            // The placeholder carries the title, so its gold tag is hidden.
            Assert.False(Part<Border>(panels["deck"], "Tag").IsVisible);
        }
        finally
        {
            window.Close();
            app.Styles.Remove(theme);
            Dispatcher.UIThread.RunJobs();
        }

        static T Part<T>(Control panel, string name) where T : Control =>
            Assert.Single(panel.GetVisualDescendants().OfType<T>(), c => c.Name == name);

        static string? Hint(Control panel) =>
            Assert.Single(new[] { Part<TextBlock>(panel, "DefaultHint"), Part<TextBlock>(panel, "HintText") }, b => b.IsVisible).Text;
    }

    [Fact]
    public void Avalonia_texts_follow_a_switch_and_let_their_controls_go()
    {
        Headless.Start();
        var block = Bound();
        try
        {
            Assert.Equal("Settings", block.Text);
            Loc.Instance.SetLanguage(Loc.Russian);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Настройки", block.Text);

            var dropped = BindMany(500);
            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            Assert.DoesNotContain(dropped, r => r.TryGetTarget(out _));
        }
        finally
        {
            Loc.Instance.SetLanguage(Loc.English);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.Equal("Settings", block.Text);
    }

    private static TextBlock Bound()
    {
        var block = new TextBlock();
        block.Bind(TextBlock.TextProperty, new LocExtension("Settings.Title").ProvideValue(null!));
        return block;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<WeakReference<TextBlock>> BindMany(int count) =>
        Enumerable.Range(0, count).Select(_ => new WeakReference<TextBlock>(Bound())).ToList();

    /// <summary>One headless Avalonia platform per test run, set up on the first test that needs it.</summary>
    private static class Headless
    {
        private static readonly Lazy<bool> Setup = new(() =>
        {
            AppBuilder.Configure<Application>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
            return true;
        });

        public static void Start() => _ = Setup.Value;
    }
}
