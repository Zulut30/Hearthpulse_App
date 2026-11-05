using Hearthpulse.Core.Localization;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main.Match;
#else
namespace Hearthpulse.UI.Main.Match;
#endif

/// <summary>
/// "Save the step as PNG" (plan R4, <c>docs/match-replay.md</c>): the table card of the step on screen, drawn by the view
/// as the page shows it. The page only shows what the log revealed by the end of the game and the card holds no names,
/// BattleTags or ranks, so the picture carries no more than the Share copy of the replay does.
/// </summary>
public sealed partial class MatchViewModel
{
    private const string IconSaved = "\U000F05E1";

    /// <summary>The table of either mode is on screen: the header's PNG button shows.</summary>
    public bool CanExportStep => ShowConstructedTable || ShowBattlegroundsTable;

    /// <summary>The picture's suggested file name: "Hearthpulse step 34 · turn 7.png", without characters a file name cannot hold.</summary>
    public string StepFileName
    {
        get
        {
            var name = Loc.F("Match.Export.FileName", BookmarkText(_frame));
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            var safe = new string(name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '-' : c).ToArray()).Trim();
            return (safe.Length > 0 ? safe : "Hearthpulse") + ".png";
        }
    }

    /// <summary>The view is about to draw the step: the playback stops on it, and the last status goes.</summary>
    public void BeginStepExport()
    {
        Pause();
        ClearHeaderStatus();
    }

    /// <summary>
    /// The view saved the picture (<paramref name="path"/>) or could not (null): the header's status line says so. A
    /// dialog closed without a file calls nothing.
    /// </summary>
    public void StepExported(string? path)
    {
        if (path is null)
        {
            ShowHeaderStatus(Loc.T("Match.Export.Failed"), "Loss", IconFailed, folder: false);
            return;
        }

        ShowHeaderStatus(Loc.F("Match.Export.Saved", System.IO.Path.GetFileName(path)), "Win", IconSaved, folder: false);
    }

    /// <summary>The pixel scale of the picture: twice the page's size, so the card's text stays sharp when it is shared.</summary>
    public static double StepExportScale => 2;

    /// <summary>The largest side in pixels (a very wide window): past it the scale goes down, so the file stays a few MB.</summary>
    public static int StepExportMaxSide => 4096;

    /// <summary>The scale for a card of this size in page units: <see cref="StepExportScale"/>, less when a side would pass the limit.</summary>
    public static double StepExportScaleFor(double width, double height)
    {
        var side = Math.Max(width, height);
        return side <= 0 ? StepExportScale : Math.Min(StepExportScale, StepExportMaxSide / side);
    }
}
