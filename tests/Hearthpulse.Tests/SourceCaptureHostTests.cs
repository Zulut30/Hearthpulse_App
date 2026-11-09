using System.Net;
using System.Net.Http.Json;
using Hearthpulse.Core;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Hosting;
using Hearthpulse.Core.Memory;
using Hearthpulse.Core.SourceCapture;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.Tests;

public sealed class SourceCaptureHostTests
{
    [Fact]
    public async Task An_old_notice_cannot_start_capture_but_the_current_notice_can()
    {
        using var scratch = new SourceScratch(); using var memory = new HearthstoneMemory();
        var session = new GameSession(HistoryStore.InMemory(), memory) { ReadsMemory = false };
        var settings = new AppSettings { PoolEndpoint = SourceScratch.Endpoint, GameStatsNoticeShown = true };
        using var handler = new DeletionHandler(); using var http = new HttpClient(handler);
        var saved = 0;
        await using var host = new SourceCaptureHost(settings, http, session, scratch.Path, () => saved++);
        await host.ApplySettingsAsync();
        Assert.False(host.Capturing); Assert.Equal(0, handler.Requests);
        settings.SourceCaptureNoticeVersion = SourceCaptureClient.NoticeVersion;
        await host.ApplySettingsAsync();
        Assert.True(host.Capturing); Assert.Equal(2, saved);
        Assert.Equal(64, settings.PoolSecret?.Length);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task Opt_out_persists_forget_and_does_not_claim_deletion_before_confirmation()
    {
        using var scratch = new SourceScratch(); using var memory = new HearthstoneMemory();
        var session = new GameSession(HistoryStore.InMemory(), memory) { ReadsMemory = false };
        var settings = new AppSettings { PoolEndpoint = SourceScratch.Endpoint, SourceCaptureNoticeVersion = SourceCaptureClient.NoticeVersion };
        using var handler = new DeletionHandler(); using var http = new HttpClient(handler);
        await using var host = new SourceCaptureHost(settings, http, session, scratch.Path, () => { });
        await host.ApplySettingsAsync(); Assert.True(host.Capturing);
        settings.GameStatsOff = true;
        await host.ApplySettingsAsync();
        Assert.False(host.Capturing); Assert.Equal(1, host.State.PendingDeletions); Assert.Equal(0, handler.Requests);
        await host.SendPendingAsync();
        Assert.Equal(1, handler.Requests); Assert.Equal(0, host.State.PendingDeletions);
        settings.GameStatsOff = false;
        await host.ApplySettingsAsync(); Assert.False(host.Capturing);
        await host.ApplySettingsAsync(explicitEnable: true); Assert.True(host.Capturing);
    }

    [Fact]
    public async Task Settings_save_failure_never_grants_capture_or_source_delivery()
    {
        using var scratch = new SourceScratch(); using var memory = new HearthstoneMemory();
        var session = new GameSession(HistoryStore.InMemory(), memory) { ReadsMemory = false };
        var settings = new AppSettings { PoolEndpoint = SourceScratch.Endpoint, SourceCaptureNoticeVersion = SourceCaptureClient.NoticeVersion };
        using var handler = new DeletionHandler(); using var http = new HttpClient(handler);
        await using var host = new SourceCaptureHost(settings, http, session, scratch.Path, () => throw new IOException("synthetic_save_failure"));
        await host.ApplySettingsAsync(); await host.SendPendingAsync();
        Assert.False(host.Capturing); Assert.Equal("unavailable", host.State.Status); Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task A_real_settings_write_failure_never_enables_source_capture()
    {
        using var scratch = new SourceScratch(); using var memory = new HearthstoneMemory();
        var session = new GameSession(HistoryStore.InMemory(), memory) { ReadsMemory = false };
        var settings = new AppSettings { PoolEndpoint = SourceScratch.Endpoint, SourceCaptureNoticeVersion = SourceCaptureClient.NoticeVersion };
        using var handler = new DeletionHandler(); using var http = new HttpClient(handler);
        var blocked = Path.Combine(scratch.Path, "missing", "settings.json");
        await using var host = new SourceCaptureHost(settings, http, session, Path.Combine(scratch.Path, "spool"), () => settings.SaveRequired(blocked));
        await host.ApplySettingsAsync();
        Assert.False(host.Capturing);
        Assert.Equal("unavailable", host.State.Status);
    }

    [Fact]
    public async Task Opt_out_remains_revoked_after_restart_even_when_settings_cannot_be_saved()
    {
        using var scratch = new SourceScratch(); using var memory = new HearthstoneMemory();
        var session = new GameSession(HistoryStore.InMemory(), memory) { ReadsMemory = false };
        var settings = new AppSettings { PoolEndpoint = SourceScratch.Endpoint, SourceCaptureNoticeVersion = SourceCaptureClient.NoticeVersion };
        using var handler = new DeletionHandler(); using var http = new HttpClient(handler);
        var failSave = false;
        await using (var host = new SourceCaptureHost(settings, http, session, scratch.Path, () => { if (failSave) throw new IOException("synthetic_save_failure"); }))
        {
            await host.ApplySettingsAsync();
            Assert.True(host.Capturing);
            session.OnSessionStarted("synthetic", new(2026, 11, 9), attachedMidSession: false);
            session.OnLines("Power.log", ReplayRecorderTests.Setup.Select(b => "D 12:30:00.0000000 GameState." + b).ToArray(), catchingUp: false);
            session.StopSourceCapture();
            await host.ApplySettingsAsync(); // Flush the captured source before testing withdrawal.
            Assert.Equal(1, host.State.PendingCaptures);
            failSave = true; settings.GameStatsOff = true;
            await host.ApplySettingsAsync();
            Assert.False(host.Capturing);
            Assert.Equal(1, host.State.PendingDeletions);
            Assert.Empty(Directory.GetFiles(scratch.Path, "*.jsonl", SearchOption.AllDirectories));
        }
        await using var reopened = await SourceCaptureQueue.OpenAsync(scratch.Path);
        Assert.Null(reopened.Generation);
        Assert.Equal(1, reopened.State.PendingDeletions);
        settings.GameStatsOff = false; // Last successfully saved settings still said enabled.
        await reopened.ConfigureAsync(settings);
        Assert.False(reopened.Capturing);
        Assert.Null(reopened.Generation);
    }

    [Fact]
    public async Task Graceful_shutdown_preserves_the_open_unpicked_draft_offer()
    {
        using var scratch = new SourceScratch(); using var memory = new HearthstoneMemory();
        var session = new GameSession(HistoryStore.InMemory(), memory) { ReadsMemory = false, Clock = () => new(2026, 11, 9) };
        var settings = new AppSettings { PoolEndpoint = SourceScratch.Endpoint, SourceCaptureNoticeVersion = SourceCaptureClient.NoticeVersion };
        using var handler = new DeletionHandler(); using var http = new HttpClient(handler);
        await using (var host = new SourceCaptureHost(settings, http, session, scratch.Path, () => { }))
        {
            await host.ApplySettingsAsync();
            session.OnLines("LoadingScreen.log", ["D 12:30:00.0000000 LoadingScreen.OnSceneLoaded() - prevMode=HUB currMode=DRAFT"], false);
            session.SetDraftOffer(["DRAFT_A", "DRAFT_B", "DRAFT_C"]);
        }
        await using var reopened = await SourceCaptureQueue.OpenAsync(scratch.Path);
        Assert.Equal(1, reopened.State.PendingCaptures);
        var source = string.Join('\n', Directory.GetFiles(scratch.Path, "*.jsonl", SearchOption.AllDirectories).Select(File.ReadAllText));
        Assert.Contains("draft_choice", source);
        Assert.Contains("DRAFT_B", source);
        Assert.Contains("\"picked\":null", source);
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(source));
        var result = await new Hearthpulse.Core.SourceCapture.Processing.SourceCaptureParser().ParseAsync(stream);
        Assert.Equal("partial", result.Status);
        Assert.Equal(1, result.Summary.DraftChoices);
        Assert.False(result.Summary.CompleteForAggregation);
    }

    [Fact]
    public async Task Rapid_off_then_explicit_on_is_serialized_and_retains_old_generation_deletion()
    {
        using var scratch = new SourceScratch(); using var memory = new HearthstoneMemory();
        var session = new GameSession(HistoryStore.InMemory(), memory) { ReadsMemory = false };
        var settings = new AppSettings { PoolEndpoint = SourceScratch.Endpoint, SourceCaptureNoticeVersion = SourceCaptureClient.NoticeVersion };
        using var handler = new DeletionHandler(); using var http = new HttpClient(handler);
        await using var host = new SourceCaptureHost(settings, http, session, scratch.Path, () => { });
        await host.ApplySettingsAsync();
        settings.GameStatsOff = true; var off = host.ApplySettingsAsync();
        settings.GameStatsOff = false; var on = host.ApplySettingsAsync(explicitEnable: true);
        await Task.WhenAll(off, on);
        Assert.True(host.Capturing); Assert.Equal(1, host.State.PendingDeletions);
        await host.SendPendingAsync();
        Assert.Equal(1, handler.Requests); Assert.Equal(0, host.State.PendingDeletions);
    }

    private sealed class DeletionHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Assert.EndsWith("/v1/captures/forget", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { ok = true, status = "deleted" }) });
        }
    }
}
