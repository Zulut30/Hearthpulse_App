using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Hearthpulse.App.Infrastructure;
using Hearthpulse.Core;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using WpfMain = Hearthpulse.App.Main.MainViewModel;
using WpfWindow = Hearthpulse.App.Main.MainWindow;
using AvaMain = Hearthpulse.UI.Main.MainViewModel;

namespace Hearthpulse.Presentation.Tests;

/// <summary>
/// The WPF main window resolves its resources only at run time, like the overlay (OverlayXamlLoadTests): this builds it
/// with a non-default theme, opens Settings and lays it out off screen (nothing is shown), so the shell, the settings
/// cards and the theme tiles are built once. The theme picker is shared: both hosts list the same presets by key.
/// </summary>
public sealed class MainWindowXamlLoadTests
{
    // The window names its images by assembly ("/Hearthpulse;component/Assets/..."), so they load here too, where the
    // application resource assembly is the test host, whatever other test made the process's Application first.
    private static HistoryStore History() => new(Path.Combine(AppPaths.Root, Guid.NewGuid().ToString("N") + ".db"));

    [Fact]
    public void Wpf_main_window_loads_with_a_theme_and_builds_the_theme_tiles()
    {
        Loc.Instance.SetLanguage(Loc.English);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Check();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new InvalidOperationException("Main window XAML failed to load", failure);
        }
    }

    private static void Check()
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Clear();
        app.Resources.MergedDictionaries.Add(ThemeEngine.Build(ThemePalette.Resolve("Frost", 0, 0, 1)));
        var model = new WpfMain(History(), new AppSettings());
        var window = new WpfWindow(model);
        window.ShowPage("Settings");
        window.ShowSettingsTab("General");
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(1260, 840));
        root.Arrange(new Rect(0, 0, 1260, 840));
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        root.UpdateLayout();

        var swatch = window.FindResource("ThemeSwatch");
        var tiles = Descendants<Button>(root).Where(b => ReferenceEquals(b.Style, swatch)).ToList();
        Assert.Equal(ThemePalette.Presets.Count, tiles.Count);
        Assert.Equal(ThemePalette.Presets.Select(p => p.Name), tiles.Select(t => (string)t.CommandParameter));
        Assert.All(tiles, t => Assert.True(t.ActualWidth > 0));
        window.Close();
    }

    [Fact]
    public void Both_hosts_offer_the_same_presets_and_keep_the_key_not_the_localized_name()
    {
        var wpf = new WpfMain(History(), new AppSettings());
        var ava = new AvaMain(History(), new AppSettings());
        Assert.Equal(wpf.Themes.Select(t => (t.Key, t.Name, t.Selected)), ava.Themes.Select(t => (t.Key, t.Name, t.Selected)));
        Assert.True(wpf.Themes.Single(t => t.Key == ThemePalette.DefaultName).Selected);

        var changes = 0;
        ava.ThemeChanged += () => changes++;
        var raised = new List<string?>();
        ava.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        ava.SelectTheme.Execute("Frost");
        Assert.Equal("Frost", ava.Settings.Theme);
        Assert.Equal(1, changes);
        // Avalonia repaints in place: the picker hears that its selection moved.
        Assert.Contains(nameof(AvaMain.Themes), raised);
        Assert.True(ava.Themes.Single(t => t.Key == "Frost").Selected);

        // A localized tile name ("Лёд" is Frost in Russian) is not a preset: nothing changes.
        ava.SelectTheme.Execute("Лёд");
        Assert.Equal("Frost", ava.Settings.Theme);
        Assert.Equal(1, changes);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found)
            {
                yield return found;
            }

            foreach (var nested in Descendants<T>(child))
            {
                yield return nested;
            }
        }
    }
}
