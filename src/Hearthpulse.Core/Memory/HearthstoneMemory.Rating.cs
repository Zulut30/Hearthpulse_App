using System.Globalization;

namespace Hearthpulse.Core.Memory;

public sealed partial class HearthstoneMemory
{
    // The NetCache object and fields the client used when this was written. Only when a game update removes that
    // class is the rating looked for under another name (DiscoverRating).
    private static readonly RatingLayout KnownRatingLayout =
        new("NetCacheBaconRatingInfo", "<Rating>k__BackingField", "<DuosRating>k__BackingField");

    // The search walks the whole NetCache map, so it runs no more often than the cached NetCache lookup refreshes.
    private static readonly TimeSpan RatingSearchInterval = TimeSpan.FromMinutes(3);

    private RatingLayout _ratingLayout = KnownRatingLayout;
    private DateTime _nextRatingDiscovery = DateTime.MinValue;
    private DateTime _nextRatingMissNote = DateTime.MinValue;

    /// <summary>A NetCache class plus the int fields holding the solo and, when the client reports one, the Duos rating.</summary>
    private sealed record RatingLayout(string ClassName, string SoloField, string? DuosField);

    public BattlegroundsRating? GetBattlegroundsRating() => Safe(rt =>
    {
        var layout = _ratingLayout;
        var rating = ReadRating(CachedNetCacheObject(rt, layout.ClassName), layout);
        if (rating is not null || DateTime.UtcNow < _nextRatingDiscovery)
        {
            return rating;
        }

        _nextRatingDiscovery = DateTime.UtcNow.Add(RatingSearchInterval);
        return ShouldSearchForRating(rating, knownClassLoaded: rt.FindClass(KnownRatingLayout.ClassName) is not null)
            ? DiscoverRating(rt)
            : null;
    });

    /// <summary>
    /// Whether to look for the rating under another class: only when nothing was read and this client build has no
    /// class by the known name. A player without a Battlegrounds rating (0/0) or a rating the server has not sent yet
    /// leaves the known class in place, and then there is nothing to search for.
    /// </summary>
    internal static bool ShouldSearchForRating(BattlegroundsRating? read, bool knownClassLoaded) =>
        read is null && !knownClassLoaded;

    private static BattlegroundsRating? ReadRating(MonoObject info, RatingLayout layout)
    {
        if (info.IsNull)
        {
            return null;
        }

        return RatingFrom(info.Int(layout.SoloField, -1), layout.DuosField is null ? -1 : info.Int(layout.DuosField, -1));
    }

    /// <summary>The rating the two fields give; null when neither holds one (an unrated player reads 0/0).</summary>
    internal static BattlegroundsRating? RatingFrom(int solo, int duos) =>
        solo <= 0 && duos <= 0 ? null : new BattlegroundsRating(solo, duos);

    /// <summary>
    /// Finds the rating after a game update removed <c>NetCacheBaconRatingInfo</c>: the one NetCache object whose class
    /// looks like a Battlegrounds rating cache, read through the same solo and Duos rating fields (matched by
    /// <see cref="Field"/> also under a respelled name). None or several such objects give null: a guess could record a
    /// wrong number in the history. The layout that is found is remembered (and logged).
    /// </summary>
    private BattlegroundsRating? DiscoverRating(MonoRuntime rt)
    {
        var netCache = FindService(rt, "NetCache")["m_netCache"];
        if (netCache.IsNull)
        {
            return null;
        }

        var objects = netCache.MapValues().Select(o => (Object: o, Name: o.ClassName)).ToList();
        if (SingleRatingCache(objects.Select(o => o.Name).ToList()) is not { } index)
        {
            if (DateTime.UtcNow >= _nextRatingMissNote)
            {
                _nextRatingMissNote = DateTime.UtcNow.AddMinutes(10);
                Diag.Info("Battlegrounds rating: NetCacheBaconRatingInfo is gone and no single other rating cache was found");
            }

            return null;
        }

        var (candidate, className) = objects[index];
        if (candidate.Class is not { } cls)
        {
            return null;
        }

        var soloField = Field(cls, "rating.solo", [KnownRatingLayout.SoloField], integer: true);
        var soloValue = soloField is null ? -1 : candidate.Int(soloField, -1);
        if (soloField is null || !IsPlausibleRating(soloValue))
        {
            return null;
        }

        // A Duos field is kept even while it reads 0: the client only fills it once that ladder is played.
        var duosField = Field(cls, "rating.duos", [KnownRatingLayout.DuosField!], integer: true);
        var duosValue = duosField is null ? -1 : candidate.Int(duosField, -1);
        if (duosValue is < 0 or >= 100_000)
        {
            duosField = null;
        }

        var layout = new RatingLayout(className!, soloField, duosField);
        lock (_netCacheObjects)
        {
            _netCacheObjects[layout.ClassName] = candidate.Address;
        }

        if (layout != _ratingLayout)
        {
            _ratingLayout = layout;
            Diag.Info($"Battlegrounds rating read from {layout.ClassName}.{layout.SoloField}"
                      + (layout.DuosField is null ? string.Empty : $" and {layout.DuosField}"));
        }

        return new BattlegroundsRating(soloValue, duosField is null ? 0 : duosValue);
    }

    /// <summary>
    /// Index of the one NetCache object whose class looks like a Battlegrounds rating cache, or null when there is none
    /// or more than one (two objects, even of the same class, are not told apart by guessing).
    /// </summary>
    internal static int? SingleRatingCache(IReadOnlyList<string?> classNames)
    {
        int? found = null;
        for (var i = 0; i < classNames.Count; i++)
        {
            if (classNames[i] is { } name && LooksLikeRatingCache(name))
            {
                if (found is not null)
                {
                    return null;
                }

                found = i;
            }
        }

        return found;
    }

    /// <summary>Only the Battlegrounds cache: the constructed ladder keeps its own, differently shaped rating caches.</summary>
    private static bool LooksLikeRatingCache(string className) =>
        className.Contains("Rating", StringComparison.OrdinalIgnoreCase)
        && (className.Contains("Bacon", StringComparison.OrdinalIgnoreCase)
            || className.Contains("Battlegrounds", StringComparison.OrdinalIgnoreCase));

    /// <summary>Battlegrounds ratings run from a few hundred to well over 10,000; 0 means "not reported".</summary>
    private static bool IsPlausibleRating(int value) => value is > 0 and < 100_000;

    /// <summary>
    /// The NetCache map holds every object the server sent. Walking it costs thousands of small reads and the map
    /// grows during a session, which is slow enough on a weak PC to stall the log reader. The address of the
    /// object we need is remembered and only looked up again every few minutes.
    /// </summary>
    private MonoObject CachedNetCacheObject(MonoRuntime rt, string className)
    {
        lock (_netCacheObjects)
        {
            return CachedNetCacheObjectCore(rt, className);
        }
    }

    private MonoObject CachedNetCacheObjectCore(MonoRuntime rt, string className)
    {
        var now = DateTime.UtcNow;
        if (now >= _netCacheRefreshAt)
        {
            _netCacheRefreshAt = now.AddMinutes(3);
            _netCacheObjects.Clear();
        }

        if (_netCacheObjects.TryGetValue(className, out var address) && address != 0)
        {
            var cached = rt.Object(address);
            if (cached.ClassName == className)
            {
                return cached;
            }
        }

        var found = FindNetCacheObject(rt, className);
        if (found.IsNull)
        {
            _netCacheObjects.Remove(className);
            return default;
        }

        _netCacheObjects[className] = found.Address;
        return found;
    }
}
