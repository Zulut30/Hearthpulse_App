using System.Net;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Updates;

namespace Hearthpulse.Tests;

/// <summary>
/// The hosts' side of the update loop (TASK-2026-11-09-update-delivery, review M1/M2/m2): "Update now" and the exit stop
/// the loop and wait for it before installing; a round that throws still ends in a result and the loop goes on; the
/// result's sentence follows a language switch.
/// </summary>
[Collection(nameof(UpdateStagingCollection))]
public sealed class UpdateLoopTests
{
    [Fact]
    public void Stop_cancels_the_loop_and_waits_until_it_has_ended()
    {
        using var loop = new UpdateLoop();
        var ended = false;
        using var started = new ManualResetEventSlim();
        loop.Start(async token =>
        {
            started.Set();
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
                // A download winding down after the cancellation: Stop must not return before this is done.
                Thread.Sleep(200);
                ended = true;
                throw;
            }
        });
        Assert.True(loop.Running);
        // Stopped before the pool ran it, a loop never starts at all; this one is mid-download.
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));

        Assert.True(loop.Stop(TimeSpan.FromSeconds(10)));

        Assert.True(ended);
        Assert.False(loop.Running);
    }

    [Fact]
    public void Stop_gives_up_after_the_wait_on_a_loop_that_ignores_the_token()
    {
        using var loop = new UpdateLoop();
        var never = new TaskCompletionSource();
        using var started = new ManualResetEventSlim();
        loop.Start(_ =>
        {
            started.Set();
            return never.Task;
        });
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));

        Assert.False(loop.Stop(TimeSpan.FromMilliseconds(100)));

        never.SetResult();
    }

    [Fact]
    public void A_stopped_loop_does_not_start_and_a_second_stop_is_harmless()
    {
        using var loop = new UpdateLoop();
        Assert.True(loop.Stop(TimeSpan.Zero));
        var started = false;
        loop.Start(_ =>
        {
            started = true;
            return Task.CompletedTask;
        });

        Assert.False(loop.Running);
        Assert.True(loop.Stop(TimeSpan.Zero));
        Assert.False(started);
    }

    [Fact]
    public async Task A_round_that_throws_ends_in_a_failed_check_and_the_loop_goes_on()
    {
        // An exception the check does not expect (here UnauthorizedAccessException from the handler): "Check now"
        // still gets Checked, then Failed, and a later request runs a new round.
        var now = DateTimeOffset.UtcNow;
        var trigger = new UpdateTrigger(() => now);
        var events = new UpdateTests.Events();
        using var http = new HttpClient(new Throwing());
        using var stop = new CancellationTokenSource();
        var task = Task.Run(() => UpdateService.WatchAsync(http, notifyOnly: false, automatic: false, events.Callbacks, trigger,
            UpdateTests.Options(UpdateTests.Context()), stop.Token));
        try
        {
            Assert.True(trigger.Request());
            await events.WaitAsync(log => log.Count >= 2);
            Assert.Equal("checked Failed", events.Log[0]);
            Assert.StartsWith("failed ", events.Log[1], StringComparison.Ordinal);
            Assert.Contains("locked by the test", events.Texts[0], StringComparison.Ordinal);

            now = now.AddMinutes(2); // past the 60 s between two requests
            await UpdateTests.WaitUntil(trigger.Request);
            await events.WaitAsync(log => log.Count >= 4);
            Assert.Equal("checked Failed", events.Log[2]);
            Assert.False(task.IsCompleted);
        }
        finally
        {
            await stop.CancelAsync();
            await task.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    private sealed class Throwing : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new UnauthorizedAccessException("locked by the test");
    }
}

/// <summary>A check's sentence follows a language switch (review m2); it switches the global language, so it runs alone.</summary>
[Collection(nameof(GlobalLanguage))]
public sealed class UpdateCheckLanguageTests
{
    [Fact]
    public async Task A_check_s_sentence_follows_the_language()
    {
        using var http = new HttpClient(new NotFound());
        Loc.Instance.SetLanguage(Loc.English);
        var check = await UpdateService.CheckForUpdateAsync(http);
        Assert.Equal(UpdateCheckOutcome.Failed, check.Outcome);
        var english = check.Text();
        Assert.Equal(check.Message, english);
        try
        {
            Loc.Instance.SetLanguage(Loc.Russian);
            Assert.NotEqual(english, check.Text());
            Assert.Equal(Loc.F("Core.Update.GitHubFailed", 404, Loc.T("Core.Update.NoRelease")), check.Text());
        }
        finally
        {
            Loc.Instance.SetLanguage(Loc.English);
        }
    }

    private sealed class NotFound : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
