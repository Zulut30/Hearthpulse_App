using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Localization;
using Hearthpulse.UI.Main;

namespace Hearthpulse.Tests;

/// <summary>
/// <see cref="ConstructedMetaLoader"/>, the Constructed stats download both hosts used to keep their own copy of: a page
/// or a game joins a running download, a reload overtakes it (cancelled, its answer dropped), the tier list shows early,
/// and Russian archetype names load every format again. Downloads are the test's; the UI thread is this one (no
/// synchronization context, so a finished download continues at once).
/// </summary>
[Collection(nameof(GlobalLanguage))]
public sealed class ConstructedMetaLoaderTests : IDisposable
{
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private readonly List<(string Format, ArchetypeTranslations Names, IProgress<ConstructedMeta> TierList, CancellationToken Cancel, TaskCompletionSource<ConstructedMeta?> Answer)> _downloads = [];
    private readonly List<string> _page = [];
    private readonly List<string> _game = [];
    private readonly List<TaskCompletionSource<ArchetypeTranslations>> _names = [];
    private readonly List<ArchetypeTranslations> _namesArrived = [];
    private string? _pageFormat;
    private int _ended;

    public ConstructedMetaLoaderTests()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        Language(Loc.English);
    }

    public void Dispose()
    {
        SynchronizationContext.SetSynchronizationContext(_context);
        Language(Loc.English);
    }

    private ConstructedMetaLoader Loader(Action<string>? onPage = null) => new(
        (format, names, tierList, cancel) =>
        {
            var answer = new TaskCompletionSource<ConstructedMeta?>();
            _downloads.Add((format, names, tierList, cancel, answer));
            return answer.Task;
        },
        () =>
        {
            var names = new TaskCompletionSource<ArchetypeTranslations>();
            _names.Add(names);
            return names.Task;
        },
        () => "standard",
        () => _pageFormat,
        meta =>
        {
            var text = meta is null ? "none" : Name(meta);
            lock (_page)
            {
                _page.Add(text);
            }

            onPage?.Invoke(text);
        },
        meta => _game.Add(Name(meta)),
        () => _ended++,
        _namesArrived.Add);

    private static ConstructedMeta Meta(string name) => new("standard", "legend", "past_week", DateTime.UnixEpoch, 0, [], []) { Source = name };

    private static string Name(ConstructedMeta meta) => meta.Source;

    [Fact]
    public void A_page_joins_the_running_download_and_then_gets_the_stats_at_once()
    {
        var loader = Loader();
        loader.Ensure("wild");
        loader.Ensure("wild");
        Assert.Single(_downloads);

        _downloads[0].Answer.SetResult(Meta("wild-1"));
        Assert.Equal(["wild-1"], _page);
        Assert.Equal(["wild-1"], _game);
        Assert.Equal(1, _ended);
        Assert.Null(loader.Default); // not the default format

        loader.Ensure("wild");
        Assert.Single(_downloads);
        Assert.Equal(["wild-1", "wild-1"], _page);
        Assert.Equal(["wild-1", "wild-1"], _game);
    }

    [Fact]
    public void A_reload_overtakes_a_running_download_and_its_late_answer_is_dropped()
    {
        var loader = Loader();
        loader.Ensure("standard");
        var reload = loader.ReloadAllAsync();
        Assert.Equal(2, _downloads.Count);
        Assert.True(_downloads[0].Cancel.IsCancellationRequested);

        _downloads[0].Answer.SetResult(Meta("old"));
        _downloads[1].Answer.SetResult(Meta("new"));

        Assert.True(reload.IsCompletedSuccessfully);
        Assert.Equal(["new"], _page);
        Assert.Equal(["new"], _game);
        Assert.Equal("new", Name(loader.Default!));
        Assert.Equal(2, _ended);
    }

    [Fact]
    public void Every_format_in_use_loads_again_and_a_failed_one_clears_only_the_page_that_shows_it()
    {
        var loader = Loader();
        loader.Ensure("wild");
        _downloads[0].Answer.SetResult(Meta("wild-1"));
        _page.Clear();

        _pageFormat = "wild";
        var reload = loader.ReloadAllAsync();
        Assert.Equal(["standard", "wild"], _downloads.Skip(1).Select(d => d.Format).Order(StringComparer.Ordinal));
        foreach (var download in _downloads.Skip(1))
        {
            download.Answer.SetResult(null);
        }

        Assert.True(reload.IsCompletedSuccessfully);
        Assert.Equal(["none"], _page); // wild's page cleared once; standard's failure leaves it alone
        Assert.Null(loader.Default);
    }

    [Fact]
    public void A_failed_download_is_logged_and_the_next_page_request_downloads_again()
    {
        var loader = Loader();
        loader.Ensure("wild");
        _downloads[0].Answer.SetException(new InvalidOperationException("api down"));

        Assert.Empty(_page);
        Assert.Contains("Constructed stats unavailable: System.InvalidOperationException: api down", TestDataFolder.ReadLog(), StringComparison.Ordinal);
        loader.Ensure("wild");
        Assert.Equal(2, _downloads.Count);
    }

    [Fact]
    public async Task The_tier_list_shows_early_but_not_once_the_download_was_overtaken()
    {
        var shown = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loader = Loader(text => shown.TrySetResult(text));
        loader.Ensure("standard");

        _downloads[0].TierList.Report(Meta("tiers"));
        Assert.Equal("tiers", await shown.Task.WaitAsync(TimeSpan.FromSeconds(10)));

        // Overtaken: a report from the old download is not shown (the new download's answer is the next page text).
        shown = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = loader.ReloadAllAsync();
        _downloads[0].TierList.Report(Meta("old tiers"));
        _downloads[1].Answer.SetResult(Meta("new"));
        Assert.Equal("new", await shown.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        lock (_page)
        {
            Assert.DoesNotContain("old tiers", _page);
        }
    }

    [Fact]
    public void In_russian_the_names_are_fetched_once_and_their_arrival_loads_every_format_again()
    {
        Language(Loc.Russian);
        var loader = Loader();
        loader.Ensure("wild");
        loader.Ensure("standard");
        Assert.Single(_names);

        // An empty answer lets the next load ask again.
        _names[0].SetResult(ArchetypeTranslations.Empty);
        _downloads[0].Answer.SetResult(Meta("wild-1"));
        _downloads[1].Answer.SetResult(Meta("standard-1"));
        _ = loader.ReloadAllAsync();
        Assert.Equal(2, _names.Count);

        var names = ArchetypeTranslations.Parse("""{"items": [{"name_en": "Big Mage", "name_ru": "Большой маг"}]}""");
        var before = _downloads.Count;
        _names[1].SetResult(names);
        Assert.Same(names, loader.Names);
        Assert.Same(names, Assert.Single(_namesArrived));
        Assert.Equal(before + 2, _downloads.Count);
        Assert.All(_downloads.Skip(before), d => Assert.Same(names, d.Names));
        Assert.All(_downloads.Take(before), d => Assert.Same(ArchetypeTranslations.Empty, d.Names));
    }

    /// <summary>
    /// Switches the language on the headless UI thread: views the Avalonia binding tests leave alive follow every
    /// switch, and they may only be touched from that thread.
    /// </summary>
    private static void Language(string language) => LocBindingTests.Headless.Run(() => Loc.Instance.SetLanguage(language));
}
