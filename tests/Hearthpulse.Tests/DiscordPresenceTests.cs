using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using Hearthpulse.Core.Discord;
using Microsoft.Extensions.Time.Testing;

namespace Hearthpulse.Tests;

/// <summary>
/// The Discord status (<c>docs/discord.md</c>, plan 3.4): the IPC frames (handshake, SET_ACTIVITY), cut texts, sending only
/// changes at most every 15 s with the latest kept, clearing when switched off, and staying off without an app id.
/// A fake Discord app on an in-memory stream; the clock is the test's.
/// </summary>
public sealed class DiscordPresenceTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    /// <summary>The Discord app's side of the pipe: answers the handshake with READY and each frame with its echo.</summary>
    private sealed class FakeDiscord : Stream
    {
        private readonly MemoryStream _toClient = new();
        private readonly Lock _gate = new();
        private TaskCompletionSource<JsonNode?> _next = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<JsonNode?> Activities { get; } = [];

        public string? ClientId { get; private set; }

        /// <summary>The next activity the client sets (null clears).</summary>
        public Task<JsonNode?> Next()
        {
            lock (_gate)
            {
                return _next.Task;
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            var op = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset));
            var length = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset + 4));
            var body = JsonNode.Parse(Encoding.UTF8.GetString(buffer, offset + 8, length))!;
            if (op == DiscordIpc.OpHandshake)
            {
                ClientId = body["client_id"]!.GetValue<string>();
                Answer(new JsonObject { ["cmd"] = "DISPATCH", ["evt"] = "READY" });
                return;
            }

            Answer(new JsonObject { ["cmd"] = "SET_ACTIVITY", ["nonce"] = body["nonce"]!.GetValue<string>() });
            var activity = body["args"]!["activity"];
            TaskCompletionSource<JsonNode?> done;
            lock (_gate)
            {
                Activities.Add(activity);
                done = _next;
                _next = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            done.SetResult(activity);
        }

        private void Answer(JsonObject frame)
        {
            var body = Encoding.UTF8.GetBytes(frame.ToJsonString());
            var header = new byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(header, DiscordIpc.OpFrame);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), body.Length);
            lock (_gate)
            {
                var at = _toClient.Position;
                _toClient.Seek(0, SeekOrigin.End);
                _toClient.Write(header);
                _toClient.Write(body);
                _toClient.Position = at;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            lock (_gate)
            {
                return _toClient.Read(buffer, offset, count);
            }
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static DiscordIpc Ipc(FakeDiscord discord, int pipe = 0) =>
        new((n, _) => Task.FromResult<Stream?>(n == pipe ? discord : null));

    [Fact]
    public async Task The_client_says_hello_on_the_first_pipe_that_answers_and_sets_the_activity()
    {
        var discord = new FakeDiscord();
        await using var ipc = Ipc(discord, pipe: 2);

        Assert.True(await ipc.ConnectAsync("123", CancellationToken.None));
        Assert.Equal("123", discord.ClientId);

        var started = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
        await ipc.SetActivityAsync(new DiscordActivity("Ranked · Standard", "Priest vs Mage", started), 42, CancellationToken.None);
        var sent = Assert.Single(discord.Activities)!;
        Assert.Equal("Ranked · Standard", sent["details"]!.GetValue<string>());
        Assert.Equal("Priest vs Mage", sent["state"]!.GetValue<string>());
        Assert.Equal(started.ToUnixTimeSeconds(), sent["timestamps"]!["start"]!.GetValue<long>());
    }

    [Fact]
    public async Task No_discord_app_means_no_connection()
    {
        await using var ipc = new DiscordIpc((_, _) => Task.FromResult<Stream?>(null));
        Assert.False(await ipc.ConnectAsync("123", CancellationToken.None));
        Assert.False(ipc.Connected);
    }

    [Fact]
    public void Lines_are_cut_to_discords_limits()
    {
        Assert.Null(DiscordIpc.Cut("  "));
        Assert.Equal("A ", DiscordIpc.Cut("A"));
        var cut = DiscordIpc.Cut(new string('x', 300))!;
        Assert.Equal(DiscordIpc.MaxText, cut.Length);
        Assert.EndsWith("…", cut, StringComparison.Ordinal);
        Assert.False(DiscordIpc.Json(new DiscordActivity("Menu", null, null)).ContainsKey("state"));
    }

    [Fact]
    public async Task Only_changes_are_sent_at_most_every_15_seconds_and_the_latest_is_kept()
    {
        var discord = new FakeDiscord();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero));
        await using var presence = new DiscordPresence(Ipc(discord), "123", clock);

        var first = discord.Next();
        presence.Show(enabled: true, new DiscordActivity("In the menu", null, null));
        Assert.Equal("In the menu", (await first.WaitAsync(Wait))!["details"]!.GetValue<string>());
        await presence.Pending.WaitAsync(Wait);
        Assert.Equal(DiscordState.Connected, presence.State);

        // The same again: nothing sent. Two changes within 15 s: only the latest, once the time is up.
        presence.Show(enabled: true, new DiscordActivity("In the menu", null, null));
        var next = discord.Next();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        presence.WaitingForClock += () => waiting.TrySetResult();
        presence.Show(enabled: true, new DiscordActivity("Ranked", "turn 1", null));
        presence.Show(enabled: true, new DiscordActivity("Ranked", "turn 2", null));
        await waiting.Task.WaitAsync(Wait);
        Assert.False(next.IsCompleted);
        clock.Advance(DiscordPresence.MinInterval);
        Assert.Equal("turn 2", (await next.WaitAsync(Wait))!["state"]!.GetValue<string>());
        await presence.Pending.WaitAsync(Wait);
        Assert.Equal(2, discord.Activities.Count);

        // Switched off: the status is cleared at once.
        var cleared = discord.Next();
        presence.Show(enabled: false, null);
        Assert.Null(await cleared.WaitAsync(Wait));
        await presence.Pending.WaitAsync(Wait);
        Assert.Equal(DiscordState.Off, presence.State);
    }

    [Fact]
    public async Task Without_an_app_id_nothing_connects()
    {
        var opened = 0;
        await using var presence = new DiscordPresence(new DiscordIpc((_, _) =>
        {
            Interlocked.Increment(ref opened);
            return Task.FromResult<Stream?>(null);
        }), applicationId: string.Empty);

        Assert.False(presence.Configured);
        presence.Show(enabled: true, new DiscordActivity("In the menu", null, null));
        await presence.Pending.WaitAsync(Wait);

        Assert.Equal(DiscordState.NotConfigured, presence.State);
        Assert.Equal(0, opened);
    }
}
