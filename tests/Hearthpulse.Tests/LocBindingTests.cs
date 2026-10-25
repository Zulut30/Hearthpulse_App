using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
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
