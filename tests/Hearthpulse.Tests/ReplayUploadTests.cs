using System.IO.Compression;
using System.Text;
using Hearthpulse.Core;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
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
        var vm = new MainViewModel(new HistoryStore(Path.Combine(_dir, "h.db")), new AppSettings())
        {
            LibraryUploads = new ReplayUploads(Path.Combine(_dir, "uploads.json")),
        };
        var card = new LibraryCard(ReplayShelf.Recent, "game.hpr", null, battlegrounds: false) { HasReplay = true };

        vm.UploadLibraryCard(card, "game.hpr");
        await vm.LibraryIdleAsync();

        Assert.False(card.OnSite);
        Assert.Equal(Hearthpulse.Core.Localization.Loc.T("Library.Site.SignIn"), vm.Library.Status);
    }
}
