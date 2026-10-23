using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Hearthlens.Core;

namespace Hearthlens.App.Infrastructure;

public enum ArtKind
{
    /// <summary>Deck-list strip (256x59).</summary>
    Tile,

    /// <summary>Square card art crop, used for hero portraits.</summary>
    Portrait,

    /// <summary>Full Battlegrounds card render.</summary>
    BattlegroundsCard,

    /// <summary>Full-size card art (512x512), used as a faded page banner.</summary>
    Splash,
}

/// <summary>
/// Card images from HearthstoneJSON, cached on disk and in memory as frozen bitmaps so list
/// refreshes never flicker. Images set through the attached properties load asynchronously.
/// </summary>
public static class CardArt
{
    /// <summary>
    /// How many card images stay in memory. A 256 px bitmap is about a quarter of a megabyte, so an unbounded
    /// cache grows with every card the game shows and pushes a small PC into swapping — the app then appears
    /// frozen. The oldest images are dropped first.
    /// </summary>
    private const int MaxCachedImages = 160;

    /// <summary>How many finished load tasks are remembered; they only keep the image reachable.</summary>
    private const int MaxPendingTasks = 2000;

    private static readonly ConcurrentDictionary<string, ImageSource> Memory = new();
    private static readonly ConcurrentDictionary<string, Task<ImageSource?>> Pending = new();
    private static readonly Queue<string> MemoryOrder = new();
    private static readonly ConcurrentDictionary<string, bool> Unreadable = new();
    private static readonly SemaphoreSlim Downloads = new(6);

    /// <summary>
    /// How many images are decoded at the same time. Decoding is the expensive part, and a burst (the comp panel
    /// asks for dozens of cards at once) must not eat the whole machine.
    /// </summary>
    private static readonly SemaphoreSlim Decodes = new(4);

    public static HttpClient? Http { get; set; }

    public static string Locale { get; set; } = "enUS";

    /// <summary>
    /// Optional fallback URL provider for art HearthstoneJSON does not have (e.g. hsbg.cards), given the card id.
    /// Wired by the host after card data loads; null keeps HearthstoneJSON as the only source.
    /// </summary>
    public static Func<string, string?>? FallbackUrl { get; set; }

    public static readonly DependencyProperty TileProperty = Register("Tile", ArtKind.Tile);
    public static readonly DependencyProperty PortraitProperty = Register("Portrait", ArtKind.Portrait);
    public static readonly DependencyProperty BattlegroundsCardProperty = Register("BattlegroundsCard", ArtKind.BattlegroundsCard);
    public static readonly DependencyProperty SplashProperty = Register("Splash", ArtKind.Splash);

    public static string? GetTile(DependencyObject d) => (string?)d.GetValue(TileProperty);
    public static void SetTile(DependencyObject d, string? value) => d.SetValue(TileProperty, value);
    public static string? GetPortrait(DependencyObject d) => (string?)d.GetValue(PortraitProperty);
    public static void SetPortrait(DependencyObject d, string? value) => d.SetValue(PortraitProperty, value);
    public static string? GetBattlegroundsCard(DependencyObject d) => (string?)d.GetValue(BattlegroundsCardProperty);
    public static void SetBattlegroundsCard(DependencyObject d, string? value) => d.SetValue(BattlegroundsCardProperty, value);
    public static string? GetSplash(DependencyObject d) => (string?)d.GetValue(SplashProperty);
    public static void SetSplash(DependencyObject d, string? value) => d.SetValue(SplashProperty, value);

    private static DependencyProperty Register(string name, ArtKind kind) =>
        DependencyProperty.RegisterAttached(name, typeof(string), typeof(CardArt),
            new PropertyMetadata(null, (d, e) => Apply(d, kind, e.NewValue as string)));

    private static async void Apply(DependencyObject target, ArtKind kind, string? cardId)
    {
        if (string.IsNullOrEmpty(cardId))
        {
            SetSource(target, null);
            return;
        }

        var key = Key(kind, cardId);
        if (Memory.TryGetValue(key, out var cached))
        {
            SetSource(target, cached);
            return;
        }

        SetSource(target, null);
        var image = await GetAsync(kind, cardId);
        // The element may have been recycled for another card while we were downloading.
        if (Equals(target.GetValue(PropertyFor(kind)), cardId))
        {
            SetSource(target, image);
        }
    }

    private static DependencyProperty PropertyFor(ArtKind kind) => kind switch
    {
        ArtKind.Tile => TileProperty,
        ArtKind.Portrait => PortraitProperty,
        ArtKind.Splash => SplashProperty,
        _ => BattlegroundsCardProperty,
    };

    private static void SetSource(DependencyObject target, ImageSource? source)
    {
        switch (target)
        {
            case Image image:
                image.Source = source;
                break;
            case ImageBrush brush:
                brush.ImageSource = source;
                break;
            case Border border:
                border.Background = source is null ? null : new ImageBrush(source) { Stretch = Stretch.UniformToFill, AlignmentX = AlignmentX.Right };
                break;
        }
    }

    private static string Key(ArtKind kind, string cardId) => $"{kind}:{cardId}";

    public static Task<ImageSource?> GetAsync(ArtKind kind, string cardId)
    {
        var key = Key(kind, cardId);
        TrimPending();
        return Pending.GetOrAdd(key, _ => LoadAsync(kind, cardId));
    }

    /// <summary>Keeps the task dictionary from growing for every card the session ever shows.</summary>
    private static void TrimPending()
    {
        if (Pending.Count <= MaxPendingTasks)
        {
            return;
        }

        foreach (var entry in Pending)
        {
            if (entry.Value.IsCompletedSuccessfully)
            {
                Pending.TryRemove(entry.Key, out _);
            }

            if (Pending.Count <= MaxPendingTasks)
            {
                return;
            }
        }
    }

    /// <summary>Caches the image and drops the oldest ones when the cache is full.</summary>
    private static void Remember(string key, ImageSource image)
    {
        Memory[key] = image;
        lock (MemoryOrder)
        {
            MemoryOrder.Enqueue(key);
            while (MemoryOrder.Count > MaxCachedImages)
            {
                Memory.TryRemove(MemoryOrder.Dequeue(), out _);
            }
        }
    }

    private static async Task<ImageSource?> LoadAsync(ArtKind kind, string cardId)
    {
        var (url, ext) = kind switch
        {
            ArtKind.Tile => ($"https://art.hearthstonejson.com/v1/tiles/{cardId}.png", "png"),
            ArtKind.Portrait => ($"https://art.hearthstonejson.com/v1/256x/{cardId}.jpg", "jpg"),
            ArtKind.Splash => ($"https://art.hearthstonejson.com/v1/512x/{cardId}.jpg", "jpg"),
            _ => ($"https://art.hearthstonejson.com/v1/bgs/latest/{Locale}/256x/{cardId}.png", "png"),
        };
        var dir = Path.Combine(AppPaths.Images, kind.ToString().ToLowerInvariant());
        var file = Path.Combine(dir, $"{cardId}.{ext}");
        ImageSource? image = null;
        if (Unreadable.ContainsKey(Key(kind, cardId)))
        {
            return null;
        }

        try
        {
            if (!File.Exists(file) && Http is { } http)
            {
                await Downloads.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (!await TryDownloadAsync(http, url, file, dir).ConfigureAwait(false) && FallbackUrl?.Invoke(cardId) is { Length: > 0 } fallback)
                    {
                        await TryDownloadAsync(http, fallback, file, dir).ConfigureAwait(false);
                    }
                }
                finally
                {
                    Downloads.Release();
                }
            }

            if (File.Exists(file))
            {
                // Decoding an image costs tens of milliseconds. It used to happen on the UI thread (the file was
                // already on disk), so a burst of card art — the comp panel alone asks for dozens — froze the
                // overlay for seconds while the tracker kept running in the background.
                try
                {
                    image = await DecodeAsync(file, kind).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Discard(Key(kind, cardId), file, ex);
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or NotSupportedException)
        {
            Diag.Error($"Art {kind} {cardId} unavailable", ex);
        }

        if (image is not null)
        {
            Remember(Key(kind, cardId), image);
        }

        // Only loads in flight stay here. A finished task would keep its image alive after the memory cache dropped
        // it (so the cache limit freed nothing), and a failed one must allow a later retry (e.g. after reconnecting).
        Pending.TryRemove(Key(kind, cardId), out _);

        return image;
    }

    /// <summary>Decodes an image off the UI thread, with a limit on how many run at once.</summary>
    private static async Task<ImageSource?> DecodeAsync(string file, ArtKind kind)
    {
        await Decodes.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Decode(file, kind)).ConfigureAwait(false);
        }
        finally
        {
            Decodes.Release();
        }
    }

    private static async Task<bool> TryDownloadAsync(HttpClient http, string url, string file, string dir)
    {
        try
        {
            using var response = await http.GetAsync(url).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            if (!LooksLikeImage(bytes))
            {
                Diag.Info($"Art download was not an image ({url})");
                return false;
            }

            Directory.CreateDirectory(dir);
            await File.WriteAllBytesAsync(file + ".tmp", bytes).ConfigureAwait(false);
            File.Move(file + ".tmp", file, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            Diag.Error($"Art download failed ({url})", ex);
            return false;
        }
    }

    /// <summary>
    /// PNG, JPEG or WebP signature. A proxy or a captive portal can answer 200 with a web page, which must never land
    /// in the image cache: it would fail to decode for that card on every later start.
    /// </summary>
    private static bool LooksLikeImage(byte[] bytes) =>
        bytes.Length > 12
        && ((bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            || (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            || (bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
                && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P'));

    /// <summary>Deletes a cached file that cannot be decoded, so the next session downloads it again.</summary>
    private static void Discard(string key, string file, Exception ex)
    {
        // The card is not retried in this session: a format the system cannot decode would otherwise be
        // downloaded and thrown away again on every redraw.
        Unreadable[key] = true;
        Diag.Error($"Cached art {Path.GetFileName(file)} is unreadable and was deleted", ex);
        try
        {
            File.Delete(file);
        }
        catch (Exception deleteError) when (deleteError is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static BitmapImage Decode(string file, ArtKind kind)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bitmap.DecodePixelWidth = kind switch { ArtKind.Tile => 256, ArtKind.Splash => 512, _ => 200 };
        bitmap.UriSource = new Uri(file);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
