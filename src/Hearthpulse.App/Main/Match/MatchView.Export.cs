using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Hearthpulse.Core;
using Hearthpulse.Core.Localization;
using Microsoft.Win32;

namespace Hearthpulse.App.Main.Match;

/// <summary>"Save the step as PNG" (plan R4): the table card of the step on screen, drawn whole even when scrolled partly out of view.</summary>
public partial class MatchView
{
    private void OnExportStep(object sender, RoutedEventArgs e)
    {
        if (_model is not { CanExportStep: true } model)
        {
            return;
        }

        model.BeginStepExport();
        var card = ExportCard(model);
        var dialog = new SaveFileDialog
        {
            Title = Loc.T("Match.Export.Title"),
            Filter = Loc.T("Match.Export.Filter") + "|*.png",
            DefaultExt = ".png",
            AddExtension = true,
            FileName = model.StepFileName,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        try
        {
            using var stream = File.Create(dialog.FileName);
            Encode(card).Save(stream);
            model.StepExported(dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Diag.Error("Saving the step picture failed", ex);
            model.StepExported(null);
        }
    }

    /// <summary>The table card of the mode on screen.</summary>
    internal FrameworkElement ExportCard(MatchViewModel model) => model.ShowBattlegroundsTable ? BattlegroundsStepCard : TableCard;

    /// <summary>
    /// The card at <see cref="MatchViewModel.StepExportScaleFor"/>, its round corners transparent (as in Avalonia). A brush
    /// of the card, not the card itself: that would be drawn at its place in the page.
    /// </summary>
    internal static PngBitmapEncoder Encode(FrameworkElement card)
    {
        card.UpdateLayout();
        var size = new Rect(0, 0, card.ActualWidth, card.ActualHeight);
        var scale = MatchViewModel.StepExportScaleFor(size.Width, size.Height);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(new VisualBrush(card) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, size);
        }

        var bitmap = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(size.Width * scale)), Math.Max(1, (int)Math.Ceiling(size.Height * scale)), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        return encoder;
    }
}
