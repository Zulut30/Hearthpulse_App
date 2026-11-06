using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Hearthpulse.Core;
using Hearthpulse.Core.Localization;
using Hearthpulse.UI.Controls;
using Hearthpulse.UI.Infrastructure;

namespace Hearthpulse.UI.Main;

/// <summary>
/// The Settings page's tools (MainViewModel.SettingsTools.cs): the search index over the tabs, the jump to a match with
/// a highlight, the settings file pickers, and re-applying everything after a reset or an import. The WPF twin is
/// MainWindow.SettingsTools.cs.
/// </summary>
public partial class MainView
{
    /// <summary>How long a jumped-to option stays outlined.</summary>
    private static readonly TimeSpan HighlightFor = TimeSpan.FromSeconds(1.6);

    /// <summary>The largest file an import reads: a settings file is a few kilobytes.</summary>
    private const long MaxSettingsFile = 1_000_000;

    private bool _settingsToolsWired;

    private void InitSettingsTools(MainViewModel viewModel)
    {
        viewModel.SettingsIndex = IndexSettings;
        viewModel.SettingJumpRequested += JumpToSetting;
        viewModel.SettingsReplaced += OnSettingsReplaced;
        if (!_settingsToolsWired)
        {
            _settingsToolsWired = true;
            SettingsData.ExportSettingsClicked += () => ExportSettingsAsync().Forget("Export settings");
            SettingsData.ImportSettingsClicked += () => ImportSettingsAsync().Forget("Import settings");
            PageSettings.KeyDown += OnSettingsKey;
        }
    }

    private void ReleaseSettingsTools(MainViewModel viewModel)
    {
        viewModel.SettingsIndex = null;
        viewModel.SettingJumpRequested -= JumpToSetting;
        viewModel.SettingsReplaced -= OnSettingsReplaced;
    }

    /// <summary>Ctrl+F on the Settings page puts the keyboard in the search box.</summary>
    private void OnSettingsKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control)
        {
            SettingsSearchBar.FocusQuery();
            e.Handled = true;
        }
    }

    /// <summary>Every option of every tab: switches, sliders and choice rows by their label, buttons and card titles.</summary>
    private List<SettingsEntry> IndexSettings()
    {
        var entries = new List<SettingsEntry>();
        var seen = new HashSet<Control>();
        foreach (var (radio, tab) in SettingsTabPages())
        {
            var title = radio.Content as string ?? (string)radio.Tag!;
            foreach (var element in tab.GetLogicalDescendants().OfType<Control>())
            {
                var (label, target) = Option(element);
                if (label is not null && target is not null && seen.Add(target))
                {
                    entries.Add(new SettingsEntry((string)radio.Tag!, title, label, SettingsSearch.Haystack(label), target));
                }
            }
        }

        return entries;
    }

    private static (string? Label, Control? Target) Option(Control element) => element switch
    {
        CheckBox { Content: string text } box => (text, box),
        Ornament { Text.Length: > 0 } ornament => (ornament.Text, ornament),
        RadioButton => (null, null),
        ToggleButton { Content: string text } toggle => (text, toggle),
        Button { Content: string { Length: > 1 } text } button when !button.Classes.Contains("icon") => (text, button),
        TextBlock { Text.Length: > 0 } block when block.Classes.Contains("settingLabel") || block.Classes.Contains("settingGroup")
            => (block.Text, block.Parent as Control ?? block),
        _ => (null, null),
    };

    private IEnumerable<(RadioButton Radio, Control Tab)> SettingsTabPages() =>
    [
        (SettingsTabGeneral, SettingsGeneral), (SettingsTabAppearance, SettingsAppearance), (SettingsTabOverlay, SettingsOverlay),
        (SettingsTabTracker, SettingsTracker), (SettingsTabStream, SettingsStream), (SettingsTabAccount, SettingsAccount),
        (SettingsTabData, SettingsData), (SettingsTabAbout, SettingsAbout),
    ];

    private void JumpToSetting(SettingsEntry entry)
    {
        if (entry.Target is not Control target)
        {
            return;
        }

        ShowSettingsTab(entry.Tab);
        // A collapsed "Advanced" group holding the option opens (its panel names its toggle in Tag).
        for (var parent = target.GetLogicalParent(); parent is not null; parent = parent.GetLogicalParent())
        {
            if (parent is Control { Tag: ToggleButton disclosure })
            {
                disclosure.IsChecked = true;
            }
        }

        // After the tab is laid out: scroll the option into view, outline it and give it the keyboard.
        Dispatcher.UIThread.Post(() =>
        {
            target.BringIntoView(new Rect(-16, -48, target.Bounds.Width + 32, target.Bounds.Height + 96));
            Highlight(target);
            if (target.Focusable)
            {
                target.Focus();
            }
        }, DispatcherPriority.Loaded);
    }

    private static void Highlight(Control target)
    {
        var outline = new Border
        {
            BorderBrush = ThemeBrushes.Get("Accent"),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(-6, -4),
            IsHitTestVisible = false,
        };
        AdornerLayer.SetAdorner(target, outline);
        DispatcherTimer.RunOnce(() => AdornerLayer.SetAdorner(target, null), HighlightFor);
    }

    /// <summary>A reset or an import changed many settings: the view shows them and the host applies and saves them.</summary>
    private void OnSettingsReplaced()
    {
        if (_viewModel is null)
        {
            return;
        }

        Loc.Instance.SetLanguage(_viewModel.Settings.InterfaceLanguage());
        SyncOverlaySwitch();
        _viewModel.RefreshPanelToggles();
        ApplyTheme();
        SettingsChanged?.Invoke();
    }

    private static FilePickerFileType SettingsFileType => new(Loc.T("Settings.File.Filter")) { Patterns = ["*.json"] };

    private async Task ExportSettingsAsync()
    {
        if (_viewModel is null || TopLevel.GetTopLevel(this)?.StorageProvider is not { CanSave: true } storage)
        {
            return;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = "hearthpulse-settings.json",
            DefaultExtension = "json",
            FileTypeChoices = [SettingsFileType],
        });
        if (file is null)
        {
            return;
        }

        try
        {
            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(_viewModel.ExportSettings());
            _viewModel.SettingsExported(file.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diag.Error("Settings file not written", ex);
            _viewModel.SettingsExported(null);
        }
    }

    private async Task ImportSettingsAsync()
    {
        if (_viewModel is null || TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } storage)
        {
            return;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false, FileTypeFilter = [SettingsFileType] });
        if (files.Count == 0)
        {
            return;
        }

        string? text = null;
        try
        {
            var size = (await files[0].GetBasicPropertiesAsync()).Size;
            if (size is null or < MaxSettingsFile)
            {
                await using var stream = await files[0].OpenReadAsync();
                using var reader = new StreamReader(stream);
                text = await reader.ReadToEndAsync();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diag.Error("Settings file not read", ex);
        }

        _viewModel.ImportSettings(text);
    }
}
