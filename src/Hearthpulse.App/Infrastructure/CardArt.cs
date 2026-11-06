using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Hearthpulse.Core;
using Hearthpulse.Core.Cards;

namespace Hearthpulse.App.Infrastructure;

/// <summary>
/// Card images from HearthstoneJSON, cached on disk and in memory as frozen bitmaps so list
/// refreshes never flicker. Images set through the attached properties load asynchronously.
/// </summary>
public static class CardArt
{
    /// <summary>
    /// Decoded pixels kept in memory. An unbounded cache grows with every card the game shows and pushes a small PC into
    /// swapping — the app then appears frozen. A limit by count let 160 splash images (1 MB each) stay; by size it holds
    /// about 150 portraits or 400 deck tiles. The least recently used images are dropped first.
    /// </summary>
    private const long MaxCachedBytes = 24L * 1024 * 1024;

    /// <summary>How many finished load tasks are remembered; they only keep the image reachable.</summary>
    private const int MaxPendingTasks = 2000;

    /// <summary>How long art with no file is not asked for again, and how many such cards are remembered.</summary>
    private const long AbsentRetryMs = 10_000;

    private const int MaxAbsent = 4096;

    private static readonly SizedCache<ImageSource> Memory = new(MaxCachedBytes, PixelBytes);
    private static readonly ConcurrentDictionary<string, Task<ImageSource?>> Pending = new();
    private static readonly ConcurrentDictionary<string, bool> Unreadable = new();
    private static readonly CardImageFiles Files = new(downloads: 6);

    /// <summary>
    /// Art with no file and no download (offline, no client yet, a failed fetch): for <see cref="AbsentRetryMs"/> it is
    /// answered "none" at once instead of asking the disk again off the UI thread, so the match player does not repeat
    /// that check for every card without art on every step. A key leaves when its art loads; all leave when a download
    /// client is set.
    /// </summary>
    internal static ArtMisses Absent { get; } = new(AbsentRetryMs, MaxAbsent, static () => Environment.TickCount64);

    /// <summary>
    /// How many images are decoded at the same time. Decoding is the expensive part, and a burst (the comp panel
    /// asks for dozens of cards at once) must not eat the whole machine.
    /// </summary>
    private static readonly SemaphoreSlim Decodes = new(4);

    /// <summary>
    /// Pixel buffers for trimming art. A portrait needed two fresh 160 KB arrays, both on the large object heap; a bitmap
    /// copies its pixels, so the buffer is handed back right after. Kept: one per running decode and size up to 256 KB
    /// (about 1.3 MB with portraits and tiles); splash art (1 MB) is rare and not kept.
    /// </summary>
    private static readonly ArrayPool<byte> PixelBuffers = ArrayPool<byte>.Create(256 * 1024, 4);

    /// <summary>The download client; setting it lets art that was missing without one be fetched at once.</summary>
    public static HttpClient? Http
    {
        get;
        set
        {
            field = value;
            Absent.Clear();
        }
    }

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
    public static readonly DependencyProperty CardProperty = Register("Card", ArtKind.Card);

    public static string? GetTile(DependencyObject d) => (string?)d.GetValue(TileProperty);
    public static void SetTile(DependencyObject d, string? value) => d.SetValue(TileProperty, value);
    public static string? GetPortrait(DependencyObject d) => (string?)d.GetValue(PortraitProperty);
    public static void SetPortrait(DependencyObject d, string? value) => d.SetValue(PortraitProperty, value);
    public static string? GetBattlegroundsCard(DependencyObject d) => (string?)d.GetValue(BattlegroundsCardProperty);
    public static void SetBattlegroundsCard(DependencyObject d, string? value) => d.SetValue(BattlegroundsCardProperty, value);
    public static string? GetSplash(DependencyObject d) => (string?)d.GetValue(SplashProperty);
    public static void SetSplash(DependencyObject d, string? value) => d.SetValue(SplashProperty, value);
    public static string? GetCard(DependencyObject d) => (string?)d.GetValue(CardProperty);
    public static void SetCard(DependencyObject d, string? value) => d.SetValue(CardProperty, value);

    /// <summary>
    /// A border's background in the hero's colours: a small patch of its splash art magnified across the border, which
    /// smooths into a soft wash (the page backdrop's coloured light; no blur effect).
    /// </summary>
    public static readonly DependencyProperty GlowProperty = DependencyProperty.RegisterAttached("Glow", typeof(string), typeof(CardArt),
        new PropertyMetadata(null, (d, e) => ApplyGlowAsync(d, e.NewValue as string).Forget("Card art glow")));

    public static string? GetGlow(DependencyObject d) => (string?)d.GetValue(GlowProperty);
    public static void SetGlow(DependencyObject d, string? value) => d.SetValue(GlowProperty, value);

    /// <summary>The patch of the art the glow magnifies: round the middle, where a hero's face and colours sit.</summary>
    private static readonly Rect GlowPatch = new(0.36, 0.22, 0.12, 0.12);

    private static async Task ApplyGlowAsync(DependencyObject target, string? cardId)
    {
        if (target is not Border border)
        {
            return;
        }

        var image = string.IsNullOrEmpty(cardId) ? null
            : Memory.TryGetValue(Key(ArtKind.Splash, cardId), out var cached) ? cached : await GetAsync(ArtKind.Splash, cardId);
        if (!Equals(border.GetValue(GlowProperty), cardId))
        {
            return;
        }

        if (image is null)
        {
            border.Background = null;
            return;
        }

        var brush = new ImageBrush(image) { Viewbox = GlowPatch, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.Linear);
        brush.Freeze();
        border.Background = brush;
    }

    private static DependencyProperty Register(string name, ArtKind kind) =>
        DependencyProperty.RegisterAttached(name, typeof(string), typeof(CardArt),
            new PropertyMetadata(null, (d, e) => Apply(d, kind, e.NewValue as string)));

    // Property callbacks cannot await; a failed load is logged instead of reaching the app's crash handler.
    private static void Apply(DependencyObject target, ArtKind kind, string? cardId) => ApplyAsync(target, kind, cardId).Forget("Card art");

    private static async Task ApplyAsync(DependencyObject target, ArtKind kind, string? cardId)
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
        ArtKind.Card => CardProperty,
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
                // The border's own background (a dark surface token) stays the placeholder while art loads or is missing.
                if (border.ReadLocalValue(PlaceholderProperty) == DependencyProperty.UnsetValue)
                {
                    border.SetValue(PlaceholderProperty, border.Background);
                }

                border.Background = source is null
                    ? (Brush?)border.GetValue(PlaceholderProperty)
                    : new ImageBrush(source) { Stretch = Stretch.UniformToFill, AlignmentX = AlignmentX.Right };
                break;
        }
    }

    /// <summary>The background a border had before card art replaced it.</summary>
    private static readonly DependencyProperty PlaceholderProperty =
        DependencyProperty.RegisterAttached("Placeholder", typeof(Brush), typeof(CardArt), new PropertyMetadata(null));

    private static string Key(ArtKind kind, string cardId) => CardImageFiles.Key(kind, cardId);

    public static Task<ImageSource?> GetAsync(ArtKind kind, string cardId)
    {
        var key = Key(kind, cardId);
        if (Absent.Skips(key))
        {
            return Task.FromResult<ImageSource?>(null);
        }

        TrimPending();
        var load = Pending.GetOrAdd(key, static (_, art) => LoadAsync(art.kind, art.cardId), (kind, cardId));
        // A load that finished before it was added (no await on the way) removed its entry too early: drop it now, or
        // the finished task keeps its image alive past the memory cache.
        if (load.IsCompleted)
        {
            Pending.TryRemove(new KeyValuePair<string, Task<ImageSource?>>(key, load));
        }

        return load;
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

    /// <summary>Caches the image; the least recently used ones go when the cache is full.</summary>
    private static void Remember(string key, ImageSource image)
    {
        Memory.Set(key, image);
        Absent.Found(key);
    }

    /// <summary>Decoded size of an image; a cropped bitmap keeps its whole source alive.</summary>
    internal static long PixelBytes(ImageSource image) => image switch
    {
        CroppedBitmap { Source: { } source } => PixelBytes(source),
        BitmapSource bitmap => (long)bitmap.PixelWidth * bitmap.PixelHeight * Math.Max(1, (bitmap.Format.BitsPerPixel + 7) / 8),
        _ => 0,
    };

    private static async Task<ImageSource?> LoadAsync(ArtKind kind, string cardId)
    {
        var (url, file) = CardImageFiles.Art(kind, cardId, Locale, AppPaths.Images);
        ImageSource? image = null;
        if (Unreadable.ContainsKey(Key(kind, cardId)))
        {
            return null;
        }

        try
        {
            // LoadAsync starts on the caller's thread (the UI thread for a template's art): the disk is asked off it, so a
            // list of 300 games does not wait on 300 file checks before its first frame.
            if (Http is { } http && !await Task.Run(() => File.Exists(file)).ConfigureAwait(false))
            {
                // A page hero's splash has no fallback: without HearthstoneJSON's art the page keeps its stone (a Battlegrounds
                // card picture from another source would not fit the backdrop).
                await Files.EnsureArtAsync(http, url, file, kind is ArtKind.Card or ArtKind.Splash ? null : () => FallbackUrl?.Invoke(cardId)).ConfigureAwait(false);
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
                // A file the start-up clean-up just removed is not unreadable: it is fetched again next time.
                catch (Exception ex) when (ex is not (OutOfMemoryException or FileNotFoundException or DirectoryNotFoundException))
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
        else
        {
            Absent.Miss(Key(kind, cardId));
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

    /// <summary>Deletes a cached file that cannot be decoded, so the next session downloads it again.</summary>
    private static void Discard(string key, string file, Exception ex)
    {
        // The card is not retried in this session: a format the system cannot decode would otherwise be
        // downloaded and thrown away again on every redraw.
        Unreadable[key] = true;
        Diag.Error($"Cached art {Path.GetFileName(file)} is unreadable and was deleted", ex);
        CardImageFiles.TryDelete(file);
    }

    private static BitmapSource Decode(string file, ArtKind kind)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bitmap.DecodePixelWidth = kind switch { ArtKind.Tile or ArtKind.Card => 256, ArtKind.Splash => 512, _ => 200 };
        bitmap.UriSource = new Uri(file);
        bitmap.EndInit();
        bitmap.Freeze();
        return kind switch
        {
            ArtKind.Portrait or ArtKind.Splash => TrimMargins(bitmap),
            ArtKind.Tile => FadeTileMargin(bitmap),
            _ => bitmap,
        };
    }

    /// <summary>Turns the white block some deck tiles carry left of the art into a transparent fade (and hides a white edge line).</summary>
    private static BitmapSource FadeTileMargin(BitmapSource bitmap)
    {
        // A failure here keeps the art as it is: the caller would delete the cached file as unreadable.
        byte[]? buffer = null;
        try
        {
            var pixels = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            var stride = pixels.PixelWidth * 4;
            var size = stride * pixels.PixelHeight;
            buffer = PixelBuffers.Rent(size);
            pixels.CopyPixels(Int32Rect.Empty, buffer, stride, 0);
            if (!ArtTrim.CleanTile(buffer.AsSpan(0, size), pixels.PixelWidth, pixels.PixelHeight, stride, premultiplied: false))
            {
                return bitmap;
            }

            return Bitmap(bitmap, pixels.PixelWidth, pixels.PixelHeight, buffer, stride);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Diag.Error("Tile margin was not faded", ex);
            return bitmap;
        }
        finally
        {
            if (buffer is not null)
            {
                PixelBuffers.Return(buffer);
            }
        }
    }

    /// <summary>Cuts the white paper margins around square art, which show as a light ring on round portraits.</summary>
    private static BitmapSource TrimMargins(BitmapSource bitmap)
    {
        // A failure here keeps the art as it is: the caller would delete the cached file as unreadable.
        byte[]? buffer = null;
        try
        {
            var pixels = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            var stride = pixels.PixelWidth * 4;
            var size = stride * pixels.PixelHeight;
            buffer = PixelBuffers.Rent(size);
            pixels.CopyPixels(Int32Rect.Empty, buffer, stride, 0);
            if (ArtTrim.FindSquare(buffer.AsSpan(0, size), pixels.PixelWidth, pixels.PixelHeight, stride) is not { } crop)
            {
                return bitmap;
            }

            // A copy of the square only: a CroppedBitmap would keep the whole decoded source alive in the cache. The
            // square's rows move to the front of the same buffer: each lands at or before where it was read, and past
            // every row still to be read, so no second buffer is needed.
            var squareStride = crop.Size * 4;
            for (var row = 0; row < crop.Size; row++)
            {
                Buffer.BlockCopy(buffer, ((crop.Y + row) * stride) + (crop.X * 4), buffer, row * squareStride, squareStride);
            }

            return Bitmap(bitmap, crop.Size, crop.Size, buffer, squareStride);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Diag.Error("Art margins were not trimmed", ex);
            return bitmap;
        }
        finally
        {
            if (buffer is not null)
            {
                PixelBuffers.Return(buffer);
            }
        }
    }

    /// <summary>
    /// A frozen BGRA bitmap of the first <paramref name="height"/> rows of a pooled buffer. Given the array, WPF sizes its
    /// copy by the array's length, which a pooled buffer exceeds: every cached portrait kept up to 100 KB more.
    /// </summary>
    private static BitmapSource Bitmap(BitmapSource like, int width, int height, byte[] pixels, int stride)
    {
        var pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            var bitmap = BitmapSource.Create(width, height, like.DpiX, like.DpiY, PixelFormats.Bgra32, null, pinned.AddrOfPinnedObject(), stride * height, stride);
            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            pinned.Free();
        }
    }
}
