using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Hearthpulse.Core;
using Hearthpulse.Core.Localization;
using Hearthpulse.UI.Infrastructure;
using Hearthpulse.UI.Main.Match;

namespace Hearthpulse.UI.Main.Share;

/// <summary>The share window: the picture's preview, "Copy image", "Save" and the BattleTag switch (TASK-2026-11-07-growth-app).</summary>
public partial class ShareWindow : Window
{
    private readonly ShareViewModel _model = null!;

    /// <summary>For the XAML loader only.</summary>
    public ShareWindow() => InitializeComponent();

    public ShareWindow(ShareViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        InitializeComponent();
        _model = model;
        DataContext = model;
        CardHost.Content = BuildCard();
        model.PropertyChanged += OnModelChanged;
        Closed += (_, _) => model.PropertyChanged -= OnModelChanged;
    }

    /// <summary>Opens the window over <paramref name="owner"/> (not modal: the main window keeps working).</summary>
    public static ShareWindow Open(Control? owner, ShareViewModel model)
    {
        var window = new ShareWindow(model);
        if (TopLevel.GetTopLevel(owner) is Window parent)
        {
            window.Show(parent);
        }
        else
        {
            window.Show();
        }

        return window;
    }

    /// <summary>The picture control on screen (for the PNG and the render scenes).</summary>
    internal Control Card => (Control)CardHost.Content!;

    /// <summary>The picture as PNG, twice its size (as the turn picture, at most 4096 px a side).</summary>
    internal byte[] Encode() => MatchView.Encode(Card);

    private Control BuildCard() => _model.Kind switch
    {
        ShareKind.Deck when _model.Cover => new DeckCoverView { DataContext = _model },
        ShareKind.Deck => new DeckListView { DataContext = _model },
        ShareKind.Year => new YearCardView { DataContext = _model },
        _ => new SessionRecapView { DataContext = _model },
    };

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShareViewModel.Cover))
        {
            CardHost.Content = BuildCard();
        }
    }

    private void OnCopy(object? sender, RoutedEventArgs e) => CopyAsync().Forget("Share picture copy");

    private async Task CopyAsync()
    {
        try
        {
            if (Clipboard is not { } clipboard)
            {
                _model.Copied(false);
                return;
            }

            using var stream = new MemoryStream(Encode());
            using var bitmap = new Bitmap(stream);
            await clipboard.SetBitmapAsync(bitmap);
            _model.Copied(true);
        }
        catch (Exception ex) when (Failure(ex))
        {
            Diag.Error("Copying the share picture failed", ex);
            _model.Copied(false);
        }
    }

    private void OnSave(object? sender, RoutedEventArgs e) => SaveAsync().Forget("Share picture save");

    private async Task SaveAsync()
    {
        if (StorageProvider is not { CanSave: true } storage)
        {
            _model.Saved(null);
            return;
        }

        byte[] png;
        try
        {
            png = Encode();
        }
        catch (Exception ex) when (Failure(ex))
        {
            Diag.Error("Drawing the share picture failed", ex);
            _model.Saved(null);
            return;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Loc.T("Growth.Share.SaveTitle"),
            SuggestedFileName = SafeFileName(_model.FileName),
            DefaultExtension = "png",
            FileTypeChoices = [new FilePickerFileType(Loc.T("Growth.Share.Filter")) { Patterns = ["*.png"], MimeTypes = ["image/png"] }],
            SuggestedStartLocation = await storage.TryGetWellKnownFolderAsync(WellKnownFolder.Pictures),
        });
        if (file is null)
        {
            return;
        }

        try
        {
            await using var stream = await file.OpenWriteAsync();
            if (stream.CanSeek)
            {
                stream.SetLength(0);
            }

            await stream.WriteAsync(png);
            _model.Saved(file.TryGetLocalPath() ?? file.Name);
        }
        catch (Exception ex) when (Failure(ex))
        {
            Diag.Error("Saving the share picture failed", ex);
            _model.Saved(null);
        }
        finally
        {
            file.Dispose();
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    /// <summary>A deck name can hold characters a file name cannot.</summary>
    internal static string SafeFileName(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();

    private static bool Failure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or ArgumentException
            or System.Runtime.InteropServices.ExternalException;
}
