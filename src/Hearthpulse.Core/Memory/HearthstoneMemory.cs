using System.Diagnostics;
using System.Globalization;

namespace Hearthpulse.Core.Memory;

public sealed record BattlegroundsRating(int Solo, int Duos);

/// <summary>
/// Live gold costs of the Battlegrounds game-mode buttons. Null when that button is not on screen;
/// the costs include hero-power and trinket discounts.
/// </summary>
public sealed record BattlegroundsButtonCosts(int? Freeze, int? Roll, int? Upgrade, int? DarkGift);

public sealed record RankInfo(string Format, int LeagueId, int StarLevel, int LegendRank, int SeasonId)
{
    private static readonly string[] Leagues = ["Bronze", "Silver", "Gold", "Platinum", "Diamond"];

    public bool IsLegend => LegendRank > 0 || StarLevel >= 51;

    /// <summary>
    /// Rank as stored in the game history, culture-independent: "Diamond 4", "Legend 1,204" (invariant digit grouping).
    /// </summary>
    public string Text => FormatIn(CultureInfo.InvariantCulture);

    /// <summary>
    /// Rank for the UI in the interface language, the legend number grouped for the current culture ("Legend 1 204" on
    /// ru-RU, "Легенда 1 204" in Russian).
    /// </summary>
    public string Display => Translate(FormatIn(CultureInfo.CurrentCulture))!;

    private string FormatIn(IFormatProvider culture)
    {
        if (IsLegend)
        {
            return LegendRank > 0 ? string.Format(culture, "Legend {0:N0}", LegendRank) : "Legend";
        }

        if (StarLevel <= 0)
        {
            return "Unranked";
        }

        var league = Leagues[Math.Clamp((StarLevel - 1) / 10, 0, Leagues.Length - 1)];
        var rank = 10 - ((StarLevel - 1) % 10);
        return $"{league} {rank}";
    }

    /// <summary>
    /// Saved rank text in its stored, culture-independent form. Older builds formatted the legend number with the
    /// user's culture ("Legend 1 204" with a no-break space, "Legend 1.204"); those are rewritten as "Legend 1,204".
    /// </summary>
    public static string? Normalize(string? text)
    {
        if (LegendNumber(text) is not { } legend)
        {
            return text;
        }

        return string.Format(CultureInfo.InvariantCulture, "Legend {0:N0}", legend);
    }

    /// <summary>Saved rank text for the UI: the legend number grouped for <paramref name="culture"/> (current by default).</summary>
    public static string? Localize(string? text, CultureInfo? culture = null) =>
        Translate(LegendNumber(text) is { } legend ? string.Format(culture ?? CultureInfo.CurrentCulture, "Legend {0:N0}", legend) : text);

    /// <summary>
    /// The league word of an English rank text ("Gold 4", "Legend 1 204", "Unranked") in the interface language; the
    /// stored text stays English because <see cref="Stats.ConstructedStats"/> parses it.
    /// </summary>
    private static string? Translate(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var space = text.IndexOf(' ', StringComparison.Ordinal);
        var key = (space < 0 ? text : text[..space]) switch
        {
            "Bronze" => "Core.Rank.Bronze",
            "Silver" => "Core.Rank.Silver",
            "Gold" => "Core.Rank.Gold",
            "Platinum" => "Core.Rank.Platinum",
            "Diamond" => "Core.Rank.Diamond",
            "Legend" => "Core.Rank.Legend",
            "Unranked" => "Core.Rank.Unranked",
            _ => null,
        };
        return key is null ? text : Localization.Loc.T(key) + (space < 0 ? string.Empty : text[space..]);
    }

    /// <summary>The number of a saved "Legend N" text, whatever digit grouping it was written with.</summary>
    private static int? LegendNumber(string? text)
    {
        if (text is null || !text.StartsWith("Legend ", StringComparison.Ordinal))
        {
            return null;
        }

        var digits = new string(text.AsSpan(7).ToArray().Where(char.IsAsciiDigit).ToArray());
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var legend) && legend > 0 ? legend : null;
    }
}

public sealed record MatchPlayerInfo(string? Name, RankInfo? Standard, RankInfo? Wild, RankInfo? Classic, RankInfo? Twist);

/// <summary>
/// Reads Hearthstone client state straight from process memory: Battlegrounds rating, ranked medals,
/// the Battlegrounds tribe pool. All calls are read-only and return null when data is unavailable.
/// </summary>
public sealed class HearthstoneMemory : IDisposable
{
    private readonly object _gate = new();
    private ProcessMemory? _memory;
    private MonoRuntime? _runtime;
    private DateTime _nextAttachAttempt = DateTime.MinValue;
    private bool _attaching;
    private int _attachFailures;

    // MONO_TYPE_I4 / MONO_TYPE_U4: the element types of the int fields ratings are stored in.
    private const byte MonoInt32 = 0x08;
    private const byte MonoUInt32 = 0x09;

    // One scan reads a few thousand values; attaching walks the whole class table once.
    private const int ScanReadBudget = 50_000;
    private static readonly TimeSpan ScanTimeBudget = TimeSpan.FromSeconds(1.5);
    private const int AttachReadBudget = 3_000_000;
    private static readonly TimeSpan AttachTimeBudget = TimeSpan.FromSeconds(30);
    private DateTime _nextBudgetNote = DateTime.MinValue;
    private readonly Dictionary<string, ulong> _netCacheObjects = new(StringComparer.Ordinal);
    private DateTime _netCacheRefreshAt = DateTime.MinValue;

    // What this client build calls each field the reads below need, keyed by class address and role, plus the class
    // addresses themselves. A game update that renames or moves one costs a single lookup, not one per scan.
    private readonly Dictionary<(ulong Class, string Role), string?> _fieldNames = new();
    private readonly Dictionary<string, ulong> _classes = new(StringComparer.Ordinal);

    public bool IsAttached
    {
        get
        {
            lock (_gate)
            {
                return _runtime is not null && _memory is { HasExited: false };
            }
        }
    }

    /// <summary>
    /// Reading the client's memory (rating, ranks, lobby tribes) uses Windows APIs. On other platforms everything
    /// else keeps working from the logs; these values are simply unavailable.
    /// </summary>
    public static bool IsSupported => OperatingSystem.IsWindows();

    public string Status { get; private set; } = IsSupported ? "Waiting for Hearthstone" : "Memory reading is only available on Windows";

    /// <summary>Attaches to a running Hearthstone process; cheap to call repeatedly.</summary>
    public bool EnsureAttached()
    {
        lock (_gate)
        {
            if (_runtime is not null && _memory is { HasExited: false })
            {
                return true;
            }

            DetachLocked();
            if (!IsSupported)
            {
                Status = "Memory reading is only available on Windows";
                return false;
            }

            if (_attaching || DateTime.UtcNow < _nextAttachAttempt)
            {
                return false;
            }

            _attaching = true;
        }

        // Attaching reads the game's whole class table. It runs outside the lock, so IsAttached (asked on the log
        // thread for every snapshot) never waits for it.
        (ProcessMemory? Memory, MonoRuntime? Runtime, string Status, bool Found) result;
        try
        {
            result = Attach();
        }
        catch (Exception ex)
        {
            result = (null, null, "Attach failed: " + ex.Message, true);
        }

        lock (_gate)
        {
            _attaching = false;
            Status = result.Status;
            if (result.Runtime is null)
            {
                result.Memory?.Dispose();
                // A game that is running but cannot be read (an update moved things, a security tool blocks the
                // reads) is tried less and less often instead of every few seconds.
                _attachFailures = result.Found ? _attachFailures + 1 : 0;
                _nextAttachAttempt = DateTime.UtcNow.AddSeconds(Math.Clamp(5 * _attachFailures, 5, 60));
                return false;
            }

            _attachFailures = 0;
            _memory = result.Memory;
            _runtime = result.Runtime;
            return true;
        }
    }

    private static (ProcessMemory? Memory, MonoRuntime? Runtime, string Status, bool Found) Attach()
    {
        var processes = Process.GetProcessesByName("Hearthstone");
        try
        {
            if (processes.Length == 0)
            {
                return (null, null, "Hearthstone not running", false);
            }

            var memory = ProcessMemory.Open(processes[0].Id);
            if (memory is null)
            {
                return (null, null, "Cannot open Hearthstone process (run as the same user)", true);
            }

            try
            {
                using (ProcessMemory.Budget(AttachReadBudget, AttachTimeBudget))
                {
                    var runtime = MonoRuntime.Attach(memory, out var error);
                    if (runtime is null || ProcessMemory.BudgetExhausted)
                    {
                        memory.Dispose();
                        return (null, null, runtime is null ? error : "Attaching took too long, retrying", true);
                    }

                    return (memory, runtime, $"Attached · {runtime.ClassCount:N0} classes", true);
                }
            }
            catch
            {
                // The process handle has no finalizer: a failed attach (retried every few seconds) leaked one each time.
                memory.Dispose();
                throw;
            }
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    public MonoRuntime? Runtime
    {
        get
        {
            lock (_gate)
            {
                return _runtime;
            }
        }
    }

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

    /// <summary>Rating shown on the post-game screen once the server has sent it, else null.</summary>
    public int? GetPostGameBattlegroundsRating() => Safe<int?>(rt =>
    {
        var gameEntity = GameStateInstance(rt)["m_gameEntity"];
        var changeField = Field(gameEntity.Class, "ratingChange", ["<RatingChangeData>k__BackingField"]);
        var change = changeField is null ? default : gameEntity.Object(changeField);
        if (change.IsNull)
        {
            return null;
        }

        var newRatingField = Field(change.Class, "newRating", ["_NewRating"], integer: true);
        var value = newRatingField is null ? -1 : change.Int(newRatingField, -1);
        return IsPlausibleRating(value) ? value : null;
    });

    /// <summary>TAG_RACE values of the tribes in the current Battlegrounds lobby.</summary>
    public IReadOnlyList<int>? GetBattlegroundsRaces() => Safe(rt =>
    {
        var state = GameStateInstance(rt);
        var field = Field(state.Class, "races", ["m_availableRacesInBattlegroundsExcludingAmalgam"]);
        var list = field is null ? default : state.Object(field);
        if (list.IsNull)
        {
            return null;
        }

        var races = list.IntListItems();
        return races.Count is > 0 and < 16 ? races : null;
    });

    /// <summary>
    /// Live gold costs of the Battlegrounds game-mode buttons: freeze (slot 1), reroll (slot 2),
    /// tavern upgrade (slot 3) and Dark Gift (slot 5). Null when the button is not on screen.
    /// </summary>
    public BattlegroundsButtonCosts? GetBattlegroundsButtonCosts() => Safe<BattlegroundsButtonCosts?>(rt =>
    {
        var zoneMgr = Instance(rt, "ZoneMgr", "ZoneMgr");
        var zonesField = Field(zoneMgr.Class, "zones", ["m_zones"]);
        if (zoneMgr.IsNull || zonesField is null)
        {
            return null;
        }

        int? freeze = null, roll = null, upgrade = null, darkGift = null;
        foreach (var zone in zoneMgr.Object(zonesField).ListItems())
        {
            var zoneClass = zone.Class;
            if (zoneClass?.Name != "ZoneGameModeButton")
            {
                continue;
            }

            var sideField = Field(zoneClass, "side", ["m_Side"], integer: true);
            var slotField = Field(zoneClass, "slot", ["m_ButtonSlot"], integer: true);
            var cardsField = Field(zoneClass, "cards", ["m_cards"]);
            if (sideField is null || slotField is null || cardsField is null || zone.Int(sideField) != 1)
            {
                continue;
            }

            var slot = zone.Int(slotField);
            if (slot is not (1 or 2 or 3 or 5))
            {
                continue;
            }

            foreach (var card in zone.Object(cardsField).ListItems())
            {
                var entityField = Field(card.Class, "entity", ["m_entity"]);
                var entity = entityField is null ? default : card.Object(entityField);
                if (entity.IsNull)
                {
                    continue;
                }

                var cardIdField = Field(entity.Class, "cardId", ["m_cardIdInternal"]);
                var cardId = cardIdField is null ? null : entity.String(cardIdField);
                if (cardId is null)
                {
                    continue;
                }

                // Slot 3 is the upgrade only when the card is the tier button; other slots are stable enough
                // to read by position (freeze, reroll, Dark Gift).
                if (slot == 3 && !cardId.StartsWith("TB_BaconShopTechUp", StringComparison.Ordinal))
                {
                    continue;
                }

                var costField = Field(entity.Class, "cost", ["m_realTimeCost"], integer: true);
                var cost = costField is null ? 0 : entity.Int(costField);
                switch (slot)
                {
                    case 1: freeze = cost; break;
                    case 2: roll = cost; break;
                    case 3: upgrade = cost; break;
                    case 5: darkGift = cost; break;
                }

                break;
            }
        }

        return new BattlegroundsButtonCosts(freeze, roll, upgrade, darkGift);
    });

    /// <summary>Gold cost of the live tavern-upgrade button, or null when it is not on screen.</summary>
    public int? GetTavernUpgradeCost() => GetBattlegroundsButtonCosts()?.Upgrade;

    /// <summary>
    /// Card id of the hero whose Battlegrounds leaderboard tile is under the mouse, or null. Read straight from the
    /// game's leaderboard so it works at any resolution or aspect ratio.
    /// </summary>
    public string? GetHoveredLeaderboardHero() => Safe(rt =>
    {
        var manager = Instance(rt, "PlayerLeaderboardManager", "PlayerLeaderboardManager");
        var hovered = Field(manager.Class, "leaderboardHovered", ["m_isMousedOver"]);
        if (manager.IsNull || hovered is null || !manager.Bool(hovered))
        {
            return null;
        }

        var tileField = Field(manager.Class, "hoveredTile", ["m_currentlyMousedOverTile"]);
        var tile = tileField is null ? default : manager.Object(tileField);
        if (tile.IsNull)
        {
            return null;
        }

        // A tile whose hero entity is not set yet still carries the hero in its plain entity.
        var heroField = Field(tile.Class, "heroEntity", ["m_playerHeroEntity"]);
        var hero = heroField is null ? default : tile.Object(heroField);
        if (hero.IsNull)
        {
            var entityField = Field(tile.Class, "entity", ["m_entity"]);
            hero = entityField is null ? default : tile.Object(entityField);
        }

        var cardIdField = Field(hero.Class, "cardId", ["m_cardIdInternal"]);
        return hero.IsNull || cardIdField is null ? null : hero.String(cardIdField);
    });

    /// <summary>
    /// Card id of the game card under the mouse (in hand, on the board, hero, hero power, weapon...), or null when the
    /// mouse is over no card or the game is not in a match. Read from the game's own input handling
    /// (<c>InputManager.m_mousedOverCard</c>), so it follows what the client highlights at any resolution. While a card
    /// is dragged this is the card under the cursor (a target), not the dragged one. Hidden cards (opponent's hand) have
    /// no id and give null.
    /// </summary>
    public string? GetHoveredCardId() => Safe(quiet: true, read: rt =>
    {
        var input = rt.FindClass("InputManager")?.GetStatic("s_instance") ?? default;
        var card = input.IsNull ? default : input["m_mousedOverCard"];
        if (card.IsNull)
        {
            return null;
        }

        var id = card["m_entity"].String("m_cardIdInternal");
        return string.IsNullOrEmpty(id) ? null : id;
    });

    /// <summary>Ranked medals for both players of the current game (only valid in-game).</summary>
    public (MatchPlayerInfo? Local, MatchPlayerInfo? Opponent)? GetMatchPlayers() => Safe<(MatchPlayerInfo?, MatchPlayerInfo?)?>(rt =>
    {
        var state = GameStateInstance(rt);
        var mapField = Field(state.Class, "playerMap", ["m_playerMap"]);
        var playerMap = mapField is null ? default : state.Object(mapField);
        if (playerMap.IsNull)
        {
            return null;
        }

        MatchPlayerInfo? local = null, opponent = null;
        foreach (var player in playerMap.MapValues())
        {
            var playerClass = player.Class;
            if (playerClass?.Name != "Player")
            {
                continue;
            }

            var sideField = Field(playerClass, "side", ["m_side"], integer: true);
            var info = ReadPlayer(player);
            switch (sideField is null ? -1 : player.Int(sideField))
            {
                case 1: local = info; break;
                case 2: opponent = info; break;
            }
        }

        return local is null && opponent is null ? null : (local, opponent);
    });

    private MatchPlayerInfo ReadPlayer(MonoObject player)
    {
        RankInfo? standard = null, wild = null, classic = null, twist = null;
        var medalInfoField = Field(player.Class, "medalInfo", ["m_medalInfo"]);
        var medalInfo = medalInfoField is null ? default : player.Object(medalInfoField);
        var medalsField = Field(medalInfo.Class, "currentMedals", ["m_currMedalInfo"]);
        var medals = medalsField is null ? default : medalInfo.Object(medalsField);
        foreach (var medal in medals.MapValues())
        {
            var medalClass = medal.Class;
            var formatField = Field(medalClass, "format", ["format"], integer: true);
            var leagueField = Field(medalClass, "league", ["leagueId"], integer: true);
            var starField = Field(medalClass, "stars", ["starLevel"], integer: true);
            var legendField = Field(medalClass, "legend", ["legendIndex"], integer: true);
            var seasonField = Field(medalClass, "season", ["seasonId"], integer: true);
            var format = formatField is null ? 0 : medal.Int(formatField);
            var rank = new RankInfo(
                FormatName(format),
                leagueField is null ? 0 : medal.Int(leagueField),
                starField is null ? 0 : medal.Int(starField),
                legendField is null ? 0 : medal.Int(legendField),
                seasonField is null ? 0 : medal.Int(seasonField));
            switch (format)
            {
                case 1: wild = rank; break;
                case 2: standard = rank; break;
                case 3: classic = rank; break;
                case 4: twist = rank; break;
            }
        }

        var nameField = Field(player.Class, "name", ["m_name"]);
        return new MatchPlayerInfo(nameField is null ? null : player.String(nameField), standard, wild, classic, twist);
    }

    private static string FormatName(int format) => format switch
    {
        1 => "Wild",
        2 => "Standard",
        3 => "Classic",
        4 => "Twist",
        _ => "Unknown",
    };

    private MonoObject GameStateInstance(MonoRuntime rt) => Instance(rt, "GameState", "GameState");

    /// <summary>
    /// The client's singleton for a class (GameState.s_instance, ZoneMgr.s_instance, …). Both the class and the static
    /// field that holds the instance are looked up by name, so a renamed field or a class that gained a namespace is
    /// found without a code change.
    /// </summary>
    private MonoObject Instance(MonoRuntime rt, string role, params string[] classNames)
    {
        var cls = Class(rt, role, classNames);
        var field = Field(cls, role + ".instance", ["s_instance"], isStatic: true);
        return cls is null || field is null ? default : cls.GetStatic(field);
    }

    /// <summary>
    /// A class by name, else the one class whose full name is that name inside a namespace (a namespace added in a game
    /// update). Two or more such classes are ambiguous and give null rather than a guess.
    /// </summary>
    private MonoClass? Class(MonoRuntime rt, string role, string[] names)
    {
        lock (_classes)
        {
            if (_classes.TryGetValue(role, out var address) && address != 0 && rt.ClassAt(address) is { } cached)
            {
                return cached;
            }
        }

        var found = names.Select(rt.FindClass).FirstOrDefault(c => c is not null);
        if (found is null)
        {
            var namespaced = rt.ClassNames
                .Where(n => names.Any(name => n.EndsWith("." + name, StringComparison.Ordinal)))
                .Take(2)
                .ToList();
            found = namespaced.Count == 1 ? rt.FindClass(namespaced[0]) : null;
        }

        if (found is not null)
        {
            lock (_classes)
            {
                _classes[role] = found.Address;
            }
        }

        return found;
    }

    /// <summary>
    /// The name this client build uses for a field we already read: the known name first, else a field that is the same
    /// name spelled the way a game update tends to respell it ("m_side", "_side", "&lt;Side&gt;k__BackingField", other
    /// case), see <see cref="CanonicalFieldName"/>. Never a different field: no substring or "looks like" matching, so
    /// a rename can only lead back to a value we already read. The answer is remembered per class and dropped when the
    /// game exits, so the lookup runs once per game update instead of once per scan.
    /// </summary>
    private string? Field(MonoClass? cls, string role, string[] known, bool integer = false, bool isStatic = false)
    {
        if (cls is null)
        {
            return null;
        }

        var key = (cls.Address, role);
        lock (_fieldNames)
        {
            if (_fieldNames.TryGetValue(key, out var remembered))
            {
                return remembered;
            }
        }

        var found = known.FirstOrDefault(name => cls.FindField(name) is not null);
        if (found is null)
        {
            var canonical = known.Select(CanonicalFieldName).ToHashSet(StringComparer.Ordinal);
            found = cls.AllFields()
                .FirstOrDefault(f => f.IsStatic == isStatic
                    && (!integer || f.TypeCode is MonoInt32 or MonoUInt32)
                    && canonical.Contains(CanonicalFieldName(f.Name)))?.Name;
        }

        // A field list cut short by the read budget must not hide the field for good.
        if (!ProcessMemory.BudgetExhausted)
        {
            lock (_fieldNames)
            {
                _fieldNames[key] = found;
            }
        }

        return found;
    }

    /// <summary>
    /// A field name without the parts a respelling changes: the auto-property wrapper "&lt;Name&gt;k__BackingField",
    /// an "m_"/"s_" prefix, underscores and case. "m_side", "_side" and "&lt;Side&gt;k__BackingField" all give "side".
    /// </summary>
    private static string CanonicalFieldName(string name)
    {
        const string backingField = ">k__BackingField";
        var core = name.StartsWith('<') && name.EndsWith(backingField, StringComparison.Ordinal)
            ? name[1..^backingField.Length]
            : name;
        if (core.StartsWith("m_", StringComparison.Ordinal) || core.StartsWith("s_", StringComparison.Ordinal))
        {
            core = core[2..];
        }

        return core.Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
    }

    /// <summary>Resolves a service registered in Hearthstone's service locator by type name.</summary>
    public static MonoObject FindService(MonoRuntime rt, string serviceName)
    {
        var jobs = rt.FindClass("Hearthstone.HearthstoneJobs");
        var builders = jobs?.GetStatic("s_dependencyBuilder") ?? default;
        foreach (var builder in builders.ListItems())
        {
            var services = builder["m_serviceLocator"]["m_services"];
            foreach (var entry in services.DictionaryValues())
            {
                if (entry.String("<ServiceTypeName>k__BackingField") == serviceName)
                {
                    return entry["<Service>k__BackingField"];
                }
            }
        }

        return default;
    }

    public static MonoObject FindNetCacheObject(MonoRuntime rt, string className)
    {
        var netCache = FindService(rt, "NetCache")["m_netCache"];
        foreach (var value in netCache.MapValues())
        {
            if (value.ClassName == className)
            {
                return value;
            }
        }

        return default;
    }

    /// <param name="quiet">A failed read does not change <see cref="Status"/> (frequent reads from another thread would make it flicker).</param>
    private T? Safe<T>(Func<MonoRuntime, T?> read, bool quiet = false)
    {
        if (!EnsureAttached())
        {
            return default;
        }

        MonoRuntime? runtime;
        lock (_gate)
        {
            runtime = _runtime;
        }

        if (runtime is null)
        {
            return default;
        }

        try
        {
            using (ProcessMemory.Budget(ScanReadBudget, ScanTimeBudget))
            {
                var result = read(runtime);
                if (!ProcessMemory.BudgetExhausted)
                {
                    return result;
                }
            }

            // The scan was cut short (usually objects freed mid-scan): its half-read values are not used.
            if (DateTime.UtcNow >= _nextBudgetNote)
            {
                _nextBudgetNote = DateTime.UtcNow.AddMinutes(1);
                Diag.Info("A memory scan ran out of its read budget and was skipped");
            }

            return default;
        }
        catch (Exception ex)
        {
            if (!quiet)
            {
                Status = "Read failed: " + ex.Message;
            }

            return default;
        }
    }

    private void DetachLocked()
    {
        _runtime = null;
        _memory?.Dispose();
        _memory = null;
        _netCacheRefreshAt = DateTime.MinValue;
        // A new game process may be a new client build: find the rating layout again instead of reusing the old one.
        _ratingLayout = KnownRatingLayout;
        _nextRatingDiscovery = DateTime.MinValue;
        lock (_fieldNames)
        {
            _fieldNames.Clear();
        }

        lock (_classes)
        {
            _classes.Clear();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            DetachLocked();
        }
    }
}
