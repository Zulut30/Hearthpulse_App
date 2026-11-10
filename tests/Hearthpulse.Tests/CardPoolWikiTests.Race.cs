using System.Collections.Concurrent;
using Hearthpulse.Core.Api;
using Hearthpulse.Core.CardPool;
using Hearthpulse.Core.Cards;

namespace Hearthpulse.Tests;

public sealed partial class CardPoolWikiTests
{
    /// <summary>
    /// Two loads of one card can be on their way at once: the overlay's first <c>Build</c> asks for the card in the
    /// background while a caller awaits <c>LoadAsync</c> itself. Each parses its own answer; the one that finishes
    /// second must not replace the answer the engine already built a view from (that made a second view and a second
    /// <c>Changed</c> for the same pool). The server holds both requests, so the order here is fixed.
    /// </summary>
    [Fact]
    public async Task A_second_load_of_the_same_card_that_finishes_later_keeps_the_first_answer_and_its_view()
    {
        using var server = new Held();
        var wiki = new WikiPoolSource(new HearthpulseData(new HearthpulseApi(new HttpClient(server)), CacheWith("BE_298", TimeSpan.FromDays(2))));
        var engine = new CardPoolEngine(Catalog.Value, CardDb.Empty, wiki: wiki);
        var changed = 0;
        wiki.Changed += () => Interlocked.Increment(ref changed);

        var first = wiki.LoadAsync("BE_298");
        await server.ArrivedAsync();
        var second = wiki.LoadAsync("BE_298");
        await server.ArrivedAsync();

        server.ReleaseNext();
        var answer = await first;
        var view = engine.Build("BE_298", DeathKnight, PoolTrigger.Choice)!;
        server.ReleaseNext();
        var late = await second;

        Assert.Equal(WikiPools.Source, view.Source);
        Assert.Same(answer, late);
        Assert.True(wiki.TryGet("BE_298", out var kept));
        Assert.Same(answer, kept);
        Assert.Same(view, engine.Build("BE_298", DeathKnight, PoolTrigger.Choice));
        Assert.Equal(1, Volatile.Read(ref changed));
        Assert.Equal(1, engine.WikiListBuilds);
    }

    /// <summary>The API answering 503 (the stale cache is read then), each request only when the test lets it go, in the order they came.</summary>
    private sealed class Held : HttpMessageHandler
    {
        private readonly ConcurrentQueue<TaskCompletionSource> _waiting = new();
        private readonly SemaphoreSlim _arrived = new(0);

        /// <summary>Completes when one more request has reached the server.</summary>
        public Task ArrivedAsync() => _arrived.WaitAsync(TimeSpan.FromSeconds(30)).ContinueWith(
            static t => Assert.True(t.Result, "The request did not reach the server"), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        public void ReleaseNext()
        {
            Assert.True(_waiting.TryDequeue(out var gate));
            gate.SetResult();
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiting.Enqueue(gate);
            _arrived.Release();
            await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{\"detail\":\"maintenance\"}") };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _arrived.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
