using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Hearthpulse.Core.Accounts;

/// <summary>
/// Receives the browser's redirect at <c>http://127.0.0.1:{port}/callback</c> (RFC 8252 7.3 loopback redirect).
/// A <see cref="TcpListener"/> rather than <c>HttpListener</c>: it binds 127.0.0.1 only, needs no URL ACL on Windows
/// (http.sys) and behaves the same on macOS and Linux. Port 0 lets the OS pick a free port atomically.
/// </summary>
internal sealed class LoopbackListener : IDisposable
{
    private const int MaxRequestBytes = 16 * 1024;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

    public LoopbackListener() => _listener.Start();

    public string RedirectUri => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/callback";

    /// <summary>
    /// Waits for <c>GET /callback</c>, answers it with the page <paramref name="answer"/> builds from the query, and
    /// returns. Other requests (favicon, probes) get 404 and the wait goes on.
    /// </summary>
    public async Task WaitAsync(Func<string, CancellationToken, Task<string>> answer, CancellationToken cancellationToken)
    {
        while (true)
        {
            using var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            await using var stream = client.GetStream();
            string? target;
            using (var read = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                read.CancelAfter(ReadTimeout);
                try
                {
                    target = await ReadTargetAsync(stream, read.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
                {
                    continue;
                }
            }

            var path = target?.Split('?', 2)[0];
            if (path != "/callback")
            {
                await TryWriteAsync(stream, target is null ? "400 Bad Request" : "404 Not Found", string.Empty, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var query = target!.Contains('?', StringComparison.Ordinal) ? target.Split('?', 2)[1] : string.Empty;
            var page = await answer(query, cancellationToken).ConfigureAwait(false);
            await TryWriteAsync(stream, "200 OK", page, cancellationToken).ConfigureAwait(false);
            return;
        }
    }

    public void Dispose() => _listener.Stop();

    /// <summary>The target of a <c>GET</c> request line, or null for anything else.</summary>
    private static async Task<string?> ReadTargetAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxRequestBytes];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            length += read;
            if (buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8) >= 0)
            {
                break;
            }
        }

        var head = Encoding.ASCII.GetString(buffer, 0, length);
        var line = head.Split("\r\n", 2)[0].Split(' ');
        return line is ["GET", var target, var version] && version.StartsWith("HTTP/1.", StringComparison.Ordinal) && target.StartsWith('/')
            ? target
            : null;
    }

    private static async Task TryWriteAsync(NetworkStream stream, string status, string html, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(html);
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Cache-Control: no-store\r\n" +
            "Referrer-Policy: no-referrer\r\n" +
            "Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'\r\n" +
            "Connection: close\r\n\r\n");
        try
        {
            await stream.WriteAsync(head, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The browser tab was closed; the sign-in result does not depend on it.
        }
    }
}
