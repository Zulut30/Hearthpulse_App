using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Hearthpulse.Core;

namespace Hearthpulse.App.Infrastructure;

/// <summary>
/// Full Constructed card renders (HearthstoneJSON <c>v1/render/latest/{locale}/256x/{id}.png</c>) for an
/// <see cref="Image"/>: <c>i:CardRender.Card="{Binding CardId}"</c>. Cached on disk (<c>images/render-{locale}</c>) and
/// in memory (oldest dropped first), loaded and decoded off the UI thread, cropped to the card (no transparent margin). Uses <see cref="CardArt.Http"/> and
/// <see cref="CardArt.Locale"/>; kept apart from <see cref="CardArt"/> so both can grow independently.
/// </summary>
public static class CardRender
{
    // A 256 px render is about 0.4 MB decoded; the pool browser shows a few dozen at a time.
    private const int MaxCachedImages = 120;

    private static readonly ConcurrentDictionary<string, ImageSource> Memory = new();
    private static readonly ConcurrentDictionary<string, Task<ImageSource?>> Pending = new();
    private static readonly ConcurrentDictionary<string, bool> Failed = new();
    private static readonly Queue<string> MemoryOrder = new();
    private static readonly SemaphoreSlim Downloads = new(4);
    private static readonly SemaphoreSlim Decodes = new(3);

    public static readonly DependencyProperty CardProperty = DependencyProperty.RegisterAttached(
        "Card", typeof(string), typeof(CardRender), new PropertyMetadata(null, (d, e) => Apply(d, e.NewValue as string)));

    public static string? GetCard(DependencyObject d) => (string?)d.GetValue(CardProperty);

    public static void SetCard(DependencyObject d, string? value) => d.SetValue(CardProperty, value);

    /// <summary>Loads still running (the off-screen renderer waits for them).</summary>
    public static int InFlight => Pending.Count(p => !p.Value.IsCompleted);

    private static async void Apply(DependencyObject target, string? cardId)
    {
        if (target is not Image image)
        {
            return;
        }

        if (string.IsNullOrEmpty(cardId))
        {
            image.Source = null;
            return;
        }

        var key = Key(cardId);
        if (Memory.TryGetValue(key, out var cached))
        {
            image.Source = cached;
            return;
        }

        image.Source = null;
        var loaded = await Pending.GetOrAdd(key, _ => LoadAsync(cardId));
        // A virtualized row may show another card by now.
        if (Equals(image.GetValue(CardProperty), cardId))
        {
            image.Source = loaded;
        }
    }

    private static string Key(string cardId) => $"{CardArt.Locale}:{cardId}";

    private static async Task<ImageSource?> LoadAsync(string cardId)
    {
        var key = Key(cardId);
        var locale = CardArt.Locale;
        var dir = Path.Combine(AppPaths.Images, "render-" + locale);
        var file = Path.Combine(dir, cardId + ".png");
        ImageSource? image = null;
        try
        {
            if (!Failed.ContainsKey(key))
            {
                if (!File.Exists(file) && CardArt.Http is { } http)
                {
                    await Downloads.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        await DownloadAsync(http, $"https://art.hearthstonejson.com/v1/render/latest/{locale}/256x/{cardId}.png", file, dir).ConfigureAwait(false);
                    }
                    finally
                    {
                        Downloads.Release();
                    }
                }

                if (File.Exists(file))
                {
                    await Decodes.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        image = await Task.Run(() => Decode(file)).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        Failed[key] = true;
                        Diag.Error($"Cached card render {cardId} is unreadable and was deleted", ex);
                        TryDelete(file);
                    }
                    finally
                    {
                        Decodes.Release();
                    }
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or UnauthorizedAccessException)
        {
            Diag.Error($"Card render {cardId} unavailable", ex);
        }

        if (image is not null)
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

        Pending.TryRemove(key, out _);
        return image;
    }

    private static async Task DownloadAsync(HttpClient http, string url, string file, string dir)
    {
        using var response = await http.GetAsync(url).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return;
        }

        var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        // PNG only: a captive portal's web page must never land in the cache.
        if (bytes.Length < 8 || bytes[0] != 0x89 || bytes[1] != 0x50 || bytes[2] != 0x4E || bytes[3] != 0x47)
        {
            Diag.Info($"Card render download was not a PNG ({url})");
            return;
        }

        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(file + ".tmp", bytes).ConfigureAwait(false);
        File.Move(file + ".tmp", file, overwrite: true);
    }

    /// <summary>The card itself inside the 256x388 render; the rest is transparent margin.</summary>
    private static readonly Int32Rect Visible = new(15, 38, 215, 303);

    private static BitmapSource Decode(string file)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bitmap.DecodePixelWidth = 256;
        bitmap.UriSource = new Uri(file);
        bitmap.EndInit();
        bitmap.Freeze();
        if (bitmap.PixelWidth != 256 || bitmap.PixelHeight != 388)
        {
            return bitmap;
        }

        var card = new CroppedBitmap(bitmap, Visible);
        card.Freeze();
        return card;
    }

    private static void TryDelete(string file)
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
