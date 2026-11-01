using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Hearthpulse.Core;
using Hearthpulse.Core.Localization;

namespace Hearthpulse.UI.Main.Match;

/// <summary>
/// The History page as the replay library. The platform parts live here: the file picker of Import, the copy to share
/// on the clipboard (as a file where the platform takes one, else its path with the folder linked), Ctrl+V (the window
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
        }

        _model = model;
        if (model is not null && listen)
        {
            model.PickFileRequested += OnPickFile;
            model.ShareFileRequested += OnShareFile;
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

    private async Task ShareFileAsync(string path)
    {
        if (_model is not { } model || TopLevel.GetTopLevel(this) is not { Clipboard: { } clipboard } top)
        {
            _model?.ShareDone(copied: false);
            return;
        }

        try
        {
            // The file itself where the platform's clipboard takes files; otherwise its path, with the folder linked.
            if (await top.StorageProvider.TryGetFileFromPathAsync(path) is { } file)
            {
                try
                {
                    await clipboard.SetFileAsync(file);
                    model.ShareDone(copied: true);
                    return;
                }
                catch (Exception ex) when (ex is NotSupportedException or NotImplementedException or InvalidOperationException)
                {
                    Diag.Debug($"The clipboard took no file ({ex.Message}): copying the path");
                }
            }

            await clipboard.SetTextAsync(path);
            model.ShareDone(copied: true, pathOnly: true, folder: Path.GetDirectoryName(path));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            model.ShareDone(copied: false);
        }
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (List.SelectedItem is { } card)
        {
            List.ScrollIntoView(card);
        }
    }
}
