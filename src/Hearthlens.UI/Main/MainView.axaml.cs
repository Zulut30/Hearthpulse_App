using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Interactivity;
using Hearthlens.Core;
using Hearthlens.UI.Infrastructure;

namespace Hearthlens.UI.Main;

/// <summary>The dashboard: navigation, the five pages and the floating live bar. Hosted by a window or a mobile shell.</summary>
public partial class MainView : UserControl
{
    private const double CompactWidth = 900;

    private MainViewModel? _viewModel;

    public MainView()
    {
        InitializeComponent();
        foreach (var nav in new[] { NavOverview, NavBattlegrounds, NavConstructed, NavHistory, NavSettings })
        {
            nav.IsCheckedChanged += (_, _) => UpdatePages();
        }

        foreach (var tab in new[] { TabHeroes, TabComps, TabTrinkets, TabCards, TabMine })
        {
            tab.IsCheckedChanged += (_, _) => UpdateTabs();
        }

        SizeChanged += (_, e) => ApplyWidth(e.NewSize.Width);
    }

    /// <summary>Raised after any setting changes from the UI.</summary>
    public event Action? SettingsChanged;

    public event Action? ReimportRequested;

    public event Action? ArrangeOverlayRequested;

    /// <summary>Opens the data folder; hosts can replace it (e.g. on mobile, where there is no file manager).</summary>
    public Action<string>? OpenFolder { get; set; } = path =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelChanged;
        }

        _viewModel = DataContext as MainViewModel;
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.PropertyChanged += OnViewModelChanged;
        SyncOverlaySwitch();
        foreach (var box in this.GetLogicalDescendantsOfType<CheckBox>())
        {
            box.IsCheckedChanged += (_, _) => OnSettingChanged(box);
        }

        foreach (var slider in this.GetLogicalDescendantsOfType<Slider>())
        {
            slider.ValueChanged += (_, _) => SettingsChanged?.Invoke();
        }

        // Segmented choices on the settings page ("Pick suggestions", "Game plan comp"): each radio's Tag names the value
        // it selects, e.g. "PickHints.StatsOnly". The value is applied here, before SettingsChanged, rather than through a
        // two-way binding: Avalonia writes a binding back only after IsCheckedChanged, and a radio group stops writing
        // back once it has unchecked an option. Only the newly checked option reports, so one click applies once.
        foreach (var radio in PageSettings.GetLogicalDescendantsOfType<RadioButton>())
        {
            radio.IsCheckedChanged += (_, _) =>
            {
                if (radio.IsChecked == true && _viewModel is not null && ApplyChoice(_viewModel.Settings, radio.Tag as string))
                {
                    SettingsChanged?.Invoke();
                }
            };
        }
    }

    /// <summary>Applies a settings radio's Tag ("PickHints.Off", "CompMode.Auto"); false when nothing changed.</summary>
    private static bool ApplyChoice(AppSettings settings, string? choice)
    {
        switch (choice?.Split('.'))
        {
            case ["PickHints", var name] when Enum.TryParse<PickHints>(name, out var hints) && settings.PickHints != hints:
                settings.PickHints = hints;
                return true;
            case ["CompMode", var name] when Enum.TryParse<CompMode>(name, out var mode) && settings.CompMode != mode:
                settings.CompMode = mode;
                return true;
            default:
                return false;
        }
    }

    public void ShowPage(string page)
    {
        var nav = page switch
        {
            "Battlegrounds" => NavBattlegrounds,
            "Constructed" => NavConstructed,
            "History" => NavHistory,
            "Settings" => NavSettings,
            _ => NavOverview,
        };
        nav.IsChecked = true;
    }

    public void SyncOverlaySwitch()
    {
        if (_viewModel is not null)
        {
            OverlaySwitch.IsChecked = _viewModel.Settings.OverlayEnabled;
            OverlaySetting.IsChecked = _viewModel.Settings.OverlayEnabled;
        }
    }

    private bool Compact => Classes.Contains("compact");

    private void ApplyWidth(double width)
    {
        var compact = width < CompactWidth;
        Classes.Set("compact", compact);
        UpdateTilePanels();
        Frame.ColumnDefinitions[0].Width = new GridLength(compact ? 76 : 250);
        // Side-by-side cards stack on narrow screens.
        foreach (var (grid, right) in new[] { (HomeCharts, (Control)PlacementCard), (DeckColumns, MatchupCard) })
        {
            grid.ColumnDefinitions = compact ? new ColumnDefinitions("*") : new ColumnDefinitions("3*,16,2*");
            grid.RowDefinitions = compact ? new RowDefinitions("Auto,16,Auto") : new RowDefinitions("Auto");
            Grid.SetColumn(right, compact ? 0 : 2);
            Grid.SetRow(right, compact ? 2 : 0);
        }
    }

    /// <summary>Stat tiles sit in one row on wide screens and two columns on narrow ones.</summary>
    private void UpdateTilePanels()
    {
        foreach (var grid in this.GetVisualDescendants().OfType<UniformGrid>())
        {
            grid.Rows = Compact ? 0 : 1;
            grid.Columns = Compact ? 2 : 0;
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(MainViewModel.HearthstoneRunning):
                HsDot.Fill = ThemeBrushes.Get(_viewModel.HearthstoneRunning ? "Positive" : "Faint");
                break;
            case nameof(MainViewModel.MemoryAttached):
                MemDot.Fill = ThemeBrushes.Get(_viewModel.MemoryAttached ? "Positive" : "Faint");
                break;
            case nameof(MainViewModel.InGame):
                LivePulse.Fill = ThemeBrushes.Get(_viewModel.InGame ? "AccentContainer" : "Surface4");
                break;
        }
    }

    private void UpdatePages()
    {
        PageOverview.IsVisible = NavOverview.IsChecked == true;
        PageBattlegrounds.IsVisible = NavBattlegrounds.IsChecked == true;
        PageConstructed.IsVisible = NavConstructed.IsChecked == true;
        PageHistory.IsVisible = NavHistory.IsChecked == true;
        PageSettings.IsVisible = NavSettings.IsChecked == true;
        // Panels of a page that was hidden are created on its first layout.
        Dispatcher.UIThread.Post(UpdateTilePanels, DispatcherPriority.Loaded);
    }

    private void UpdateTabs()
    {
        HeroTierCard.IsVisible = TabHeroes.IsChecked == true;
        CompTierCard.IsVisible = TabComps.IsChecked == true;
        TrinketTierCard.IsVisible = TabTrinkets.IsChecked == true;
        CardStatsCard.IsVisible = TabCards.IsChecked == true;
        MyHeroesCard.IsVisible = TabMine.IsChecked == true;
        BracketPicker.IsVisible = TabMine.IsChecked != true;
    }

    private void OnSettingChanged(CheckBox box)
    {
        if (_viewModel is null || box == OverlaySwitch)
        {
            return;
        }

        if (box == OverlaySetting)
        {
            OverlaySwitch.IsChecked = _viewModel.Settings.OverlayEnabled;
        }

        SettingsChanged?.Invoke();
    }

    private void OnViewHistory(object? sender, RoutedEventArgs e) => NavHistory.IsChecked = true;

    private void OnOverlaySwitch(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.Settings.OverlayEnabled = OverlaySwitch.IsChecked == true;
        OverlaySetting.IsChecked = _viewModel.Settings.OverlayEnabled;
        SettingsChanged?.Invoke();
    }

    private void OnReimport(object? sender, RoutedEventArgs e) => ReimportRequested?.Invoke();

    private void OnArrangeOverlay(object? sender, RoutedEventArgs e) => ArrangeOverlayRequested?.Invoke();

    private void OnOpenData(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        try
        {
            OpenFolder?.Invoke(_viewModel.DataFolder);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            Core.Diag.Error("Opening the data folder failed", ex);
        }
    }
}

internal static class LogicalTreeHelpers
{
    public static IEnumerable<T> GetLogicalDescendantsOfType<T>(this StyledElement root)
    {
        foreach (var child in ((Avalonia.LogicalTree.ILogical)root).LogicalChildren)
        {
            if (child is T match)
            {
                yield return match;
            }

            if (child is StyledElement element)
            {
                foreach (var nested in element.GetLogicalDescendantsOfType<T>())
                {
                    yield return nested;
                }
            }
        }
    }
}
