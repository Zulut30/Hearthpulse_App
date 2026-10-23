using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Hearthlens.Core;

namespace Hearthlens.UI.Infrastructure;

public enum ArtKind
{
    /// <summary>Deck-list strip (256x59).</summary>
    Tile,

    /// <summary>Square card art crop, used for hero portraits.</summary>
    Portrait,

    /// <summary>Full Battlegrounds card render.</summary>
    BattlegroundsCard,
}

/// <summary>
/// Card images from HearthstoneJSON, cached on disk and in memory so list refreshes never flicker. Set through the
/// attached properties on an <see cref="Image"/> (source) or a <see cref="Border"/> (background); loads asynchronously.
/// </summary>
public static class CardArt
{
    private static readonly ConcurrentDictionary<string, Bitmap> Memory = new();
    private static readonly ConcurrentDictionary<string, Task<Bitmap?>> Pending = new();
    private static readonly ConcurrentDictionary<string, bool> Unreadable = new();

    /// <summary>
    /// How many card images stay in memory. Without a limit every card a long session shows stays loaded and a small
    /// PC starts swapping; the oldest images are dropped first.
    /// </summary>
    private const int MaxCachedImages = 160;

    private static readonly Queue<string> MemoryOrder = new();
    private static readonly SemaphoreSlim Downloads = new(6);

    public static HttpClient? Http { get; set; }

    public static string Locale { get; set; } = "enUS";

    /// <summary>
    /// Optional fallback URL provider for art HearthstoneJSON does not have (e.g. hsbg.cards), given the card id.
    /// Wired by the host after card data loads; null keeps HearthstoneJSON as the only source.
    /// </summary>
    public static Func<string, string?>? FallbackUrl { get; set; }

    public static readonly AttachedProperty<string?> TileProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Tile", typeof(CardArt));

    public static readonly AttachedProperty<string?> PortraitProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Portrait", typeof(CardArt));

    public static readonly AttachedProperty<string?> BattlegroundsCardProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("BattlegroundsCard", typeof(CardArt));

    static CardArt()
    {
        TileProperty.Changed.AddClassHandler<Control>((c, e) => Apply(c, ArtKind.Tile, e.NewValue as string));
        PortraitProperty.Changed.AddClassHandler<Control>((c, e) => Apply(c, ArtKind.Portrait, e.NewValue as string));
        BattlegroundsCardProperty.Changed.AddClassHandler<Control>((c, e) => Apply(c, ArtKind.BattlegroundsCard, e.NewValue as string));
    }

    public static string? GetTile(Control c) => c.GetValue(TileProperty);
    public static void SetTile(Control c, string? value) => c.SetValue(TileProperty, value);
    public static string? GetPortrait(Control c) => c.GetValue(PortraitProperty);
    public static void SetPortrait(Control c, string? value) => c.SetValue(PortraitProperty, value);
    public static string? GetBattlegroundsCard(Control c) => c.GetValue(BattlegroundsCardProperty);
    public static void SetBattlegroundsCard(Control c, string? value) => c.SetValue(BattlegroundsCardProperty, value);

    /// <summary>Number of image loads still in flight (the off-screen renderer waits for them).</summary>
    public static int InFlight => Pending.Count(p => !p.Value.IsCompleted);

    private static async void Apply(Control target, ArtKind kind, string? cardId)
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
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            // The element may have been recycled for another card while we were downloading.
            if (Equals(target.GetValue(PropertyFor(kind)), cardId))
            {
                SetSource(target, image);
            }
        });
    }

    private static AttachedProperty<string?> PropertyFor(ArtKind kind) => kind switch
    {
        ArtKind.Tile => TileProperty,
        ArtKind.Portrait => PortraitProperty,
        _ => BattlegroundsCardProperty,
    };

    private static void SetSource(Control target, Bitmap? source)
    {
        switch (target)
        {
            case Image image:
                image.Source = source;
                break;
            case Border border:
                border.Background = source is null ? null : new ImageBrush(source) { Stretch = Stretch.UniformToFill, AlignmentX = AlignmentX.Right };
                break;
        }
    }

    private static string Key(ArtKind kind, string cardId) => $"{kind}:{cardId}";

    public static Task<Bitmap?> GetAsync(ArtKind kind, string cardId) =>
        Pending.GetOrAdd(Key(kind, cardId), _ => Task.Run(() => LoadAsync(kind, cardId)));

    private static async Task<Bitmap?> LoadAsync(ArtKind kind, string cardId)
    {
        var (url, ext) = kind switch
        {
            ArtKind.Tile => ($"https://art.hearthstonejson.com/v1/tiles/{cardId}.png", "png"),
            ArtKind.Portrait => ($"https://art.hearthstonejson.com/v1/256x/{cardId}.jpg", "jpg"),
            _ => ($"https://art.hearthstonejson.com/v1/bgs/latest/{Locale}/256x/{cardId}.png", "png"),
        };
        var dir = Path.Combine(AppPaths.Images, kind.ToString().ToLowerInvariant());
        var file = Path.Combine(dir, $"{cardId}.{ext}");
        Bitmap? image = null;
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
                try
                {
                    await using var stream = File.OpenRead(file);
                    image = Bitmap.DecodeToWidth(stream, kind == ArtKind.Tile ? 256 : 200, BitmapInterpolationMode.HighQuality);
                }
                catch (Exception ex) when (ex is not (OutOfMemoryException or IOException))
                {
                    Discard(Key(kind, cardId), file, ex);
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or NotSupportedException or InvalidOperationException
                                       or ArgumentException)
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

    /// <summary>Caches the image and drops the oldest ones when the cache is full.</summary>
    private static void Remember(string key, Bitmap image)
    {
        Memory[key] = image;
        lock (MemoryOrder)
        {
            MemoryOrder.Enqueue(key);
            while (MemoryOrder.Count > MaxCachedImages)
            {
                // The bitmap is not disposed: a control may still show it; the GC frees it once nothing does.
                Memory.TryRemove(MemoryOrder.Dequeue(), out _);
            }
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
}
