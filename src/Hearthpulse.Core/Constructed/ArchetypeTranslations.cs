using System.Text.Json;
using Hearthpulse.Core.Localization;

namespace Hearthpulse.Core.Constructed;

/// <summary>
/// Russian archetype names from <c>api.blizzcore.ru/archetypes</c>: <c>{count, items: [{id, name_en, name_ru, ...}]}</c>,
/// matched to the API's archetype names by <c>name_en</c> (case and apostrophe style ignored). Cached on disk for
/// <see cref="MaxAge"/>; offline the last list is used. Names it lacks fall back to the built-in labels
/// (<see cref="ArchetypeNames.RussianLabel"/>), which keep the English name when they have none either.
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

    /// <summary>Names change a few times a patch: half a day keeps them fresh without asking on every start.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(12);

    private const string CacheName = "blizzcore-archetypes.json";

    private readonly Dictionary<string, string> _russian;

    private ArchetypeTranslations(Dictionary<string, string> russian) => _russian = russian;

    /// <summary>No names: every archetype keeps its built-in label.</summary>
    public static ArchetypeTranslations Empty { get; } = new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>The list the app loaded at start (<see cref="Empty"/> until then); set by the hosts.</summary>
    public static ArchetypeTranslations Current { get; set; } = Empty;

    /// <summary>How many names the list has.</summary>
    public int Count => _russian.Count;

    /// <summary>The list's Russian name for an English archetype name, or null when it has none.</summary>
    public string? Russian(string english) =>
        _russian.TryGetValue(Key(english), out var russian) ? russian : null;

    /// <summary>The Russian label: the list's name, else the built-in label, else the English name.</summary>
    public string RussianLabel(string english) => Russian(english) ?? ArchetypeNames.RussianLabel(english);

    /// <summary>An archetype name in the interface language: <see cref="RussianLabel"/> in Russian, the English name otherwise.</summary>
    public string Display(string english) => Loc.IsRussian ? RussianLabel(english) : english;

    /// <summary>
    /// Fetches the list (or reads the cache while it is fresh) and parses it. Never throws: a failed fetch falls back to
    /// the cached list, however old, and to <see cref="Empty"/> without one.
    /// </summary>
    /// <param name="cacheDirectory">Where the answer is cached; the app's API cache folder by default (tests pass a scratch one).</param>
    public static async Task<ArchetypeTranslations> LoadAsync(HttpClient http, string? cacheDirectory = null, CancellationToken cancellationToken = default)
    {
        var file = Path.Combine(cacheDirectory ?? Path.Combine(AppPaths.Cache, "api"), CacheName);
        var info = new FileInfo(file);
        if (!info.Exists || DateTime.UtcNow - info.LastWriteTimeUtc > MaxAge)
        {
            if (await FetchAsync(http, cancellationToken).ConfigureAwait(false) is { } text && Parse(text) is { Count: > 0 } fresh)
            {
                await WriteCacheAsync(file, text, cancellationToken).ConfigureAwait(false);
                return fresh;
            }
        }

        try
        {
            return File.Exists(file) ? Parse(await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false)) : Empty;
        }
        catch (IOException ex)
        {
            Diag.Error("Archetype names cache unreadable", ex);
            return Empty;
        }
    }

    /// <summary>
    /// Reads <c>{"items": [{"name_en", "name_ru"}]}</c> (or a bare list of items). Items without both names are skipped;
    /// the first item wins when two share an English name. Unreadable text gives <see cref="Empty"/>.
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
                    && Text(item, "name_en") is { Length: > 0 } english && Text(item, "name_ru") is { Length: > 0 } name)
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

    /// <summary>Trimmed, single-spaced, typographic apostrophes as plain ones ("Mecha’thun Druid" = "Mecha'thun druid").</summary>
    private static string Key(string name) =>
        string.Join(' ', name.Replace('’', '\'').Replace('‘', '\'').Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;

    private static async Task<string?> FetchAsync(HttpClient http, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync(Endpoint, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Diag.Info($"Archetype names: HTTP {(int)response.StatusCode}");
                return null;
            }

            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // TaskCanceledException is the HttpClient timeout here, not ours: offline, use the cache.
            Diag.Info($"Archetype names: {ex.GetType().Name} {ex.Message}");
            return null;
        }
    }

    /// <summary>Through a temporary file, so a crash mid-write leaves the old list.</summary>
    private static async Task WriteCacheAsync(string file, string text, CancellationToken cancellationToken)
    {
        var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(temp, text, cancellationToken).ConfigureAwait(false);
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diag.Error("Archetype names cache not written", ex);
            try
            {
                File.Delete(temp);
            }
            catch (IOException)
            {
            }
        }
    }
}
