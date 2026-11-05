using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Hearthpulse.Core;
using Hearthpulse.Core.Localization;
using Hearthpulse.UI.Infrastructure;

namespace Hearthpulse.UI.Main.Match;

/// <summary>"Save the step as PNG" (plan R4): the table card of the step on screen, drawn whole even when scrolled partly out of view.</summary>
public partial class MatchView
{
    private void OnExportStep(object? sender, RoutedEventArgs e) => ExportStepAsync().Forget("Step picture");

    private async Task ExportStepAsync()
    {
        if (_model is not { CanExportStep: true } model)
        {
            return;
        }

        model.BeginStepExport();
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanSave: true } storage)
        {
            // Nowhere to save on this platform: said, not silently nothing.
            model.StepExported(null);
            return;
        }

        // Drawn now, before the dialog: the step the button was pressed on, whatever the page does meanwhile.
        byte[] png;
        try
        {
            png = Encode(model.ShowBattlegroundsTable ? BattlegroundsCard : TableCard);
        }
        catch (Exception ex) when (Failure(ex))
        {
            Diag.Error("Drawing the step picture failed", ex);
            model.StepExported(null);
            return;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Loc.T("Match.Export.Title"),
            SuggestedFileName = model.StepFileName,
            DefaultExtension = "png",
            FileTypeChoices = [new FilePickerFileType(Loc.T("Match.Export.Filter")) { Patterns = ["*.png"], MimeTypes = ["image/png"] }],
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
                // A longer file of the same name is replaced, not left with its old tail.
                stream.SetLength(0);
            }

            await stream.WriteAsync(png);
            model.StepExported(file.TryGetLocalPath() ?? file.Name);
        }
        catch (Exception ex) when (Failure(ex))
        {
            Diag.Error("Saving the step picture failed", ex);
            model.StepExported(null);
        }
        finally
        {
            file.Dispose();
        }
    }

    private static bool Failure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or ArgumentException
            or System.Runtime.InteropServices.ExternalException;

    /// <summary>
    /// The card as PNG at <see cref="MatchViewModel.StepExportScaleFor"/>, its round corners transparent (as in WPF).
    /// <see cref="RenderTargetBitmap.Render"/> draws the control at its own origin, so a card scrolled out of view comes whole.
    /// </summary>
    internal static byte[] Encode(Control card)
    {
        var size = card.Bounds.Size;
        var scale = MatchViewModel.StepExportScaleFor(size.Width, size.Height);
        using var bitmap = new RenderTargetBitmap(
            new PixelSize(Math.Max(1, (int)Math.Ceiling(size.Width * scale)), Math.Max(1, (int)Math.Ceiling(size.Height * scale))), new Vector(96 * scale, 96 * scale));
        bitmap.Render(card);
        using var output = new MemoryStream();
        bitmap.Save(output, new PngBitmapEncoderOptions());
        return output.ToArray();
    }
}
