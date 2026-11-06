using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Hearthpulse.App.Main.Match;
using Hearthpulse.Core;
using Hearthpulse.Core.Localization;
using Microsoft.Win32;

namespace Hearthpulse.App.Main.Share;

/// <summary>The share window: the picture's preview, "Copy image", "Save" and the BattleTag switch (TASK-2026-11-07-growth-app).</summary>
public partial class ShareWindow : Window
{
    private readonly ShareViewModel _model;

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
    public static ShareWindow Open(Window? owner, ShareViewModel model)
    {
        var window = new ShareWindow(model) { Owner = owner };
        window.Show();
        return window;
    }

    /// <summary>The picture control on screen (for the PNG and the render scenes).</summary>
    internal FrameworkElement Card => (FrameworkElement)CardHost.Content;

    /// <summary>
    /// The picture as PNG at twice its size (the turn picture's scale, at most 4096 px a side), on the theme's background:
    /// a card a fraction of a pixel high (layout rounding at 125 or 150 %) would leave its last row half see-through,
    /// a dark line once pasted as a bitmap.
    /// </summary>
    internal byte[] Encode()
    {
        var card = Card;
        card.UpdateLayout();
        var size = new Rect(0, 0, card.ActualWidth, card.ActualHeight);
        var scale = MatchViewModel.StepExportScaleFor(size.Width, size.Height);
        var width = Math.Max(1, (int)Math.Ceiling(size.Width * scale));
        var height = Math.Max(1, (int)Math.Ceiling(size.Height * scale));
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle((Brush)FindResource("Bg"), null, new Rect(0, 0, width / scale, height / scale));
            var brush = new VisualBrush(card) { Stretch = Stretch.None, Viewbox = size, ViewboxUnits = BrushMappingMode.Absolute, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top };
            context.DrawRectangle(brush, null, size);
        }

        var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    private FrameworkElement BuildCard() => _model.Kind switch
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

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            var png = Encode();
            using var stream = new MemoryStream(png);
            var image = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            // Two formats: "PNG" for apps that read it (Discord, browsers, editors), the bitmap for the rest. The picture is
            // opaque, so the bitmap loses nothing.
            var data = new DataObject();
            data.SetData("PNG", new MemoryStream(png));
            data.SetImage(image);
            Clipboard.SetDataObject(data, copy: true);
            _model.Copied(true);
        }
        catch (Exception ex) when (Failure(ex))
        {
            Diag.Error("Copying the share picture failed", ex);
            _model.Copied(false);
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
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

        var dialog = new SaveFileDialog
        {
            Title = Loc.T("Growth.Share.SaveTitle"),
            Filter = Loc.T("Growth.Share.Filter") + "|*.png",
            DefaultExt = ".png",
            AddExtension = true,
            FileName = SafeFileName(_model.FileName),
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            File.WriteAllBytes(dialog.FileName, png);
            _model.Saved(dialog.FileName);
        }
        catch (Exception ex) when (Failure(ex))
        {
            Diag.Error("Saving the share picture failed", ex);
            _model.Saved(null);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>A deck name can hold characters a file name cannot.</summary>
    internal static string SafeFileName(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();

    private static bool Failure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or ArgumentException
            or System.Runtime.InteropServices.ExternalException;
}
