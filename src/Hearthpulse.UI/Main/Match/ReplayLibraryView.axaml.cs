using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Hearthpulse.Core;
using Hearthpulse.Core.Localization;

namespace Hearthpulse.UI.Main.Match;

/// <summary>
/// The History page as the replay library. The platform parts live here: the file picker of Import, the copy to share
/// on the clipboard (as a file where the platform takes one, else shown selected in its folder), Ctrl+V (the window
/// calls <see cref="PasteFromClipboardAsync"/>), scrolling to a selected card.
/// </summary>
public partial class ReplayLibraryView : UserControl
{
    private ReplayLibraryViewModel? _model;

    public ReplayLibraryView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as ReplayLibraryViewModel, this.IsAttachedToVisualTree());
        AttachedToVisualTree += (_, _) => Attach(DataContext as ReplayLibraryViewModel, listen: true);
        DetachedFromVisualTree += (_, _) => Attach(DataContext as ReplayLibraryViewModel, listen: false);
        // Tunnel: Esc reaches the search box's handler before the box itself.
        SearchBox.AddHandler(KeyDownEvent, OnSearchKey, RoutingStrategies.Tunnel);
    }

    /// <summary>Ctrl+F: the search box takes the focus with its text selected.</summary>
    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    /// <summary>Esc in the search box clears its text (the result filter stays); an empty box lets Esc go on.</summary>
    private void OnSearchKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && _model is { Search.Length: > 0 } model)
        {
            model.Search = string.Empty;
            e.Handled = true;
        }
    }

    /// <summary>The box's ✕: the text goes, the focus stays in the box.</summary>
    private void OnClearSearchText(object? sender, RoutedEventArgs e)
    {
        if (_model is { } model)
        {
            model.Search = string.Empty;
        }

        SearchBox.Focus();
    }

    /// <summary>"Clear search" of the empty state: the text and the result filter go, the focus goes to the box.</summary>
    private void OnClearSearch(object? sender, RoutedEventArgs e)
    {
        _model?.ClearSearch.Execute(null);
        SearchBox.Focus();
    }

    /// <summary>Imports the file on the clipboard, or the file whose path is there as text.</summary>
    public async Task PasteFromClipboardAsync()
    {
        if (_model is not { } model)
        {
            return;
        }

        string? path = null;
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                path = (await clipboard.TryGetFileAsync())?.TryGetLocalPath()
                    ?? (await clipboard.TryGetTextAsync())?.Trim().Trim('"');
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.ExternalException or NotSupportedException)
        {
            // Another program holds the clipboard, or the platform has no such format: the same as nothing there.
        }

        await model.PasteAsync(path);
    }

    /// <summary>
    /// Listens to the model only while the view is on screen: the model outlives a closed window, and a handler of
    /// its events would keep the window alive.
    /// </summary>
    private void Attach(ReplayLibraryViewModel? model, bool listen)
    {
        if (_model is not null)
        {
            _model.PickFileRequested -= OnPickFile;
            _model.ShareFileRequested -= OnShareFile;
            _model.CopyLinkRequested -= OnCopyLink;
        }

        _model = model;
        if (model is not null && listen)
        {
            model.PickFileRequested += OnPickFile;
            model.ShareFileRequested += OnShareFile;
            model.CopyLinkRequested += OnCopyLink;
        }
    }

    private void OnPickFile() => PickFileAsync().Forget("Replay import picker");

    private async Task PickFileAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } storage)
        {
            return;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.T("Library.ImportTitle"),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(Loc.T("Library.ImportFilter")) { Patterns = ["*.hpr", "*.log"] }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
        {
            _model?.ImportPath(path);
        }
    }

    private void OnShareFile(string path) => ShareFileAsync(path).Forget("Replay share clipboard");

    /// <summary>An uploaded replay's link (plan 2.2); the status line shows it too.</summary>
    private void OnCopyLink(string url) => (TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(url) ?? Task.CompletedTask).Forget("Replay link clipboard");

    private async Task ShareFileAsync(string path)
    {
        if (_model is not { } model)
        {
            return;
        }

        // The file itself where the platform's clipboard takes files; otherwise its folder opens with it selected
        // (never its path on the clipboard: that holds the system user's name).
        try
        {
            if (TopLevel.GetTopLevel(this) is { Clipboard: { } clipboard } top && await top.StorageProvider.TryGetFileFromPathAsync(path) is { } file)
            {
                await clipboard.SetFileAsync(file);
                model.ShareDone(copied: true);
                return;
            }
        }
        catch (Exception ex) when (ex is NotSupportedException or NotImplementedException or InvalidOperationException
                                       or System.Runtime.InteropServices.ExternalException)
        {
            Diag.Debug($"The clipboard took no file ({ex.Message}): showing it in its folder");
        }

        model.ShareInFolder(path);
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (List.SelectedItem is { } card)
        {
            List.ScrollIntoView(card);
        }
    }
}
