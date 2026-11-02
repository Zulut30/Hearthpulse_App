using System.Buffers.Binary;
using System.Globalization;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hearthpulse.Core.Discord;

/// <summary>
/// What Discord shows under the player's name (<c>docs/discord.md</c>): two lines, the time since the game started and
/// the app's picture. Discord takes 2–128 characters a line; longer text is cut.
/// </summary>
public sealed record DiscordActivity(string Details, string? State, DateTimeOffset? Started)
{
    /// <summary>The picture's key in the registered app's art assets, and its hover text.</summary>
    public string? LargeImage { get; init; }

    public string? LargeText { get; init; }
}

/// <summary>
/// The local connection to the Discord app (its RPC over IPC: on Windows the named pipe <c>discord-ipc-0…9</c>, elsewhere
/// the unix socket of that name in the runtime folder or Discord's snap / flatpak folder in it). A frame is a
/// little-endian opcode, a length and UTF-8 JSON; the app says hello (handshake), then sets the activity. Nothing leaves
/// the PC but through the Discord app.
/// </summary>
public sealed class DiscordIpc : IAsyncDisposable
{
    internal const int OpHandshake = 0;
    internal const int OpFrame = 1;
    internal const int OpClose = 2;
    internal const int OpPing = 3;
    internal const int OpPong = 4;

    /// <summary>The longest text a line takes.</summary>
    internal const int MaxText = 128;

    /// <summary>Where Discord's socket may be inside the runtime folder: its own, flatpak's and snap's.</summary>
    private static readonly string[] UnixFolders =
        ["", "app/com.discordapp.Discord", "app/com.discordapp.DiscordCanary", "snap.discord", "snap.discord-canary"];

    private readonly Func<int, CancellationToken, Task<Stream?>> _open;
    private readonly TimeSpan _answerTimeout;
    private Stream? _stream;

    /// <param name="open">Opens pipe <c>n</c> (0–9), or null when it is not there; tests pass their own.</param>
    /// <param name="answerTimeout">How long Discord may take to answer a frame (5 s).</param>
    public DiscordIpc(Func<int, CancellationToken, Task<Stream?>>? open = null, TimeSpan? answerTimeout = null)
    {
        _open = open ?? OpenAsync;
        _answerTimeout = answerTimeout ?? TimeSpan.FromSeconds(5);
    }

    public bool Connected => _stream is not null;

    /// <summary>Finds the running Discord app and says hello as <paramref name="clientId"/>; false when there is none.</summary>
    public async Task<bool> ConnectAsync(string clientId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(clientId);
        await CloseAsync().ConfigureAwait(false);
        for (var pipe = 0; pipe < 10; pipe++)
        {
            if (await _open(pipe, cancellationToken).ConfigureAwait(false) is not { } stream)
            {
                continue;
            }

            try
            {
                await WriteAsync(stream, OpHandshake, new JsonObject { ["v"] = 1, ["client_id"] = clientId }, cancellationToken).ConfigureAwait(false);
                // The first dispatch is READY, or ERROR for a bad id.
                var answer = await AwaitAsync(stream, body => body?["evt"] is not null, cancellationToken).ConfigureAwait(false);
                if (answer?["evt"]?.GetValue<string>() == "READY")
                {
                    _stream = stream;
                    return true;
                }

                Diag.Info("Discord: pipe " + pipe.ToString(CultureInfo.InvariantCulture) + " refused the app: " + answer?["data"]?["message"]);
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or ObjectDisposedException)
            {
                Diag.Info(string.Create(CultureInfo.InvariantCulture, $"Discord: pipe {pipe} did not answer ({ex.Message})"));
            }

            await stream.DisposeAsync().ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>Shows <paramref name="activity"/>, or clears it with null. Throws <see cref="IOException"/> when the app went away.</summary>
    public async Task SetActivityAsync(DiscordActivity? activity, int processId, CancellationToken cancellationToken)
    {
        var stream = _stream ?? throw new IOException("Not connected to Discord.");
        var nonce = Guid.NewGuid().ToString("N");
        var args = new JsonObject { ["pid"] = processId, ["activity"] = activity is null ? null : Json(activity) };
        var frame = new JsonObject { ["cmd"] = "SET_ACTIVITY", ["args"] = args, ["nonce"] = nonce };
        try
        {
            await WriteAsync(stream, OpFrame, frame, cancellationToken).ConfigureAwait(false);
            // Our answer (or an error) comes back at once; reading it keeps the pipe from filling up.
            var answer = await AwaitAsync(stream, body => body?["nonce"]?.GetValue<string>() == nonce, cancellationToken).ConfigureAwait(false);
            if (answer?["evt"]?.GetValue<string>() == "ERROR")
            {
                Diag.Info("Discord: activity refused: " + answer["data"]?["message"]);
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ObjectDisposedException)
        {
            await CloseAsync().ConfigureAwait(false);
            throw new IOException("Discord closed the connection.", ex);
        }
        catch (IOException)
        {
            await CloseAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task CloseAsync()
    {
        if (_stream is { } stream)
        {
            _stream = null;
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync() => new(CloseAsync());

    /// <summary>The activity as Discord's RPC takes it; lines cut to <see cref="MaxText"/>, empty ones left out.</summary>
    internal static JsonObject Json(DiscordActivity activity)
    {
        var json = new JsonObject { ["details"] = Cut(activity.Details), ["instance"] = false };
        if (Cut(activity.State) is { } state)
        {
            json["state"] = state;
        }

        if (activity.Started is { } started)
        {
            json["timestamps"] = new JsonObject { ["start"] = started.ToUnixTimeSeconds() };
        }

        if (!string.IsNullOrEmpty(activity.LargeImage))
        {
            json["assets"] = new JsonObject { ["large_image"] = activity.LargeImage, ["large_text"] = Cut(activity.LargeText) };
        }

        return json;
    }

    /// <summary>Discord needs 2–128 characters: shorter is padded, longer cut with an ellipsis; nothing stays nothing.</summary>
    internal static string? Cut(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        return trimmed.Length > MaxText ? string.Concat(trimmed.AsSpan(0, MaxText - 1), "…") : trimmed.Length < 2 ? trimmed + " " : trimmed;
    }

    /// <summary>
    /// Reads frames until one <paramref name="wanted"/> takes, answering Discord's pings on the way. Throws
    /// <see cref="IOException"/> when Discord closes the pipe or does not answer within the timeout.
    /// </summary>
    private async Task<JsonNode?> AwaitAsync(Stream stream, Func<JsonNode?, bool> wanted, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_answerTimeout);
        try
        {
            while (true)
            {
                var (op, body) = await ReadAsync(stream, timeout.Token).ConfigureAwait(false);
                switch (op)
                {
                    case OpPing:
                        await WriteAsync(stream, OpPong, body ?? new JsonObject(), timeout.Token).ConfigureAwait(false);
                        break;
                    case OpClose:
                        throw new IOException("Discord closed the connection: " + body?["message"]);
                    case OpFrame when wanted(body):
                        return body;
                }
            }
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("Discord did not answer in time.", ex);
        }
    }

    internal static async Task WriteAsync(Stream stream, int op, JsonNode payload, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(payload.ToJsonString());
        var frame = new byte[8 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, op);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), body.Length);
        body.CopyTo(frame, 8);
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<(int Op, JsonNode? Body)> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[8];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var op = BinaryPrimitives.ReadInt32LittleEndian(header);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        if (length is < 0 or > 64 * 1024)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"A Discord frame of {length} bytes."));
        }

        var body = new byte[length];
        await stream.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
        return (op, length == 0 ? null : JsonNode.Parse(body));
    }

    /// <summary>The Discord app's pipe <paramref name="pipe"/>, or null when it is not there or does not answer.</summary>
    private static async Task<Stream?> OpenAsync(int pipe, CancellationToken cancellationToken)
    {
        var name = "discord-ipc-" + pipe.ToString(CultureInfo.InvariantCulture);
        if (OperatingSystem.IsWindows())
        {
            // Only a pipe that exists: connecting to a missing one would wait out the timeout ten times a round.
            if (!PipeExists(name))
            {
                return null;
            }

            var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await client.ConnectAsync(200, cancellationToken).ConfigureAwait(false);
                return client;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
            {
                await client.DisposeAsync().ConfigureAwait(false);
                return null;
            }
        }

        var root = new[] { "XDG_RUNTIME_DIR", "TMPDIR", "TMP", "TEMP" }.Select(Environment.GetEnvironmentVariable).FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "/tmp";
        if (UnixFolders.Select(folder => Path.Combine(root, folder, name)).FirstOrDefault(File.Exists) is not { } path)
        {
            return null;
        }

        Socket? socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), cancellationToken).ConfigureAwait(false);
            var stream = new NetworkStream(socket, ownsSocket: true);
            socket = null;
            return stream;
        }
        catch (Exception ex) when (ex is SocketException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            socket?.Dispose();
        }
    }

    /// <summary>Whether Windows lists the pipe (listing does not open it); when the list cannot be read, try anyway.</summary>
    private static bool PipeExists(string name)
    {
        try
        {
            return Directory.GetFiles(@"\\.\pipe\", name).Length > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}
