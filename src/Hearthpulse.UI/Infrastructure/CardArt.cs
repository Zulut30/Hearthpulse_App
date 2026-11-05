using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Hearthpulse.Core;
using Hearthpulse.Core.Cards;

namespace Hearthpulse.UI.Infrastructure;

/// <summary>
/// Card images from HearthstoneJSON, cached on disk and in memory so list refreshes never flicker. Set through the
/// attached properties on an <see cref="Image"/> (source) or a <see cref="Border"/> (background); loads asynchronously.
/// </summary>
public static class CardArt
{
    /// <summary>
    /// Decoded pixels kept in memory. Without a limit every card a long session shows stays loaded and a small PC starts
    /// swapping; by size it holds about 150 portraits or 400 deck tiles. The least recently used images are dropped first.
    /// </summary>
    private const long MaxCachedBytes = 24L * 1024 * 1024;

    private static readonly SizedCache<Bitmap> Memory = new(MaxCachedBytes, PixelBytes);
    private static readonly ConcurrentDictionary<string, Task<Bitmap?>> Pending = new();
    private static readonly ConcurrentDictionary<string, bool> Unreadable = new();
    private static readonly CardImageFiles Files = new(downloads: 6);

    /// <summary>
    /// How many images are decoded at the same time. A burst (the comp panel asks for dozens of cards at once) must not
    /// take every core; same limit as the Windows app.
    /// </summary>
    private static readonly SemaphoreSlim Decodes = new(4);

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

    public static readonly AttachedProperty<string?> CardProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Card", typeof(CardArt));

    /// <summary>Full-size art (512 px): the hero behind a main-window page.</summary>
    public static readonly AttachedProperty<string?> SplashProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Splash", typeof(CardArt));

    /// <summary>
    /// A border's background in the hero's colours: a small patch of its splash art magnified across the border, which
    /// smooths into a soft wash (the page backdrop's coloured light; no blur effect).
    /// </summary>
    public static readonly AttachedProperty<string?> GlowProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Glow", typeof(CardArt));

    /// <summary>The patch of the art the glow magnifies: round the middle, where a hero's face and colours sit.</summary>
    private static readonly RelativeRect GlowPatch = new(0.36, 0.22, 0.12, 0.12, RelativeUnit.Relative);

    static CardArt()
    {
        GlowProperty.Changed.AddClassHandler<Control>((c, e) => ApplyGlowAsync(c, e.NewValue as string).Forget("Card art glow"));
        TileProperty.Changed.AddClassHandler<Control>((c, e) => Apply(c, ArtKind.Tile, e.NewValue as string));
        PortraitProperty.Changed.AddClassHandler<Control>((c, e) => Apply(c, ArtKind.Portrait, e.NewValue as string));
        BattlegroundsCardProperty.Changed.AddClassHandler<Control>((c, e) => Apply(c, ArtKind.BattlegroundsCard, e.NewValue as string));
        CardProperty.Changed.AddClassHandler<Control>((c, e) => Apply(c, ArtKind.Card, e.NewValue as string));
        SplashProperty.Changed.AddClassHandler<Control>((c, e) => Apply(c, ArtKind.Splash, e.NewValue as string));
    }

    public static string? GetTile(Control c) => c.GetValue(TileProperty);
    public static void SetTile(Control c, string? value) => c.SetValue(TileProperty, value);
    public static string? GetPortrait(Control c) => c.GetValue(PortraitProperty);
    public static void SetPortrait(Control c, string? value) => c.SetValue(PortraitProperty, value);
    public static string? GetBattlegroundsCard(Control c) => c.GetValue(BattlegroundsCardProperty);
    public static void SetBattlegroundsCard(Control c, string? value) => c.SetValue(BattlegroundsCardProperty, value);
    public static string? GetCard(Control c) => c.GetValue(CardProperty);
    public static void SetCard(Control c, string? value) => c.SetValue(CardProperty, value);
    public static string? GetGlow(Control c) => c.GetValue(GlowProperty);
    public static void SetGlow(Control c, string? value) => c.SetValue(GlowProperty, value);
    public static string? GetSplash(Control c) => c.GetValue(SplashProperty);
    public static void SetSplash(Control c, string? value) => c.SetValue(SplashProperty, value);

    /// <summary>Number of image loads still in flight (the off-screen renderer waits for them).</summary>
    public static int InFlight => Pending.Count(p => !p.Value.IsCompleted);

    // Property callbacks cannot await; a failed load is logged instead of reaching the app's crash handler.
    private static void Apply(Control target, ArtKind kind, string? cardId) => ApplyAsync(target, kind, cardId).Forget("Card art");

    private static async Task ApplyAsync(Control target, ArtKind kind, string? cardId)
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

    private static async Task ApplyGlowAsync(Control target, string? cardId)
    {
        if (target is not Border border)
        {
            return;
        }

        var image = string.IsNullOrEmpty(cardId) ? null
            : Memory.TryGetValue(Key(ArtKind.Splash, cardId), out var cached) ? cached : await GetAsync(ArtKind.Splash, cardId);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (Equals(border.GetValue(GlowProperty), cardId))
            {
                border.Background = image is null ? null : new ImageBrush(image) { SourceRect = GlowPatch, Stretch = Stretch.Fill };
            }
        });
    }

    private static AttachedProperty<string?> PropertyFor(ArtKind kind) => kind switch
    {
        ArtKind.Tile => TileProperty,
        ArtKind.Portrait => PortraitProperty,
        ArtKind.Card => CardProperty,
        ArtKind.Splash => SplashProperty,
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
                // The border's own background (a dark surface token) stays the placeholder while art loads or is missing.
                if (!border.IsSet(PlaceholderProperty))
                {
                    border.SetValue(PlaceholderProperty, border.Background);
                }

                border.Background = source is null
                    ? border.GetValue(PlaceholderProperty)
                    : new ImageBrush(source) { Stretch = Stretch.UniformToFill, AlignmentX = AlignmentX.Right };
                break;
        }
    }

    /// <summary>The background a border had before card art replaced it.</summary>
    private static readonly AttachedProperty<IBrush?> PlaceholderProperty =
        AvaloniaProperty.RegisterAttached<Border, IBrush?>("Placeholder", typeof(CardArt));

    private static string Key(ArtKind kind, string cardId) => CardImageFiles.Key(kind, cardId);

    public static Task<Bitmap?> GetAsync(ArtKind kind, string cardId) =>
        Pending.GetOrAdd(Key(kind, cardId), static (_, art) => Task.Run(() => LoadAsync(art.kind, art.cardId)), (kind, cardId));

    private static async Task<Bitmap?> LoadAsync(ArtKind kind, string cardId)
    {
        var (url, file) = CardImageFiles.Art(kind, cardId, Locale, AppPaths.Images);
        Bitmap? image = null;
        if (Unreadable.ContainsKey(Key(kind, cardId)))
        {
            return null;
        }

        try
        {
            // LoadAsync already runs on the thread pool (GetAsync starts it with Task.Run): the file check that
            // EnsureArtAsync makes is off the UI thread.
            if (Http is { } http)
            {
                await Files.EnsureArtAsync(http, url, file, kind == ArtKind.Card ? null : () => FallbackUrl?.Invoke(cardId)).ConfigureAwait(false);
            }

            if (File.Exists(file))
            {
                await Decodes.WaitAsync().ConfigureAwait(false);
                try
                {
                    await using var stream = File.OpenRead(file);
                    image = Bitmap.DecodeToWidth(stream, kind switch { ArtKind.Tile or ArtKind.Card => 256, ArtKind.Splash => 512, _ => 200 },
                        BitmapInterpolationMode.HighQuality);
                    image = kind switch
                    {
                        ArtKind.Portrait or ArtKind.Splash => TrimMargins(image),
                        ArtKind.Tile => FadeTileMargin(image),
                        _ => image,
                    };
                }
                catch (Exception ex) when (ex is not (OutOfMemoryException or IOException))
                {
                    Discard(Key(kind, cardId), file, ex);
                }
                finally
                {
                    Decodes.Release();
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

    /// <summary>Cuts the white paper margins around square art, which show as a light ring on round portraits.</summary>
    private static Bitmap TrimMargins(Bitmap bitmap)
    {
        if (bitmap.Format is not { BitsPerPixel: 32 } format || bitmap.AlphaFormat is not { } alpha)
        {
            return bitmap;
        }

        var size = bitmap.PixelSize;
        var stride = size.Width * 4;
        var buffer = new byte[stride * size.Height];
        var pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var address = pin.AddrOfPinnedObject();
            bitmap.CopyPixels(new PixelRect(size), address, buffer.Length, stride);
            if (ArtTrim.FindSquare(buffer, size.Width, size.Height, stride) is not { } crop)
            {
                return bitmap;
            }

            var cropped = new Bitmap(format, alpha, address + (crop.Y * stride) + (crop.X * 4), new PixelSize(crop.Size, crop.Size), bitmap.Dpi, stride);
            bitmap.Dispose();
            return cropped;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The art stays as it is: the caller would delete the cached file as unreadable.
            Diag.Error("Art margins were not trimmed", ex);
            return bitmap;
        }
        finally
        {
            pin.Free();
        }
    }

    /// <summary>Turns the white block some deck tiles carry left of the art into a transparent fade (and hides a white edge line).</summary>
    private static Bitmap FadeTileMargin(Bitmap bitmap)
    {
        if (bitmap.Format is not { BitsPerPixel: 32 } format || bitmap.AlphaFormat is not { } alpha)
        {
            return bitmap;
        }

        var size = bitmap.PixelSize;
        var stride = size.Width * 4;
        var buffer = new byte[stride * size.Height];
        var pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var address = pin.AddrOfPinnedObject();
            bitmap.CopyPixels(new PixelRect(size), address, buffer.Length, stride);
            // Opaque pixels read the same premultiplied or not; the faded copy needs an alpha channel either way.
            var premultiplied = alpha != AlphaFormat.Unpremul;
            if (!ArtTrim.CleanTile(buffer, size.Width, size.Height, stride, premultiplied))
            {
                return bitmap;
            }

            var faded = new Bitmap(format, premultiplied ? AlphaFormat.Premul : AlphaFormat.Unpremul, address, size, bitmap.Dpi, stride);
            bitmap.Dispose();
            return faded;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The art stays as it is: the caller would delete the cached file as unreadable.
            Diag.Error("Tile margin was not faded", ex);
            return bitmap;
        }
        finally
        {
            pin.Free();
        }
    }

    /// <summary>Caches the image; the least recently used ones go when the cache is full (not disposed: a control may still show one).</summary>
    private static void Remember(string key, Bitmap image) => Memory.Set(key, image);

    /// <summary>Decoded size of a bitmap (32 bits a pixel).</summary>
    internal static long PixelBytes(Bitmap bitmap) => (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4;

    /// <summary>Deletes a cached file that cannot be decoded, so the next session downloads it again.</summary>
    private static void Discard(string key, string file, Exception ex)
    {
        // The card is not retried in this session: a format the system cannot decode would otherwise be
        // downloaded and thrown away again on every redraw.
        Unreadable[key] = true;
        Diag.Error($"Cached art {Path.GetFileName(file)} is unreadable and was deleted", ex);
        CardImageFiles.TryDelete(file);
    }
}
