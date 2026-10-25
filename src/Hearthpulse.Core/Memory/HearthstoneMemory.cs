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
public sealed partial class HearthstoneMemory : IDisposable
{
    private readonly object _gate = new();
    private ProcessMemory? _memory;
    private MonoRuntime? _runtime;
    private DateTime _nextAttachAttempt = DateTime.MinValue;
    private bool _attaching;
    private int _attachFailures;

    // One scan reads a few thousand values; attaching walks the whole class table once.
    private const int ScanReadBudget = 50_000;
    private static readonly TimeSpan ScanTimeBudget = TimeSpan.FromSeconds(1.5);
    private const int AttachReadBudget = 3_000_000;
    private static readonly TimeSpan AttachTimeBudget = TimeSpan.FromSeconds(30);
    private DateTime _nextBudgetNote = DateTime.MinValue;
    private readonly Dictionary<string, ulong> _netCacheObjects = new(StringComparer.Ordinal);
    private DateTime _netCacheRefreshAt = DateTime.MinValue;

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

    public BattlegroundsRating? GetBattlegroundsRating() => Safe(rt =>
    {
        var info = CachedNetCacheObject(rt, "NetCacheBaconRatingInfo");
        if (info.IsNull)
        {
            return null;
        }

        var solo = info.Int("<Rating>k__BackingField", -1);
        var duos = info.Int("<DuosRating>k__BackingField", -1);
        return solo <= 0 && duos <= 0 ? null : new BattlegroundsRating(solo, duos);
    });

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
        var change = gameEntity["<RatingChangeData>k__BackingField"];
        var value = change.Int("_NewRating", -1);
        return value > 0 ? value : null;
    });

    /// <summary>TAG_RACE values of the tribes in the current Battlegrounds lobby.</summary>
    public IReadOnlyList<int>? GetBattlegroundsRaces() => Safe(rt =>
    {
        var list = GameStateInstance(rt)["m_availableRacesInBattlegroundsExcludingAmalgam"];
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
        var zoneMgr = rt.FindClass("ZoneMgr")?.GetStatic("s_instance") ?? default;
        if (zoneMgr.IsNull)
        {
            return null;
        }

        int? freeze = null, roll = null, upgrade = null, darkGift = null;
        foreach (var zone in zoneMgr["m_zones"].ListItems())
        {
            if (zone.ClassName != "ZoneGameModeButton" || zone.Int("m_Side") != 1)
            {
                continue;
            }

            var slot = zone.Int("m_ButtonSlot");
            if (slot is not (1 or 2 or 3 or 5))
            {
                continue;
            }

            foreach (var card in zone["m_cards"].ListItems())
            {
                var entity = card["m_entity"];
                if (entity.IsNull)
                {
                    continue;
                }

                var cardId = entity.String("m_cardIdInternal");
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

                var cost = entity.Int("m_realTimeCost");
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
        var manager = rt.FindClass("PlayerLeaderboardManager")?.GetStatic("s_instance") ?? default;
        if (manager.IsNull || !manager.Bool("m_isMousedOver"))
        {
            return null;
        }

        var tile = manager["m_currentlyMousedOverTile"];
        if (tile.IsNull)
        {
            return null;
        }

        var hero = tile["m_playerHeroEntity"];
        if (hero.IsNull)
        {
            hero = tile["m_entity"];
        }

        return hero.IsNull ? null : hero.String("m_cardIdInternal");
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
        var playerMap = GameStateInstance(rt)["m_playerMap"];
        if (playerMap.IsNull)
        {
            return null;
        }

        MatchPlayerInfo? local = null, opponent = null;
        foreach (var player in playerMap.MapValues())
        {
            if (player.ClassName != "Player")
            {
                continue;
            }

            var side = player.Int("m_side");
            var info = ReadPlayer(player);
            if (side == 1) local = info;
            else if (side == 2) opponent = info;
        }

        return local is null && opponent is null ? null : (local, opponent);
    });

    private static MatchPlayerInfo ReadPlayer(MonoObject player)
    {
        RankInfo? standard = null, wild = null, classic = null, twist = null;
        var medals = player["m_medalInfo"]["m_currMedalInfo"];
        foreach (var medal in medals.MapValues())
        {
            var format = medal.Int("format");
            var rank = new RankInfo(
                FormatName(format),
                medal.Int("leagueId"),
                medal.Int("starLevel"),
                medal.Int("legendIndex"),
                medal.Int("seasonId"));
            switch (format)
            {
                case 1: wild = rank; break;
                case 2: standard = rank; break;
                case 3: classic = rank; break;
                case 4: twist = rank; break;
            }
        }

        return new MatchPlayerInfo(player.String("m_name"), standard, wild, classic, twist);
    }

    private static string FormatName(int format) => format switch
    {
        1 => "Wild",
        2 => "Standard",
        3 => "Classic",
        4 => "Twist",
        _ => "Unknown",
    };

    private static MonoObject GameStateInstance(MonoRuntime rt) => rt.FindClass("GameState")?.GetStatic("s_instance") ?? default;

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
    }

    public void Dispose()
    {
        lock (_gate)
        {
            DetachLocked();
        }
    }
}
