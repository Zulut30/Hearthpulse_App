using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Hearthpulse.Core.SourceCapture.Processing;

/// <summary>Decodes the typed visible projection, never raw logs or post-game replay reveals.</summary>
public sealed class SourceCaptureParser
{
    /// <summary>Changing derived semantics requires a new version; source bytes remain immutable.</summary>
    public const string ParserVersion = "source-v2.0";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly SourceCaptureLimits _limits;

    /// <summary>Creates a bounded parser. Operators may lower budgets for smaller isolated jobs.</summary>
    public SourceCaptureParser(SourceCaptureLimits? limits = null)
    {
        _limits = limits ?? new();
        if (_limits.MaxSourceBytes is < 1 or > 256L * 1024 * 1024 ||
            _limits.MaxLineBytes is < 1 or > 64 * 1024 || _limits.MaxRecords is < 1 or > 100_000 ||
            _limits.MaxDerivedBytes is < 1 or > 64L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(limits));
    }

    /// <summary>Reads at most the configured budgets, leaves the input open, and returns no partial result on invalid input.</summary>
    public async Task<SourceCaptureResult> ParseAsync(Stream input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var state = new SourceCaptureState(_limits);
        byte[] buffer = new byte[8192], line = new byte[_limits.MaxLineBytes];
        int length = 0;
        long bytes = 0;
        while (true)
        {
            int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            bytes += read;
            if (bytes > _limits.MaxSourceBytes) throw new SourceCaptureException("limit_exceeded");
            hash.AppendData(buffer.AsSpan(0, read));
            for (int i = 0; i < read; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (buffer[i] == (byte)'\n')
                {
                    var (record, version) = ParseLine(line.AsMemory(0, length));
                    state.Add(record, version);
                    length = 0;
                }
                else
                {
                    if (length == line.Length) throw new SourceCaptureException("limit_exceeded");
                    line[length++] = buffer[i];
                }
            }
        }
        SourceCaptureSchema.Require(length == 0); // A torn final record is not a successful empty match.
        return state.Finish(Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private static (SourceCaptureEvent Record, int Version) ParseLine(ReadOnlyMemory<byte> line)
    {
        // Every v1/v2 string is an enum, a field name, or an ASCII card/token ID. None contains whitespace.
        SourceCaptureSchema.Require(!line.IsEmpty && !line.Span.Contains((byte)'\r') &&
            !line.Span.Contains((byte)' ') && !line.Span.Contains((byte)'\t'));
        try
        {
            _ = StrictUtf8.GetCharCount(line.Span);
            using var doc = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 16 });
            var r = doc.RootElement;
            SourceCaptureSchema.Fields(r, "v", "seq", "kind", "turn", "side", "entity", "card", "target", "data");
            int version = SourceCaptureSchema.Number(r.GetProperty("v"), 1, int.MaxValue);
            if (version is not (1 or 2)) throw new SourceCaptureException("unsupported_version");
            var e = new SourceCaptureEvent(SourceCaptureSchema.Number(r.GetProperty("seq"), 0, int.MaxValue),
                SourceCaptureSchema.Text(r.GetProperty("kind")), SourceCaptureSchema.Number(r.GetProperty("turn"), 0, 1_000),
                SourceCaptureSchema.Side(r.GetProperty("side")), SourceCaptureSchema.Number(r.GetProperty("entity")),
                SourceCaptureSchema.Card(r.GetProperty("card")), SourceCaptureSchema.Number(r.GetProperty("target")), r.GetProperty("data"));
            SourceCaptureSchema.Validate(e, version);
            // Stable data key order even when the source producer writes equivalent JSON differently.
            using var canonical = new MemoryStream();
            using (var writer = new Utf8JsonWriter(canonical)) WriteCanonical(writer, e.Data);
            using var data = JsonDocument.Parse(canonical.GetBuffer().AsMemory(0, (int)canonical.Length));
            return (e with { Data = data.RootElement.Clone() }, version);
        }
        catch (JsonException) { throw new SourceCaptureException("invalid_source"); }
        catch (DecoderFallbackException) { throw new SourceCaptureException("invalid_source"); }
        catch (InvalidOperationException) { throw new SourceCaptureException("invalid_source"); }
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var p in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(p.Name);
                    WriteCanonical(writer, p.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            default: value.WriteTo(writer); break;
        }
    }
}
