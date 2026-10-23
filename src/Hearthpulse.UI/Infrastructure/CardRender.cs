using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Hearthpulse.Core;

namespace Hearthpulse.UI.Infrastructure;

/// <summary>
/// Full Constructed card renders (HearthstoneJSON <c>v1/render/latest/{locale}/256x/{id}.png</c>) for an
/// <see cref="Image"/>: <c>i:CardRender.Card="{Binding CardId}"</c>. Cached on disk (<c>images/render-{locale}</c>) and
/// in memory (least recently used dropped first), loaded and decoded off the UI thread. Uses <see cref="CardArt.Http"/> and
/// <see cref="CardArt.Locale"/>; kept apart from <see cref="CardArt"/> so both can grow independently.
/// </summary>
public static class CardRender
{
    // A 256 px render is about 0.4 MB decoded (the crop keeps it whole; about 120 fit); the pool browser shows a few
    // dozen at a time.
    private const long MaxCachedBytes = 48L * 1024 * 1024;

    private static readonly SizedCache<IImage> Memory = new(MaxCachedBytes, PixelBytes);
    private static readonly ConcurrentDictionary<string, Task<Bitmap?>> Pending = new();
    private static readonly ConcurrentDictionary<string, bool> Failed = new();
    private static readonly ConcurrentDictionary<string, bool> TrimmedFolders = new();

    // About 200 MB of 256 px renders on disk.
    private const int MaxDiskFiles = 2000;
    private static readonly SemaphoreSlim Downloads = new(4);
    private static readonly SemaphoreSlim Decodes = new(3);

    public static readonly AttachedProperty<string?> CardProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Card", typeof(CardRender));

    static CardRender()
    {
        CardProperty.Changed.AddClassHandler<Control>((c, e) => Apply(c, e.NewValue as string));
    }

    public static string? GetCard(Control c) => c.GetValue(CardProperty);

    public static void SetCard(Control c, string? value) => c.SetValue(CardProperty, value);

    /// <summary>Loads still running (the off-screen renderer waits for them).</summary>
    public static int InFlight => Pending.Count(p => !p.Value.IsCompleted);

    // Property callbacks cannot await; a failed load is logged instead of reaching the app's crash handler.
    private static void Apply(Control target, string? cardId) => ApplyAsync(target, cardId).Forget("Card image");

    private static async Task ApplyAsync(Control target, string? cardId)
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
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            // Cropping makes an Avalonia object: on the UI thread only.
            var card = Memory.TryGetValue(key, out var known) ? known : loaded is null ? null : Remember(key, Crop(loaded));
            // A virtualized row may show another card by now.
            if (Equals(image.GetValue(CardProperty), cardId))
            {
                image.Source = card;
            }
        });
    }

    /// <summary>The card itself inside the 256x388 render; the rest is transparent margin.</summary>
    private static IImage Crop(Bitmap bitmap) =>
        bitmap.PixelSize == new PixelSize(256, 388) ? new CroppedBitmap(bitmap, new PixelRect(15, 38, 215, 303)) : bitmap;

    private static IImage Remember(string key, IImage image)
    {
        Memory.Set(key, image);
        return image;
    }

    /// <summary>Decoded size of an image; a cropped bitmap keeps its whole source alive.</summary>
    private static long PixelBytes(IImage image) => image switch
    {
        CroppedBitmap { Source: Bitmap source } => CardArt.PixelBytes(source),
        Bitmap bitmap => CardArt.PixelBytes(bitmap),
        _ => 0,
    };

    private static string Key(string cardId) => $"{CardArt.Locale}:{cardId}";

    private static async Task<Bitmap?> LoadAsync(string cardId)
    {
        // Never finish synchronously: the task must be in Pending before it removes itself (end of this method).
        await Task.Yield();
        var key = Key(cardId);
        var locale = CardArt.Locale;
        var dir = Path.Combine(AppPaths.Images, "render-" + locale);
        TrimDiskCache(dir);
        var file = Path.Combine(dir, cardId + ".png");
        Bitmap? image = null;
        try
        {
            if (!Failed.ContainsKey(key))
            {
                if (!File.Exists(file) && CardArt.Http is { } http)
                {
                    await Downloads.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        if (!await DownloadAsync(http, $"https://art.hearthstonejson.com/v1/render/latest/{locale}/256x/{cardId}.png", file, dir).ConfigureAwait(false))
                        {
                            // No render for this card (404, not a PNG): do not ask again this session.
                            Failed[key] = true;
                        }
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
                        image = await Task.Run(() =>
                        {
                            using var stream = File.OpenRead(file);
                            return Bitmap.DecodeToWidth(stream, 256, BitmapInterpolationMode.HighQuality);
                        }).ConfigureAwait(false);
                    }
                    // A file the start-up clean-up just removed is not unreadable: it is fetched again next time.
                    catch (Exception ex) when (ex is not (OutOfMemoryException or FileNotFoundException or DirectoryNotFoundException))
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
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or UnauthorizedAccessException
                                       or InvalidOperationException or ArgumentException)
        {
            Diag.Error($"Card render {cardId} unavailable", ex);
        }

        Pending.TryRemove(key, out _);
        return image;
    }

    /// <returns>False when the server has no PNG for the card.</returns>
    private static async Task<bool> DownloadAsync(HttpClient http, string url, string file, string dir)
    {
        using var response = await http.GetAsync(url).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        // PNG only: a captive portal's web page must never land in the cache.
        if (bytes.Length < 8 || bytes[0] != 0x89 || bytes[1] != 0x50 || bytes[2] != 0x4E || bytes[3] != 0x47)
        {
            Diag.Info($"Card render download was not a PNG ({url})");
            return false;
        }

        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(file + ".tmp", bytes).ConfigureAwait(false);
        File.Move(file + ".tmp", file, overwrite: true);
        return true;
    }

    /// <summary>
    /// Once per session and folder: keeps the newest <see cref="MaxDiskFiles"/> renders (a Wild pool alone can be
    /// thousands of cards, ~100 KB each).
    /// </summary>
    private static void TrimDiskCache(string dir)
    {
        if (!TrimmedFolders.TryAdd(dir, true) || !Directory.Exists(dir))
        {
            return;
        }

        try
        {
            var files = new DirectoryInfo(dir).GetFiles("*.png");
            foreach (var old in files.OrderByDescending(f => f.LastWriteTimeUtc).Skip(MaxDiskFiles))
            {
                old.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diag.Error("Card render cache not trimmed", ex);
        }
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
