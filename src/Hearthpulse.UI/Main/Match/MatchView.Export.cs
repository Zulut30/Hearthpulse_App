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
        if (_model is not { CanExportStep: true } model || TopLevel.GetTopLevel(this)?.StorageProvider is not { CanSave: true } storage)
        {
            return;
        }

        model.BeginStepExport();
        Control card = model.ShowBattlegroundsTable ? BattlegroundsCard : TableCard;
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
            using var bitmap = Draw(card);
            await using var stream = await file.OpenWriteAsync();
            bitmap.Save(stream, new PngBitmapEncoderOptions());
            model.StepExported(file.TryGetLocalPath() ?? file.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Diag.Error("Saving the step picture failed", ex);
            model.StepExported(null);
        }
        finally
        {
            file.Dispose();
        }
    }

    /// <summary>The card at <see cref="MatchViewModel.StepExportScaleFor"/>, its round corners transparent (as in WPF).</summary>
    private static RenderTargetBitmap Draw(Control card)
    {
        var size = card.Bounds.Size;
        var scale = MatchViewModel.StepExportScaleFor(size.Width, size.Height);
        var bitmap = new RenderTargetBitmap(
            new PixelSize(Math.Max(1, (int)Math.Ceiling(size.Width * scale)), Math.Max(1, (int)Math.Ceiling(size.Height * scale))), new Vector(96 * scale, 96 * scale));
        bitmap.Render(card);
        return bitmap;
    }
}
