#if HEARTHPULSE_WPF
using Hearthpulse.App.Infrastructure;
#else
using Hearthpulse.UI.Infrastructure;
#endif
using System.IO;
using System.Globalization;
using Hearthpulse.Core;
using Hearthpulse.Core.Accounts;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Replay;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main.Match;
#else
namespace Hearthpulse.UI.Main.Match;
#endif

public sealed partial class ReplayLibraryViewModel
{
    /// <summary>The view put the shared copy on the clipboard as a file, or could not (the clipboard was busy).</summary>
    public void ShareDone(bool copied)
    {
        if (copied)
        {
            ShowStatus(Loc.T("Library.Shared"), "Win", "\U000F05E1");
        }
        else
        {
            Failed(Loc.T("Main.Meta.ClipboardBusy"));
        }
    }

    /// <summary>
    /// The platform's clipboard takes no files: the copy's folder opens with the file selected, for the player to drag
    /// it into the chat, and the status line links it again. Its path is never put on the clipboard: it holds the
    /// system user's name.
    /// </summary>
    public void ShareInFolder(string file)
    {
        ShowStatus(Loc.T("Library.SharedPath"), "Win", "\U000F05E1", file);
        RevealInShell(file);
    }

    internal void RequestShare(string path) => ShareFileRequested?.Invoke(path);

    /// <summary>The view should put this link on the clipboard (an uploaded replay).</summary>
    public event Action<string>? CopyLinkRequested;

    internal void Uploaded(string url)
    {
        CopyLinkRequested?.Invoke(url);
        ShowStatus(Loc.F("Library.Site.Uploaded", url), "Win", "\U000F05E1");
    }

    internal void ImportFinished() => Importing = false;

    internal void Succeeded(string text) => ShowStatus(text, "Win", "\U000F05E1");

    internal void Failed(string text) => ShowStatus(text, "Loss", "\U000F05D6");

    /// <summary>The status goes at the next tab, filter, opened match or action; there are no timers.</summary>
    internal void ClearStatus()
    {
        if (!_importing)
        {
            ShowStatus(string.Empty, string.Empty, string.Empty);
        }
    }

    private void ShowStatus(string text, string key, string glyph, string? file = null)
    {
        Status = text;
        StatusKey = key;
        StatusGlyph = glyph;
        if (_statusFile != file)
        {
            _statusFile = file;
            Raise(nameof(HasStatusFolder));
        }
    }

    /// <summary>Opens the folder of <paramref name="file"/> with the file selected where the platform can (Windows, macOS).</summary>
    private static void RevealInShell(string file)
    {
        try
        {
            var start = OperatingSystem.IsWindows() ? new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{file}\"")
                : OperatingSystem.IsMacOS() ? new System.Diagnostics.ProcessStartInfo("open", ["-R", file])
                : new System.Diagnostics.ProcessStartInfo(Path.GetDirectoryName(file) ?? file) { UseShellExecute = true };
            System.Diagnostics.Process.Start(start)?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Diag.Error("Showing the shared replay copy failed", ex);
        }
    }

    private static void OpenInShell(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Diag.Error($"Opening {folder} failed", ex);
        }
    }
}
