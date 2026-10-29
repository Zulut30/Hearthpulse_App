using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Hearthpulse.Core;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.UI.Main;

namespace Hearthpulse.Tests;

/// <summary>
/// The usability pass (TASK-2026-10-29-ux-polish) in the Avalonia views: what was hidden in tips or easy to hit by
/// mistake. Styles, templates and bindings only: the window is not shown, so nothing measures text (no fonts here).
/// </summary>
public sealed partial class LocBindingTests
{
    [Fact]
    public void Busy_line_tip_carries_the_whole_message() => Headless.Run(() => WithMainView((view, model) =>
    {
        model.Busy = Loc.T("Host.Busy.GameNotFound");
        Dispatcher.UIThread.RunJobs();

        var line = view.FindControl<DockPanel>("BusyLine")!;
        Assert.True(line.IsVisible);
        var shown = line.Children.OfType<TextBlock>().Last();
        Assert.Equal(model.Busy, shown.Text);
        Assert.Equal(Avalonia.Media.TextTrimming.CharacterEllipsis, shown.TextTrimming);

        var tip = Assert.IsType<StackPanel>(ToolTip.GetTip(line));
        // A tooltip takes its owner's data context when it opens; unopened it is handed over here.
        tip.DataContext ??= model;
        Dispatcher.UIThread.RunJobs();
        var texts = tip.Children.OfType<TextBlock>().ToList();
        Assert.Equal(model.Busy, texts[0].Text);
        Assert.Equal(Avalonia.Media.TextWrapping.Wrap, texts[0].TextWrapping);
        Assert.Equal(Loc.T("Main.Busy.Tip"), texts[1].Text);
    }));

    /// <summary>The main view over a fresh view model with the theme applied, in English.</summary>
    private static void WithMainView(Action<MainView, MainViewModel> body, AppSettings? settings = null)
    {
        Loc.Instance.SetLanguage(Loc.English);
        var theme = new StyleInclude(new Uri("avares://Hearthpulse.UI/"))
        {
            Source = new Uri("avares://Hearthpulse.UI/Themes/Theme.axaml"),
        };
        var app = Application.Current!;
        app.Styles.Add(theme);
        var window = new Window { Width = 1260, Height = 840 };
        var model = new MainViewModel(new HistoryStore(Path.Combine(AppPaths.Root, $"hp-ux-polish-{Guid.NewGuid():N}.db")), settings ?? new AppSettings());
        try
        {
            var view = new MainView { DataContext = model };
            window.Content = view;
            Dispatcher.UIThread.RunJobs();
            body(view, model);
        }
        finally
        {
            window.Close();
            app.Styles.Remove(theme);
            Dispatcher.UIThread.RunJobs();
        }
    }
}
