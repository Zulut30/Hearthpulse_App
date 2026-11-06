#if HEARTHPULSE_WPF
using Hearthpulse.App.Main.Match;
using Hearthpulse.App.Overlay;
#else
using Hearthpulse.UI.Main.Match;
using Hearthpulse.UI.Overlay;
#endif
using Hearthpulse.Core;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Memory;
using Hearthpulse.Core.Replay;
using Hearthpulse.Core.Stats;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main;
#else
namespace Hearthpulse.UI.Main;
#endif

public sealed partial class MainViewModel
{
    /// <summary>The card's link on the site, from the uploads read once for the tab (only own games are uploaded).</summary>
    private static string SiteUrlOf(IReadOnlyDictionary<string, ReplayUploadResult> uploads, LibraryCard card) =>
        card is { CanUpload: true, FileName: { } name } && uploads.GetValueOrDefault(ReplayUploads.KeyOf(card.Shelf, name)) is { } upload
            ? upload.Url
            : string.Empty;

    /// <summary>
    /// Uploads the card's replay to the site (plan 2.2), after the confirmation in the card: the Share copy without the
    /// post-game part (<see cref="ReplayUpload"/>), with the account's sign-in. The link goes to the clipboard. While it
    /// runs the card's button cancels it; a second upload cannot start.
    /// </summary>
    internal void UploadLibraryCard(LibraryCard card, string name)
    {
        if (card.SiteWork != SiteWork.None)
        {
            return;
        }

        _libraryAction = UploadAsync(card, name);
        _libraryAction.Forget("Replay upload");
    }

    private async Task UploadAsync(LibraryCard card, string name)
    {
        if (!card.CanUpload || card.SiteWork != SiteWork.None)
        {
            return;
        }

        if (_account is not { SignedIn: true } account)
        {
            Library.Failed(Loc.T("Library.Site.SignIn"));
            return;
        }

        using var cancel = new CancellationTokenSource();
        card.SiteCancel = cancel;
        card.SiteWork = SiteWork.Uploading;
        try
        {
            var cards = _cards;
            var files = LibraryFiles;
            var uploads = LibraryUploads;
            var shelf = card.Shelf;
            var body = await Task.Run(() => files.Load(shelf, name) is { } text && ReplayUpload.Prepare(text, cards) is { } upload
                ? ReplayUpload.Encode(upload)
                : null, cancel.Token).ConfigureAwait(true);
            if (body is null)
            {
                Library.Failed(Loc.T("Library.ShareFailed"));
                return;
            }

            // The game's profile id links the replay in the site's «Последние игры»: only for a game the profile upload
            // sends, while it is on - the site then already has the game (the id is derived from its start time).
            var gameId = Settings.UploadGamesToProfile && card.HistoryKey is { } key
                && _libraryGames.Concat(_recentGames ?? []).FirstOrDefault(g => g.Key == key) is { } record
                ? Core.Accounts.ProfileEvents.ProfileGameId(record)
                : null;
            var (result, error) = await account.UploadReplayAsync(body, gameId, cancel.Token).ConfigureAwait(true);
            if (result is null)
            {
                Library.Failed(Loc.Or("Library.Site.Error." + error, Loc.T("Library.Site.Error.SERVER_ERROR")));
                return;
            }

            await Task.Run(() => uploads.Set(ReplayUploads.KeyOf(shelf, name), result), CancellationToken.None).ConfigureAwait(true);
            card.SiteUrl = result.Url;
            Library.Uploaded(result.Url);
            await RefreshAccountCountsAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            Library.Failed(Loc.T("Library.Site.Cancelled"));
        }
        finally
        {
            card.SiteCancel = null;
            card.SiteWork = SiteWork.None;
        }
    }

    /// <summary>Deletes the card's replay from the site with the token its upload handed out.</summary>
    internal void RemoveLibraryCardFromSite(LibraryCard card, string name)
    {
        if (card.SiteWork != SiteWork.None)
        {
            return;
        }

        _libraryAction = RemoveFromSiteAsync(card, name);
        _libraryAction.Forget("Replay removal from the site");
    }

    private async Task RemoveFromSiteAsync(LibraryCard card, string name)
    {
        if (card.SiteWork != SiteWork.None || _account is not { } account)
        {
            return;
        }

        card.SiteWork = SiteWork.Removing;
        try
        {
            var uploads = LibraryUploads;
            var key = ReplayUploads.KeyOf(card.Shelf, name);
            if (await Task.Run(() => uploads.Get(key)).ConfigureAwait(true) is not { } upload)
            {
                card.SiteUrl = string.Empty;
                return;
            }

            var error = await account.DeleteReplayAsync(upload.Id, upload.DeleteToken).ConfigureAwait(true);
            if (error is not (null or "NOT_FOUND"))
            {
                // Still on the site: the link and the token stay, so the delete can be tried again.
                Library.Failed(Loc.Or("Library.Site.DeleteError." + error, Loc.T("Library.Site.DeleteError.SERVER_ERROR")));
                return;
            }

            await Task.Run(() => uploads.Set(key, null)).ConfigureAwait(true);
            card.SiteUrl = string.Empty;
            Library.Succeeded(Loc.T(error is null ? "Library.Site.Removed" : "Library.Site.Gone"));
            await RefreshAccountCountsAsync().ConfigureAwait(true);
        }
        finally
        {
            card.SiteWork = SiteWork.None;
        }
    }

    internal void ShareLibraryCard(LibraryCard card, string name)
    {
        _libraryAction = ShareAsync(card.Shelf, name);
        _libraryAction.Forget("Replay share");
    }

    private async Task ShareAsync(ReplayShelf shelf, string name)
    {
        var cards = _cards;
        var files = LibraryFiles;
        var path = await Task.Run(() => files.ShareCopy(shelf, name, cards)).ConfigureAwait(true);
        if (path is null)
        {
            Library.Failed(Loc.T("Library.ShareFailed"));
        }
        else
        {
            Library.RequestShare(path);
        }
    }
}
