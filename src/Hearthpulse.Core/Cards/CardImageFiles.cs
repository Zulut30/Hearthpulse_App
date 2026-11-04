using System.Collections.Concurrent;
using System.Net;

namespace Hearthpulse.Core.Cards;

/// <summary>The kinds of card image the UIs show, each with its own HearthstoneJSON address and cache folder.</summary>
public enum ArtKind
{
    /// <summary>Deck-list strip (256x59).</summary>
    Tile,

    /// <summary>Square card art crop, used for hero portraits.</summary>
    Portrait,

    /// <summary>Full Battlegrounds card render.</summary>
    BattlegroundsCard,

    /// <summary>Full-size card art (512x512), used as a faded page banner (WPF only).</summary>
    Splash,

    /// <summary>Full Constructed card render (256 px wide) in the card-data locale, shown by the hover preview.</summary>
    Card,
}

/// <summary>
/// The files behind card images, for both UIs (<c>CardArt</c>, <c>CardRender</c>): where an image comes from on
/// HearthstoneJSON and where it is kept on disk, the download that never lets a web page into the cache, and the
/// clean-up of files that cannot be used. Decoding and drawing stay in each UI. One instance per kind of loader: it holds
/// that loader's download limit and what it learned this session (addresses without an image, folders trimmed).
/// </summary>
public sealed class CardImageFiles
{
    /// <summary>HearthstoneJSON's image server.</summary>
    public const string Server = "https://art.hearthstonejson.com/v1/";

    private readonly SemaphoreSlim _downloads;

    /// <summary>Addresses that answered 404 this session (tokens without art...): not asked again on every hover.</summary>
    private readonly ConcurrentDictionary<string, bool> _missing = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, bool> _trimmed = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="downloads">How many downloads may run at the same time.</param>
    public CardImageFiles(int downloads) => _downloads = new SemaphoreSlim(downloads);

    /// <summary>The memory cache key of an image.</summary>
    public static string Key(ArtKind kind, string cardId) => $"{kind}:{cardId}";

    /// <summary>
    /// Where art of <paramref name="kind"/> comes from and where it is kept under <paramref name="imagesFolder"/>.
    /// Renders carry the card text, so each locale keeps its own copies.
    /// </summary>
    public static (string Url, string File) Art(ArtKind kind, string cardId, string locale, string imagesFolder)
    {
        var (url, ext) = kind switch
        {
            ArtKind.Tile => ($"{Server}tiles/{cardId}.png", "png"),
            ArtKind.Portrait => ($"{Server}256x/{cardId}.jpg", "jpg"),
            ArtKind.Splash => ($"{Server}512x/{cardId}.jpg", "jpg"),
            ArtKind.Card => ($"{Server}render/latest/{locale}/256x/{cardId}.png", "png"),
            _ => ($"{Server}bgs/latest/{locale}/256x/{cardId}.png", "png"),
        };
        var dir = kind == ArtKind.Card
            ? Path.Combine(imagesFolder, "card", locale)
            : Path.Combine(imagesFolder, kind.ToString().ToLowerInvariant());
        return (url, Path.Combine(dir, $"{cardId}.{ext}"));
    }

    /// <summary>Where the full Constructed render the card pool browser shows comes from, and its own cache folder.</summary>
    public static (string Url, string File) Render(string cardId, string locale, string imagesFolder) =>
        ($"{Server}render/latest/{locale}/256x/{cardId}.png", Path.Combine(imagesFolder, "render-" + locale, cardId + ".png"));

    /// <summary>
    /// PNG, JPEG or WebP signature. A proxy or a captive portal can answer 200 with a web page, which must never land
    /// in the image cache: it would fail to decode for that card on every later start.
    /// </summary>
    public static bool LooksLikeImage(ReadOnlySpan<byte> bytes) =>
        bytes.Length > 12
        && (LooksLikePng(bytes)
            || (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            || (bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
                && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P'));

    /// <summary>PNG signature (its first four bytes).</summary>
    public static bool LooksLikePng(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47;

    /// <summary>
    /// Card art: unless <paramref name="file"/> is already on disk, downloads it from <paramref name="url"/>, and when that
    /// has nothing, from the address <paramref name="fallbackUrl"/> gives (another source for art HearthstoneJSON lacks; never for renders,
    /// which carry the card text). Network failures are logged and leave no file.
    /// </summary>
    public async Task EnsureArtAsync(HttpClient http, string url, string file, Func<string?>? fallbackUrl)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (File.Exists(file))
        {
            return;
        }

        await _downloads.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!await TryDownloadArtAsync(http, url, file).ConfigureAwait(false) && fallbackUrl?.Invoke() is { Length: > 0 } fallback)
            {
                await TryDownloadArtAsync(http, fallback, file).ConfigureAwait(false);
            }
        }
        finally
        {
            _downloads.Release();
        }
    }

    private async Task<bool> TryDownloadArtAsync(HttpClient http, string url, string file)
    {
        if (_missing.ContainsKey(url))
        {
            return false;
        }

        try
        {
            using var response = await http.GetAsync(url).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    _missing[url] = true;
                }

                return false;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            if (!LooksLikeImage(bytes))
            {
                Diag.Info($"Art download was not an image ({url})");
                return false;
            }

            await SaveAsync(file, bytes).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            Diag.Error($"Art download failed ({url})", ex);
            return false;
        }
    }

    /// <summary>
    /// A card render: downloads <paramref name="url"/> into <paramref name="file"/>. Network failures are the caller's.
    /// </summary>
    /// <returns>False when the server has no PNG for the card.</returns>
    public async Task<bool> DownloadRenderAsync(HttpClient http, string url, string file)
    {
        ArgumentNullException.ThrowIfNull(http);
        await _downloads.WaitAsync().ConfigureAwait(false);
        try
        {
            using var response = await http.GetAsync(url).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            // PNG only: a captive portal's web page must never land in the cache.
            if (!LooksLikePng(bytes))
            {
                Diag.Info($"Card render download was not a PNG ({url})");
                return false;
            }

            await SaveAsync(file, bytes).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _downloads.Release();
        }
    }

    /// <summary>Writes beside the file and moves it in, so a half-written image never sits under the card's name.</summary>
    private static async Task SaveAsync(string file, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllBytesAsync(file + ".tmp", bytes).ConfigureAwait(false);
        File.Move(file + ".tmp", file, overwrite: true);
    }

    /// <summary>
    /// Once per session and folder: keeps the newest <paramref name="maxFiles"/> PNGs in <paramref name="dir"/> (a Wild
    /// pool alone can be thousands of renders, ~100 KB each).
    /// </summary>
    public void TrimOnce(string dir, int maxFiles)
    {
        if (!_trimmed.TryAdd(dir, true) || !Directory.Exists(dir))
        {
            return;
        }

        try
        {
            var files = new DirectoryInfo(dir).GetFiles("*.png");
            foreach (var old in files.OrderByDescending(f => f.LastWriteTimeUtc).Skip(maxFiles))
            {
                old.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diag.Error("Card render cache not trimmed", ex);
        }
    }

    /// <summary>Deletes a cached image that cannot be decoded, so a later session downloads it again; a locked file stays.</summary>
    public static void TryDelete(string file)
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
