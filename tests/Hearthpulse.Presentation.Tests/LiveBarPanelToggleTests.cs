using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Hearthpulse.App.Infrastructure;
using Hearthpulse.Core;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using WpfMain = Hearthpulse.App.Main.MainViewModel;
using WpfToggle = Hearthpulse.App.Main.PanelToggle;
using WpfWindow = Hearthpulse.App.Main.MainWindow;
using AvaMain = Hearthpulse.UI.Main.MainViewModel;

namespace Hearthpulse.Presentation.Tests;

/// <summary>
/// The live bar's overlay panel buttons: each one switches a panel's existing setting (the one Settings -> Overlay /
/// Tracker switches), both UIs list the same panels, and in WPF a button turns the settings page's switch at once.
/// </summary>
public sealed class LiveBarPanelToggleTests
{
    private static HistoryStore History() => new(Path.Combine(AppPaths.Root, Guid.NewGuid().ToString("N") + ".db"));

    [Fact]
    public void Each_button_switches_only_its_own_setting_and_both_uis_list_the_same_panels()
    {
        var wpf = new WpfMain(History(), new AppSettings());
        var ava = new AvaMain(History(), new AppSettings());
        Assert.Equal(wpf.PanelToggles.Select(t => (t.Id, t.Icon, t.Quick)), ava.PanelToggles.Select(t => (t.Id, t.Icon, t.Quick)));
        Assert.Equal(
            [nameof(AppSettings.ShowDeckTracker), nameof(AppSettings.ShowOpponentTracker), nameof(AppSettings.ShowOpponentArchetype),
             nameof(AppSettings.ShowTurnTimer), nameof(AppSettings.ShowCounters), nameof(AppSettings.ShowCardPool)],
            wpf.QuickPanelToggles.Select(t => t.Id));
        Assert.Equal(wpf.PanelToggles.Count, wpf.PanelToggles.Select(t => t.Id).Distinct().Count());

        var toggled = new List<string>();
        wpf.PanelToggled += t => toggled.Add(t.Id);
        var bools = typeof(AppSettings).GetProperties().Where(p => p.PropertyType == typeof(bool)).ToList();
        foreach (var toggle in wpf.PanelToggles)
        {
            var property = Assert.Single(bools, p => p.Name == toggle.Id);
            // Every panel starts shown but the lobby tribes panel, whose player lines repeat the lobby list's.
            var initial = toggle.IsOn;
            Assert.Equal(toggle.Id != nameof(AppSettings.ShowBgLobbyPanel), initial);
            var before = bools.ToDictionary(p => p.Name, p => (bool)p.GetValue(wpf.Settings)!);

            toggle.Toggle.Execute(null);
            Assert.Equal(!initial, (bool)property.GetValue(wpf.Settings)!);
            Assert.Equal(!initial, toggle.IsOn);
            Assert.All(bools.Where(p => p != property), p => Assert.Equal(before[p.Name], (bool)p.GetValue(wpf.Settings)!));

            // Setting the same state again is not a change.
            toggle.IsOn = !initial;
            toggle.IsOn = initial;
            Assert.Equal(initial, (bool)property.GetValue(wpf.Settings)!);
        }

        Assert.Equal(wpf.PanelToggles.SelectMany(t => new[] { t.Id, t.Id }), toggled);
    }

    /// <summary>English and Russian carry the same keys (LocalizationTests), so English is enough here.</summary>
    [Fact]
    public void Every_panel_has_a_name_and_a_state_tip()
    {
        Loc.Instance.SetLanguage(Loc.English);
        var model = new WpfMain(History(), new AppSettings());
        Assert.All(model.PanelToggles, t => Assert.DoesNotContain("Main.Live.", t.Name, StringComparison.Ordinal));
        Assert.Equal(model.PanelToggles.Count, model.PanelToggles.Select(t => t.Name).Distinct().Count());
        Assert.All(new[] { "Main.Live.Panels", "Main.Live.PanelsTip" }, k => Assert.NotEqual(k, Loc.T(k)));

        var timer = model.PanelToggles.Single(t => t.Id == nameof(AppSettings.ShowTurnTimer));
        Assert.Equal("Turn timer: shown, click to hide", timer.Tip);
        timer.IsOn = false;
        Assert.Equal("Turn timer: hidden, click to show", timer.Tip);
    }

    [Fact]
    public void Wpf_bar_button_turns_the_settings_page_switch_and_saves_once()
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
            throw new InvalidOperationException("Live bar panel buttons failed", failure);
        }
    }

    private static void Check()
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Clear();
        app.Resources.MergedDictionaries.Add(ThemeEngine.Build(ThemePalette.Resolve(ThemePalette.DefaultName, 0, 0, 1)));
        var model = new WpfMain(History(), new AppSettings());
        var window = new WpfWindow(model);
        var saves = 0;
        window.SettingsChanged += () => saves++;
        window.ShowPage("Settings");
        window.ShowSettingsTab("Overlay");
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(1260, 840));
        root.Arrange(new Rect(0, 0, 1260, 840));
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        root.UpdateLayout();

        // The bar: one button per quick panel, checked while the panel is on.
        var buttons = Descendants<ToggleButton>(root).Where(b => b.DataContext is WpfToggle && b is not CheckBox).ToList();
        Assert.Equal(model.QuickPanelToggles.Select(t => t.Id), buttons.Select(b => ((WpfToggle)b.DataContext).Id));
        Assert.All(buttons, b => Assert.True(b.IsChecked));
        Assert.All(buttons, b => Assert.True(b.ActualWidth > 0));

        var timerButton = buttons.Single(b => ((WpfToggle)b.DataContext).Id == nameof(AppSettings.ShowTurnTimer));
        var pageSwitch = Descendants<CheckBox>(root).Single(c => Equals(c.Content, Loc.T("Widgets.Turn.Setting")));
        Assert.True(pageSwitch.IsChecked);

        timerButton.IsChecked = false;
        Assert.False(model.Settings.ShowTurnTimer);
        Assert.False(pageSwitch.IsChecked);
        Assert.Equal(1, saves);
        Assert.Equal(Loc.F("Main.Live.PanelHidden", Loc.T("Main.Live.Panel.TurnTimer")), timerButton.ToolTip);

        // The settings page changes it back: the bar follows once told.
        model.Settings.ShowTurnTimer = true;
        model.RefreshPanelToggles();
        Assert.True(timerButton.IsChecked);
        window.Close();
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
