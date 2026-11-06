using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Hearthpulse.App.Controls;
using Hearthpulse.App.Infrastructure;
using Hearthpulse.Core;
using Hearthpulse.Core.Localization;
using Microsoft.Win32;

namespace Hearthpulse.App.Main;

/// <summary>
/// The Settings page's tools (MainViewModel.SettingsTools.cs): the search index over the tabs, the jump to a match with
/// a highlight, the settings file dialogs, and re-applying everything after a reset or an import.
/// </summary>
public partial class MainWindow
{
    /// <summary>How long a jumped-to option stays outlined.</summary>
    private static readonly TimeSpan HighlightFor = TimeSpan.FromSeconds(1.6);

    private static readonly TimeSpan HighlightFade = TimeSpan.FromMilliseconds(400);

    /// <summary>The largest file an import reads: a settings file is a few kilobytes.</summary>
    private const long MaxSettingsFile = 1_000_000;

    private void InitSettingsTools(MainViewModel viewModel)
    {
        viewModel.SettingsIndex = IndexSettings;
        viewModel.SettingJumpRequested += JumpToSetting;
        viewModel.SettingsReplaced += OnSettingsReplaced;
        SettingsData.ExportSettingsClicked += OnExportSettings;
        SettingsData.ImportSettingsClicked += OnImportSettings;
        PageSettings.PreviewKeyDown += OnSettingsKey;
    }

    private void ReleaseSettingsTools()
    {
        _viewModel.SettingsIndex = null;
        _viewModel.SettingJumpRequested -= JumpToSetting;
        _viewModel.SettingsReplaced -= OnSettingsReplaced;
    }

    /// <summary>Ctrl+F on the Settings page puts the keyboard in the search box.</summary>
    private void OnSettingsKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SettingsSearchBar.FocusQuery();
            e.Handled = true;
        }
    }

    /// <summary>Every option of every tab: switches, sliders and choice rows by their label, buttons and card titles.</summary>
    private List<SettingsEntry> IndexSettings()
    {
        var entries = new List<SettingsEntry>();
        var seen = new HashSet<DependencyObject>();
        foreach (var (radio, tab) in SettingsTabs())
        {
            var title = radio.Content as string ?? (string)radio.Tag;
            Walk(tab, element =>
            {
                var (label, target) = Option(element);
                if (label is not null && target is not null && !HiddenInTab(target, tab) && seen.Add(target))
                {
                    entries.Add(new SettingsEntry((string)radio.Tag, title, label, SettingsSearch.Haystack(label), target));
                }
            });
        }

        return entries;
    }

    private static (string? Label, FrameworkElement? Target) Option(DependencyObject element) => element switch
    {
        CheckBox { Content: string text } box => (text, box),
        Ornament { Text.Length: > 0 } ornament => (ornament.Text, ornament),
        ToggleButton { Content: string text } toggle => (text, toggle),
        Button { Content: string { Length: > 1 } text } button => (text, button),
        TextBlock { Text.Length: > 0 } block when IsRowLabel(block) => (block.Text, block.Parent as FrameworkElement ?? block),
        _ => (null, null),
    };

    /// <summary>
    /// Hidden by its state (a switch that is off, a feature flag) rather than by its tab or a closed "Advanced" group: the
    /// search does not offer what it cannot show.
    /// </summary>
    private static bool HiddenInTab(FrameworkElement target, FrameworkElement tab)
    {
        for (DependencyObject? e = target; e is not null && !ReferenceEquals(e, tab); e = LogicalTreeHelper.GetParent(e))
        {
            if (e is FrameworkElement { Visibility: not Visibility.Visible } hidden && hidden.Tag is not ToggleButton)
            {
                return true;
            }
        }

        return false;
    }

    private IEnumerable<(RadioButton Radio, FrameworkElement Tab)> SettingsTabs() =>
    [
        (SettingsTabGeneral, SettingsGeneral), (SettingsTabAppearance, SettingsAppearance), (SettingsTabOverlay, SettingsOverlay),
        (SettingsTabTracker, SettingsTracker), (SettingsTabStream, SettingsStream), (SettingsTabAccount, SettingsAccount),
        (SettingsTabData, SettingsData), (SettingsTabAbout, SettingsAbout),
    ];

    /// <summary>A setting's name beside its slider or choice (SettingLabel), or a group heading inside a card (SettingGroup).</summary>
    private static bool IsRowLabel(TextBlock block) =>
        block.Style is { } style
        && (ReferenceEquals(style, block.TryFindResource("SettingLabel")) || ReferenceEquals(style, block.TryFindResource("SettingGroup")));

    private static void Walk(DependencyObject root, Action<DependencyObject> visit)
    {
        visit(root);
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            Walk(child, visit);
        }
    }

    private void JumpToSetting(SettingsEntry entry)
    {
        if (entry.Target is not FrameworkElement target)
        {
            return;
        }

        ShowSettingsTab(entry.Tab);
        // A collapsed "Advanced" group holding the option opens (its panel names its toggle in Tag).
        for (var parent = LogicalTreeHelper.GetParent(target); parent is not null; parent = LogicalTreeHelper.GetParent(parent))
        {
            if (parent is FrameworkElement { Tag: ToggleButton disclosure })
            {
                disclosure.IsChecked = true;
            }
        }

        // After the tab is laid out: scroll the option into view, outline it and give it the keyboard.
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            target.BringIntoView(new Rect(-16, -48, target.ActualWidth + 32, target.ActualHeight + 96));
            Highlight(target);
            if (target.Focusable)
            {
                target.Focus();
            }
        });
    }

    private static void Highlight(FrameworkElement target)
    {
        if (AdornerLayer.GetAdornerLayer(target) is not { } layer || target.TryFindResource("Accent") is not Brush accent)
        {
            return;
        }

        var adorner = new SearchHitAdorner(target, accent);
        layer.Add(adorner);
        if (Motion.On)
        {
            var fade = new DoubleAnimation(1, 0, new Duration(HighlightFade)) { BeginTime = HighlightFor - HighlightFade };
            fade.Completed += (_, _) => layer.Remove(adorner);
            adorner.BeginAnimation(OpacityProperty, fade);
            return;
        }

        var timer = new DispatcherTimer { Interval = HighlightFor };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            layer.Remove(adorner);
        };
        timer.Start();
    }

    /// <summary>A reset or an import changed many settings: the window shows them and the host applies and saves them.</summary>
    private void OnSettingsReplaced()
    {
        Loc.Instance.SetLanguage(_viewModel.Settings.InterfaceLanguage());
        SyncOverlaySwitch();
        _viewModel.RefreshPanelToggles();
        SettingsChanged?.Invoke();
    }

    private void OnExportSettings()
    {
        var dialog = new SaveFileDialog
        {
            FileName = "hearthpulse-settings.json",
            Filter = Loc.T("Settings.File.Filter") + " (*.json)|*.json",
            AddExtension = true,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, _viewModel.ExportSettings());
            _viewModel.SettingsExported(dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diag.Error("Settings file not written", ex);
            _viewModel.SettingsExported(null);
        }
    }

    private void OnImportSettings()
    {
        var dialog = new OpenFileDialog { Filter = Loc.T("Settings.File.Filter") + " (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        string? text = null;
        try
        {
            if (new FileInfo(dialog.FileName).Length < MaxSettingsFile)
            {
                text = File.ReadAllText(dialog.FileName);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diag.Error("Settings file not read", ex);
        }

        _viewModel.ImportSettings(text);
    }

    /// <summary>A rounded accent outline around the option the search jumped to.</summary>
    private sealed class SearchHitAdorner(UIElement target, Brush accent) : Adorner(target)
    {
        private readonly Pen _pen = new(accent, 2);

        protected override void OnRender(DrawingContext drawingContext)
        {
            var size = AdornedElement.RenderSize;
            drawingContext.DrawRoundedRectangle(null, _pen, new Rect(-6, -4, size.Width + 12, size.Height + 8), 8, 8);
        }
    }
}
