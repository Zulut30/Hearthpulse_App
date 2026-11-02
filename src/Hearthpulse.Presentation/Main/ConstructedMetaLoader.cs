using Hearthpulse.Core;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Localization;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main;
#else
namespace Hearthpulse.UI.Main;
#endif

/// <summary>
/// The Constructed stats of every format in use, the same in both apps. A format is in use once it is the meta page's
/// default (<see cref="AppSettings.MetaFormat"/>), a page shows it or a game is played in it. <see cref="Ensure"/> hands
/// over stats already in and otherwise joins or starts a download. <see cref="ReloadAllAsync"/> (start, a filter or the
/// language changed, the 6-hour refresh, Russian archetype names arrived) loads every format again and overtakes a
/// download still running: its many requests stop and its answer is dropped. The tier list goes to the page as soon as
/// it is in; the full stats then go to the page and to the game. Russian archetype names are fetched beside the first
/// load while the interface is Russian. Everything runs on the UI thread.
/// </summary>
/// <param name="download">Loads one format's stats (<see cref="ConstructedMeta.LoadAsync"/> with the host's data and
/// cards and the meta filters), reporting the tier list first.</param>
/// <param name="fetchNames">Fetches the Russian archetype names (<see cref="ArchetypeTranslations.LoadAsync(HttpClient, string?, CancellationToken)"/>).</param>
/// <param name="defaultFormat">The format key of <see cref="AppSettings.MetaFormat"/>.</param>
/// <param name="pageFormat">The format key the Standard or Wild page shows, if any.</param>
/// <param name="showOnPage">Shows stats (or none) on the meta page.</param>
/// <param name="useInGame">Gives a format's stats to the live session.</param>
/// <param name="loadEnded">A load ended, its parsing garbage can go (the session's memory trim).</param>
public sealed class ConstructedMetaLoader(
    Func<string, IProgress<ConstructedMeta>, CancellationToken, Task<ConstructedMeta?>> download,
    Func<Task<ArchetypeTranslations>> fetchNames,
    Func<string> defaultFormat,
    Func<string?> pageFormat,
    Action<ConstructedMeta?> showOnPage,
    Action<ConstructedMeta> useInGame,
    Action loadEnded)
{
    private readonly Dictionary<string, ConstructedMeta> _metas = new(StringComparer.Ordinal);
    private readonly HashSet<string> _formats = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Request, CancellationTokenSource Cancel)> _loads = new(StringComparer.Ordinal);
    private int _request;
    private Task? _names;

    /// <summary>The stats of the default format (<see cref="AppSettings.MetaFormat"/>), once loaded: card pool and render scenes.</summary>
    public ConstructedMeta? Default { get; private set; }

    /// <summary>A page or a game wants a format: its stats at once when they are in, else a download unless one runs.</summary>
    public void Ensure(string format)
    {
        _formats.Add(format);
        if (_metas.TryGetValue(format, out var meta))
        {
            showOnPage(meta);
            useInGame(meta);
        }
        else if (!_loads.ContainsKey(format))
        {
            LoadAsync(format).Forget("Constructed meta load");
        }
    }

    /// <summary>Loads every format in use again (the default one included), overtaking downloads still running.</summary>
    public Task ReloadAllAsync()
    {
        _formats.Add(defaultFormat());
        return Task.WhenAll(_formats.ToList().Select(LoadAsync));
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD103:Call async methods when in an async method", Justification = "Cancel runs before the new request registers, with no await point in between on the UI thread; StartNames only starts the shared load and does not block.")]
    private async Task LoadAsync(string format)
    {
        var request = ++_request;
        if (_loads.TryGetValue(format, out var running))
        {
            // Overtaken: its many requests (mulligan numbers, matchups) stop instead of running on for nothing.
            running.Cancel.Cancel();
        }

        // Disposed when this load ends: by then a newer request has replaced it in the table, or it removes itself.
        using var cancel = new CancellationTokenSource();
        _loads[format] = (request, cancel);
        bool Latest() => _loads.TryGetValue(format, out var load) && load.Request == request;
        var done = false;
        var tierList = new Progress<ConstructedMeta>(m =>
        {
            if (!done && Latest())
            {
                showOnPage(m);
            }
        });
        try
        {
            StartNames();
            var meta = await download(format, tierList, cancel.Token).ConfigureAwait(true);
            done = true;
            if (!Latest())
            {
                return;
            }

            if (meta is not null)
            {
                _metas[format] = meta;
                if (format == defaultFormat())
                {
                    Default = meta;
                }

                showOnPage(meta);
                useInGame(meta);
            }
            else if (pageFormat() == format)
            {
                showOnPage(null);
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Diag.Error("Constructed stats unavailable", ex);
        }
        finally
        {
            if (Latest())
            {
                _loads.Remove(format);
            }

            // A load (another format, filters, language) leaves about 30 MB of parsing garbage that an idle app would
            // never collect: it is given back once the window is minimized or let go and things are quiet.
            loadEnded();
        }
    }

    /// <summary>Russian archetype names, once per run and only while the interface is Russian (the only language that uses them).</summary>
    private void StartNames()
    {
        if (Loc.IsRussian && _names is null)
        {
            _names = LoadNamesAsync();
            _names.Forget("Archetype names load");
        }
    }

    /// <summary>
    /// Fetched beside the stats, never before them: when the names arrive every format loads again (from the cache) so
    /// its labels use them. An empty answer lets the next load ask again (the fetch itself backs off after failures).
    /// </summary>
    private async Task LoadNamesAsync()
    {
        var names = await fetchNames().ConfigureAwait(true);
        if (names.Count == 0)
        {
            _names = null;
            return;
        }

        ArchetypeTranslations.Current = names;
        if (Loc.IsRussian)
        {
            await ReloadAllAsync().ConfigureAwait(true);
        }
    }
}
