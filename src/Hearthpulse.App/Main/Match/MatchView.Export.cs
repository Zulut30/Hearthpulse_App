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
        // Drawn now, before the dialog: the step the button was pressed on.
        byte[] png;
        try
        {
            png = Encode(ExportCard(model));
        }
        catch (Exception ex) when (Failure(ex))
        {
            Diag.Error("Drawing the step picture failed", ex);
            model.StepExported(null);
            return;
        }

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
            // The whole picture is ready: a failed write leaves no half-drawn file behind a good name.
            File.WriteAllBytes(dialog.FileName, png);
            model.StepExported(dialog.FileName);
        }
        catch (Exception ex) when (Failure(ex))
        {
            Diag.Error("Saving the step picture failed", ex);
            model.StepExported(null);
        }
    }

    private static bool Failure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or ArgumentException
            or System.Runtime.InteropServices.ExternalException;

    /// <summary>The table card of the mode on screen.</summary>
    internal FrameworkElement ExportCard(MatchViewModel model) => model.ShowBattlegroundsTable ? BattlegroundsStepCard : TableCard;

    /// <summary>
    /// The card as PNG at <see cref="MatchViewModel.StepExportScaleFor"/>, its round corners transparent (as in Avalonia).
    /// A brush of the card, not the card itself: that would be drawn at its place in the page. The brush shows exactly the
    /// card's box (absolute view box), not its content's bounds, which the arrow layer or a margin could widen.
    /// </summary>
    internal static byte[] Encode(FrameworkElement card)
    {
        card.UpdateLayout();
        var size = new Rect(0, 0, card.ActualWidth, card.ActualHeight);
        var scale = MatchViewModel.StepExportScaleFor(size.Width, size.Height);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var brush = new VisualBrush(card) { Stretch = Stretch.None, Viewbox = size, ViewboxUnits = BrushMappingMode.Absolute, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top };
            context.DrawRectangle(brush, null, size);
        }

        var bitmap = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(size.Width * scale)), Math.Max(1, (int)Math.Ceiling(size.Height * scale)), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }
}
