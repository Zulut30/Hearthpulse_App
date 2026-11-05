using System.IO.Compression;
using System.Net;
using System.Text;
using Hearthpulse.Core;
using Hearthpulse.Core.Accounts;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Replay;
using Hearthpulse.UI.Main;
using Hearthpulse.UI.Main.Match;

namespace Hearthpulse.Tests;

/// <summary>Web replays (plan 2.2): the upload copy, the site's answer, the remembered uploads and the sign-in check.</summary>
public sealed class ReplayUploadTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"hp-upload-{Guid.NewGuid():N}");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void The_upload_is_the_share_copy_without_the_post_game_lines_and_without_ranks_or_names()
    {
        var text = ReplayImportTests.Constructed();
        Assert.Contains(text.Split('\n'), line => line.StartsWith("K ", StringComparison.Ordinal) || line.StartsWith("N ", StringComparison.Ordinal));

        var upload = ReplayUpload.Prepare(text, CardDb.Empty)!;

        var lines = upload.Split('\n');
        Assert.Equal("HPR4", lines[0]);
        Assert.DoesNotContain(lines, line => line.StartsWith("K ", StringComparison.Ordinal) || line.StartsWith("N ", StringComparison.Ordinal));
        Assert.DoesNotContain("Legend", upload, StringComparison.Ordinal);
        Assert.Contains(lines, line => line.StartsWith("T ", StringComparison.Ordinal));

        // Gzip in base64, as the site takes it.
        using var gzip = new GZipStream(new MemoryStream(Convert.FromBase64String(ReplayUpload.Encode(upload))), CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        Assert.Equal(upload, reader.ReadToEnd());
    }

    [Fact]
    public void Only_an_https_link_with_an_id_and_a_token_is_an_upload()
    {
        Assert.NotNull(ReplayUploadResult.Parse("""{"id":"abcDEF1234","url":"https://hearthpulse.net/replay/abcDEF1234","deleteToken":"t"}"""));
        Assert.Null(ReplayUploadResult.Parse("""{"id":"abcDEF1234","url":"http://evil.example/x","deleteToken":"t"}"""));
        Assert.Null(ReplayUploadResult.Parse("""{"id":"short","url":"https://hearthpulse.net/replay/short","deleteToken":"t"}"""));
        Assert.Null(ReplayUploadResult.Parse("not json"));
    }

    [Fact]
    public void Uploads_are_remembered_by_file_name_and_forgotten_after_the_delete()
    {
        var uploads = new ReplayUploads(Path.Combine(_dir, "uploads.json"));
        var upload = new ReplayUploadResult("abcDEF1234", "https://hearthpulse.net/replay/abcDEF1234", "token");

        uploads.Set("game.hpr", upload);
        Assert.Equal(upload.Url, new ReplayUploads(Path.Combine(_dir, "uploads.json")).Get("game.hpr")?.Url);

        uploads.Set("game.hpr", null);
        Assert.Null(uploads.Get("game.hpr"));
    }

    [Fact]
    public async Task Without_a_site_sign_in_nothing_is_uploaded_and_the_card_says_why()
    {
        Directory.CreateDirectory(_dir);
        var vm = new MainViewModel(new HistoryStore(Path.Combine(_dir, "h.db")), new AppSettings(), TestDataFolder.App)
        {
            LibraryUploads = new ReplayUploads(Path.Combine(_dir, "uploads.json")),
        };
        var card = new LibraryCard(ReplayShelf.Recent, "game.hpr", null, battlegrounds: false) { HasReplay = true };

        vm.UploadLibraryCard(card, "game.hpr");
        await vm.LibraryIdleAsync();

        Assert.False(card.OnSite);
        Assert.Equal(Loc.T("Library.Site.SignIn"), vm.Library.Status);
    }

    [Fact]
    public async Task The_client_sends_the_replay_with_the_sign_in_and_deletes_it_with_the_token()
    {
        var site = new FakeReplaySite();
        var client = new AccountClient(new HttpClient(site), FakeReplaySite.Base);

        var (status, body) = await client.PostReplayAsync("acc", "H4sI");
        Assert.Equal(201, status);
        Assert.Equal("abcDEF1234", ReplayUploadResult.Parse(body)?.Id);
        var post = Assert.Single(site.Posts);
        Assert.Equal("acc", post.Bearer);
        Assert.Equal("""{"v":1,"hpr":"H4sI"}""", post.Body);

        Assert.Equal(204, await client.DeleteReplayAsync("abcDEF1234", "secret"));
        Assert.Equal(("abcDEF1234", "secret"), Assert.Single(site.Deletes));

        site.Offline = true;
        Assert.Equal("OFFLINE", (await Assert.ThrowsAsync<AccountException>(() => client.PostReplayAsync("acc", "H4sI"))).Code);
        Assert.Equal("OFFLINE", (await Assert.ThrowsAsync<AccountException>(() => client.DeleteReplayAsync("abcDEF1234", "secret"))).Code);
    }

    [Theory]
    [InlineData(201, null)]
    [InlineData(401, "SIGNED_OUT")]
    [InlineData(403, "SIGNED_OUT")]
    [InlineData(409, "QUOTA_EXCEEDED")]
    [InlineData(413, "TOO_LARGE")]
    [InlineData(429, "RATE_LIMITED")]
    [InlineData(400, "REFUSED")]
    [InlineData(404, "NOT_AVAILABLE")]
    [InlineData(500, "SERVER_ERROR")]
    [InlineData(0, "OFFLINE")]
    public async Task Upload_answers_map_to_the_card_s_error_texts(int status, string? error)
    {
        var site = new FakeReplaySite();
        var account = await SignedInAsync(site);
        site.UploadStatus = status;
        site.Offline = status == 0;

        var (result, code) = await account.UploadReplayAsync("H4sI");

        Assert.Equal(error, code);
        Assert.Equal(error is null, result is not null);
        if (error is not null)
        {
            Assert.NotEqual("Library.Site.Error." + error, Loc.T("Library.Site.Error." + error));
        }
    }

    [Theory]
    [InlineData(204, null)]
    [InlineData(404, "NOT_FOUND")]
    [InlineData(429, "RATE_LIMITED")]
    [InlineData(500, "SERVER_ERROR")]
    [InlineData(0, "OFFLINE")]
    public async Task Delete_answers_tell_gone_from_still_there(int status, string? error)
    {
        var site = new FakeReplaySite { DeleteStatus = status, Offline = status == 0 };
        var account = new AccountService(new AccountClient(new HttpClient(site), FakeReplaySite.Base), new TokenStore(Path.Combine(_dir, "a.dat")));

        Assert.Equal(error, await account.DeleteReplayAsync("abcDEF1234", "secret"));
    }

    [Fact]
    public async Task An_upload_puts_the_link_on_the_card_and_the_clipboard_and_a_delete_takes_it_off()
    {
        var site = new FakeReplaySite();
        var (vm, uploads, name) = await LibraryAsync(site);
        var copied = new List<string>();
        vm.Library.CopyLinkRequested += copied.Add;
        var card = new LibraryCard(ReplayShelf.Recent, name, null, battlegrounds: false) { HasReplay = true };

        vm.UploadLibraryCard(card, name);
        await vm.LibraryIdleAsync();

        Assert.Equal("https://hearthpulse.net/replay/abcDEF1234", card.SiteUrl);
        Assert.Equal([card.SiteUrl], copied);
        Assert.Equal(SiteWork.None, card.SiteWork);
        // The starred copy is the same game: it shows the same link.
        Assert.Equal(card.SiteUrl, uploads.Get(ReplayUploads.KeyOf(ReplayShelf.Saved, name))?.Url);
        Assert.DoesNotContain(Directory.GetFiles(_dir), f => f.EndsWith(".tmp", StringComparison.Ordinal));

        // A failed delete keeps the link and the token, so it can be tried again.
        site.DeleteStatus = 500;
        vm.RemoveLibraryCardFromSite(card, name);
        await vm.LibraryIdleAsync();
        Assert.True(card.OnSite);
        Assert.Equal(Loc.T("Library.Site.DeleteError.SERVER_ERROR"), vm.Library.Status);

        site.DeleteStatus = 204;
        vm.RemoveLibraryCardFromSite(card, name);
        await vm.LibraryIdleAsync();
        Assert.False(card.OnSite);
        Assert.Null(uploads.Get(ReplayUploads.KeyOf(ReplayShelf.Recent, name)));
        Assert.Equal("token-1", site.Deletes[^1].Token);
        Assert.Equal(Loc.T("Library.Site.Removed"), vm.Library.Status);
    }

    [Theory]
    [InlineData("Ranked", "GT_RANKED", null, true, true)]
    [InlineData("Ranked", "GT_RANKED", null, false, false)]
    [InlineData("Brawl", "GT_TAVERNBRAWL", null, true, false)]
    [InlineData("Ranked", "GT_RANKED", Hearthpulse.Core.Migration.HdtImport.Source, true, false)]
    public async Task The_game_s_profile_id_goes_only_for_a_game_the_profile_upload_sends(string mode, string gameType, string? source, bool toProfile, bool sent)
    {
        var site = new FakeReplaySite();
        var key = $"20261101120000|Zulut#2187|{gameType}";
        Directory.CreateDirectory(_dir);
        var history = new HistoryStore(Path.Combine(_dir, "h.db"));
        var start = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Local);
        history.Add(new GameRecord
        {
            Key = key, StartedAt = start, EndedAt = start.AddMinutes(9), Mode = mode, GameType = gameType, Format = "FT_STANDARD",
            Result = "Win", PlayerName = "Zulut#2187", PlayerClass = "MAGE", OpponentClass = "ROGUE", HeroCardId = "HERO_08",
            Source = source ?? "live",
        });
        var (vm, _, name) = await LibraryAsync(site, history);
        vm.Settings.UploadGamesToProfile = toProfile;
        await vm.RefreshAsync();
        var card = new LibraryCard(ReplayShelf.Recent, name, key, battlegrounds: false) { HasReplay = true };

        vm.UploadLibraryCard(card, name);
        await vm.LibraryIdleAsync();

        var body = Assert.Single(site.Posts).Body;
        Assert.DoesNotContain("Zulut", body, StringComparison.Ordinal);
        Assert.Equal(sent, body.Contains("\"gameId\"", StringComparison.Ordinal));
        if (sent)
        {
            Assert.Contains($"\"gameId\":\"{ProfileEvents.EventId("game|20261101120000|GT_RANKED")}\"", body, StringComparison.Ordinal);
        }

        Assert.True(card.OnSite);
    }

    /// <summary>
    /// The owner's bug: the site has no replay route yet (404). The card says that uploads are not on yet, not that the
    /// site failed, and the log keeps the answer.
    /// </summary>
    [Fact]
    public async Task An_upload_the_site_does_not_take_yet_says_so_and_is_logged()
    {
        var site = new FakeReplaySite { UploadStatus = 404 };
        var (vm, uploads, name) = await LibraryAsync(site);
        var card = new LibraryCard(ReplayShelf.Recent, name, null, battlegrounds: false) { HasReplay = true };

        vm.UploadLibraryCard(card, name);
        await vm.LibraryIdleAsync();

        Assert.False(card.OnSite);
        Assert.Null(uploads.Get(ReplayUploads.KeyOf(ReplayShelf.Recent, name)));
        Assert.Equal(Loc.T("Library.Site.Error.NOT_AVAILABLE"), vm.Library.Status);
        Assert.NotEqual(Loc.T("Library.Site.Error.SERVER_ERROR"), vm.Library.Status);
        Assert.Contains("replay upload answered 404", TestDataFolder.ReadLog(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The owner's bug: the question in the card is answered with the action it asks about - "Upload", "Remove from
    /// site" or "Delete" - not always "Delete".
    /// </summary>
    [Fact]
    public async Task The_confirm_button_names_the_action_it_confirms()
    {
        var site = new FakeReplaySite();
        var (vm, _, name) = await LibraryAsync(site);
        var card = new LibraryCard(ReplayShelf.Recent, name, null, battlegrounds: false) { HasReplay = true };

        vm.Library.SiteAction.Execute(card);
        Assert.Equal(Loc.F("Library.Site.ConfirmUpload", ReplayUpload.KeepDays), card.ConfirmText);
        Assert.Equal(Loc.T("Library.Site.ConfirmUploadYes"), card.ConfirmLabel);
        vm.Library.Confirm.Execute(card);
        await vm.LibraryIdleAsync();
        Assert.True(card.OnSite);

        vm.Library.SiteAction.Execute(card);
        Assert.Equal(Loc.T("Library.Site.ConfirmRemoveYes"), card.ConfirmLabel);
        vm.Library.Cancel.Execute(card);

        var imported = new LibraryCard(ReplayShelf.Imported, "game.hpr", null, battlegrounds: false) { HasReplay = true };
        vm.Library.Delete.Execute(imported);
        Assert.Equal(Loc.T("Library.ConfirmYes"), imported.ConfirmLabel);
        Assert.NotEqual(Loc.T("Library.ConfirmYes"), Loc.T("Library.Site.ConfirmUploadYes"));
    }

    /// <summary>All four places with the question (the library card and the match header, in both UIs) show its label.</summary>
    [Theory]
    [InlineData("src/Hearthpulse.App/Main/Match/ReplayLibraryView.xaml", "{Binding ConfirmLabel}")]
    [InlineData("src/Hearthpulse.App/Main/Match/MatchView.xaml", "{Binding HeaderCard.ConfirmLabel}")]
    [InlineData("src/Hearthpulse.UI/Main/Match/ReplayLibraryView.axaml", "{Binding ConfirmLabel}")]
    [InlineData("src/Hearthpulse.UI/Main/Match/MatchView.axaml", "{Binding HeaderCard.ConfirmLabel}")]
    public void Every_view_of_the_question_binds_its_confirm_label(string view, string binding)
    {
        var markup = File.ReadAllText(Path.Combine(RepoFiles.Root, view));

        Assert.Contains($"Content=\"{binding}\"", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Library.ConfirmYes", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task While_uploading_the_button_cancels_and_a_second_upload_never_starts()
    {
        var site = new FakeReplaySite { Hold = new TaskCompletionSource() };
        var (vm, _, name) = await LibraryAsync(site);
        var card = new LibraryCard(ReplayShelf.Recent, name, null, battlegrounds: false) { HasReplay = true };

        vm.UploadLibraryCard(card, name);
        await site.Reached.Task;
        Assert.Equal(SiteWork.Uploading, card.SiteWork);
        Assert.Equal(Loc.T("Library.Site.Uploading"), card.SiteTip);
        vm.UploadLibraryCard(card, name);

        vm.Library.SiteAction.Execute(card);
        await vm.LibraryIdleAsync();

        Assert.Single(site.Posts);
        Assert.False(card.OnSite);
        Assert.Equal(SiteWork.None, card.SiteWork);
        Assert.Equal(Loc.T("Library.Site.Cancelled"), vm.Library.Status);
    }

    [Fact]
    public void An_imported_replay_is_someone_else_s_game_and_has_no_upload()
    {
        Directory.CreateDirectory(_dir);
        var vm = new MainViewModel(new HistoryStore(Path.Combine(_dir, "h.db")), new AppSettings(), TestDataFolder.App);
        var card = new LibraryCard(ReplayShelf.Imported, "game.hpr", null, battlegrounds: false) { HasReplay = true };

        vm.Library.SiteAction.Execute(card);

        Assert.False(card.CanUpload);
        Assert.False(card.Confirming);
        Assert.NotEqual(ReplayUploads.KeyOf(ReplayShelf.Imported, "game.hpr"), ReplayUploads.KeyOf(ReplayShelf.Recent, "game.hpr"));
    }

    private async Task<AccountService> SignedInAsync(FakeReplaySite site)
    {
        Directory.CreateDirectory(_dir);
        var store = new TokenStore(Path.Combine(_dir, "account.dat"));
        var client = new AccountClient(new HttpClient(site), FakeReplaySite.Base);
        Assert.True(store.Save("ref-1", new AccountUser("u-1", "Player", Entitlements.None), DateTime.UtcNow.AddDays(30), client.Site));
        var account = new AccountService(client, store);
        await account.RestoreAsync();
        Assert.True(account.SignedIn);
        return account;
    }

    private async Task<(MainViewModel Vm, ReplayUploads Uploads, string Name)> LibraryAsync(FakeReplaySite site, HistoryStore? history = null)
    {
        var account = await SignedInAsync(site);
        var files = new ReplayLibrary(new ReplayStore(Path.Combine(_dir, "replays")), Path.Combine(_dir, "saved"), Path.Combine(_dir, "imported"));
        var name = files.Recent.Save("20261101120000|me#1|GT_RANKED", ReplayImportTests.Constructed())!;
        var uploads = new ReplayUploads(Path.Combine(_dir, "uploads.json"));
        var vm = new MainViewModel(history ?? new HistoryStore(Path.Combine(_dir, "h.db")), new AppSettings(), TestDataFolder.App) { LibraryFiles = files, LibraryUploads = uploads };
        vm.AttachAccount(account);
        return (vm, uploads, name);
    }

    /// <summary>The site's token, profile and replay endpoints in miniature.</summary>
    private sealed class FakeReplaySite : HttpMessageHandler
    {
        public const string Base = "https://site.test/api/v1/";

        public bool Offline { get; set; }

        public int UploadStatus { get; set; } = 201;

        public int DeleteStatus { get; set; } = 204;

        /// <summary>When set, an upload waits for it (after signalling <see cref="Reached"/>).</summary>
        public TaskCompletionSource? Hold { get; init; }

        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<(string? Bearer, string Body)> Posts { get; } = [];

        public List<(string Id, string Token)> Deletes { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Offline)
            {
                throw new HttpRequestException("offline");
            }

            var path = request.RequestUri!.AbsolutePath["/api/v1/".Length..];
            if (path == "oauth/token")
            {
                return Json(200, """{"access_token":"acc","token_type":"Bearer","refresh_token":"ref-2","expires_in":900}""");
            }

            if (path == "me")
            {
                return Json(200, """{"user":{"id":"u-1","name":"Player"}}""");
            }

            if (path == "tracker/replays" && request.Method == HttpMethod.Post)
            {
                Posts.Add((request.Headers.Authorization?.Parameter, await request.Content!.ReadAsStringAsync(cancellationToken)));
                if (Hold is not null)
                {
                    Reached.TrySetResult();
                    await Hold.Task.WaitAsync(cancellationToken);
                }

                return UploadStatus == 201
                    ? Json(201, """{"id":"abcDEF1234","url":"https://hearthpulse.net/replay/abcDEF1234","deleteToken":"token-1","events":6}""")
                    : Json(UploadStatus, """{"error":{"code":"X","message":"x"}}""");
            }

            if (path.StartsWith("tracker/replays/", StringComparison.Ordinal) && request.Method == HttpMethod.Delete)
            {
                Deletes.Add((path["tracker/replays/".Length..], request.Headers.GetValues("X-Delete-Token").Single()));
                return new HttpResponseMessage((HttpStatusCode)DeleteStatus);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(int status, string body) =>
            new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
