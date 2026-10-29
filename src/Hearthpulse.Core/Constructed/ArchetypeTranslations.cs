using System.Globalization;
using System.Text;
using System.Text.Json;
using Hearthpulse.Core.Localization;

namespace Hearthpulse.Core.Constructed;

/// <summary>
/// Russian archetype names from Hearthpulse's archetype dictionary <c>api.blizzcore.ru/archetypes</c> (our service, next
/// to the data API): <c>{count, items: [{id, name_en, name_ru, ...}]}</c>, matched to the API's archetype names by
/// <c>name_en</c> (case and apostrophe style ignored). Cached on disk for <see cref="MaxAge"/>; offline the last list is
/// used, and a failed fetch is not retried for a while (<see cref="RetryAfter"/>). The labels the site itself shows come
/// first; names neither has fall back to the built-in labels (<see cref="ArchetypeNames.RussianLabel"/>), which keep the
/// English name when they have none either.
/// </summary>
public sealed class ArchetypeTranslations
{
    /// <summary>
    /// The endpoint answers 200 items unless asked for more (<c>count</c> is the size of the answer, not the total, and
    /// <c>offset</c>/<c>page</c> are ignored), so one request asks for <see cref="Limit"/>.
    /// </summary>
    public const string Endpoint = "https://api.blizzcore.ru/archetypes?limit=1000";

    /// <summary>The <c>limit</c> of <see cref="Endpoint"/>: an answer this long may be cut, which the log notes.</summary>
    public const int Limit = 1000;

    /// <summary>Longest name shown; longer ones are cut with an ellipsis (the meta table and overlay rows are narrow).</summary>
    public const int MaxNameLength = 40;

    /// <summary>Names change a few times a patch: half a day keeps them fresh without asking on every start.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(12);

    /// <summary>
    /// The request's own time limit, well under the shared client's 40 s: the names only relabel a meta that is already
    /// on screen, so a host that drops packets must not keep anything waiting.
    /// </summary>
    public static TimeSpan FetchTimeout { get; internal set; } = TimeSpan.FromSeconds(5);

    /// <summary>A full answer is ~50 KB; anything past this is not the dictionary.</summary>
    private const int MaxBytes = 1024 * 1024;

    private const string CacheName = "blizzcore-archetypes.json";

    private const string FailureName = "blizzcore-archetypes.failed";

    private readonly Dictionary<string, string> _russian;

    private ArchetypeTranslations(Dictionary<string, string> russian) => _russian = russian;

    /// <summary>No names: every archetype keeps its built-in label.</summary>
    public static ArchetypeTranslations Empty { get; } = new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>The list the app loaded (<see cref="Empty"/> until then); set by the hosts.</summary>
    public static ArchetypeTranslations Current { get; set; } = Empty;

    /// <summary>How many names the list has.</summary>
    public int Count => _russian.Count;

    /// <summary>The list's Russian name for an English archetype name, or null when it has none.</summary>
    public string? Russian(string english) =>
        _russian.TryGetValue(Key(english), out var russian) ? russian : null;

    /// <summary>
    /// The Russian label: the one hearthpulse.net shows (<see cref="ArchetypeNames.SiteLabel"/>), else the list's name,
    /// else the built-in label, else the English name.
    /// </summary>
    public string RussianLabel(string english) => ArchetypeNames.SiteLabel(english) ?? Russian(english) ?? ArchetypeNames.RussianLabel(english);

    /// <summary>An archetype name in the interface language: <see cref="RussianLabel"/> in Russian, the English name otherwise.</summary>
    public string Display(string english) => Loc.IsRussian ? RussianLabel(english) : english;

    /// <summary>
    /// After the n-th failed fetch in a row, wait 1, 2, 4, then 6 hours before asking again, so a blocked host costs one
    /// request per few hours instead of one per start.
    /// </summary>
    public static TimeSpan RetryAfter(int failures) => TimeSpan.FromHours(Math.Min(6, Math.Pow(2, Math.Max(0, failures - 1))));

    /// <summary>
    /// Reads the cache while it is fresh and readable, else fetches the list. Never throws: a failed fetch falls back to
    /// the cached list, however old, and to <see cref="Empty"/> without one.
    /// </summary>
    /// <param name="cacheDirectory">Where the answer is cached; the app's API cache folder by default (tests pass a scratch one).</param>
    public static async Task<ArchetypeTranslations> LoadAsync(HttpClient http, string? cacheDirectory = null, CancellationToken cancellationToken = default)
    {
        var directory = cacheDirectory ?? Path.Combine(AppPaths.Cache, "api");
        var file = Path.Combine(directory, CacheName);
        var cached = await ReadCacheAsync(file, cancellationToken).ConfigureAwait(false);
        var fresh = File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) <= MaxAge;
        if ((cached.Count > 0 && fresh) || WaitingAfterFailure(directory))
        {
            return cached;
        }

        if (await FetchAsync(http, cancellationToken).ConfigureAwait(false) is { } text && Parse(text) is { Count: > 0 } fetched)
        {
            await WriteFileAsync(file, text, cancellationToken).ConfigureAwait(false);
            Delete(Path.Combine(directory, FailureName));
            return fetched;
        }

        await RecordFailureAsync(directory, cancellationToken).ConfigureAwait(false);
        return cached;
    }

    /// <summary>
    /// Reads <c>{"items": [{"name_en", "name_ru"}]}</c> (or a bare list of items). Items without both names, or with a
    /// Russian name that is not a plain name (markup, control characters, no letters), are skipped; long names are cut
    /// to <see cref="MaxNameLength"/>; the first item wins when two share an English name. Unreadable text gives
    /// <see cref="Empty"/>.
    /// </summary>
    internal static ArchetypeTranslations Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var items = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("items", out var list) ? list : doc.RootElement;
            if (items.ValueKind != JsonValueKind.Array)
            {
                return Empty;
            }

            var russian = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object
                    && Text(item, "name_en") is { Length: > 0 } english && Clean(Text(item, "name_ru")) is { } name)
                {
                    russian.TryAdd(Key(english), name);
                }
            }

            if (russian.Count >= Limit)
            {
                Diag.Info($"Archetype names: {russian.Count} names, the request limit; the list may be cut");
            }

            return new ArchetypeTranslations(russian);
        }
        catch (JsonException ex)
        {
            Diag.Error("Archetype names unreadable", ex);
            return Empty;
        }
    }

    /// <summary>A name fit to show: single-spaced, cut to <see cref="MaxNameLength"/>; null when it is empty or not a plain name.</summary>
    private static string? Clean(string? name)
    {
        if (name is null || name.Any(c => char.IsControl(c) || c is '<' or '>' || char.GetUnicodeCategory(c) == UnicodeCategory.PrivateUse)
            || !name.Any(char.IsLetter))
        {
            return null;
        }

        var spaced = string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return spaced.Length <= MaxNameLength ? spaced : spaced[..(MaxNameLength - 1)].TrimEnd() + "…";
    }

    /// <summary>Trimmed, single-spaced, typographic apostrophes as plain ones ("Mecha’thun Druid" = "Mecha'thun druid").</summary>
    private static string Key(string name) =>
        string.Join(' ', name.Replace('’', '\'').Replace('‘', '\'').Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;

    private static async Task<ArchetypeTranslations> ReadCacheAsync(string file, CancellationToken cancellationToken)
    {
        try
        {
            // A cache that does not parse counts as missing, so it is fetched again rather than kept for MaxAge.
            return File.Exists(file) ? Parse(await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false)) : Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diag.Error("Archetype names cache unreadable", ex);
            return Empty;
        }
    }

    private static async Task<string?> FetchAsync(HttpClient http, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(FetchTimeout);
        try
        {
            using var response = await http.GetAsync(Endpoint, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Diag.Info($"Archetype names: HTTP {(int)response.StatusCode}");
                return null;
            }

            if (response.Content.Headers.ContentLength > MaxBytes)
            {
                Diag.Info($"Archetype names: answer of {response.Content.Headers.ContentLength} bytes ignored");
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxBytes)
                {
                    Diag.Info("Archetype names: answer over 1 MB ignored");
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }

            return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // TaskCanceledException is our FetchTimeout or the client's own timeout here, not the caller: offline, use the cache.
            Diag.Info($"Archetype names: {ex.GetType().Name} {ex.Message}");
            return null;
        }
    }

    /// <summary>Whether an earlier fetch failed so recently that this start should not ask again (<see cref="RetryAfter"/>).</summary>
    private static bool WaitingAfterFailure(string directory)
    {
        var marker = Path.Combine(directory, FailureName);
        try
        {
            return File.Exists(marker)
                && int.TryParse(File.ReadAllText(marker).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var failures)
                && DateTime.UtcNow - File.GetLastWriteTimeUtc(marker) < RetryAfter(failures);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Counts the failures in a row in a small marker file next to the cache; its time is the last failure.</summary>
    private static async Task RecordFailureAsync(string directory, CancellationToken cancellationToken)
    {
        var marker = Path.Combine(directory, FailureName);
        var failures = 0;
        try
        {
            if (File.Exists(marker))
            {
                _ = int.TryParse(File.ReadAllText(marker).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out failures);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        await WriteFileAsync(marker, (failures + 1).ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Through a temporary file, so a crash mid-write leaves the old content.</summary>
    private static async Task WriteFileAsync(string file, string text, CancellationToken cancellationToken)
    {
        var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(temp, text, cancellationToken).ConfigureAwait(false);
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            Diag.Error($"Archetype names file not written ({Path.GetFileName(file)})", ex);
            Delete(temp);
        }
    }

    private static void Delete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
