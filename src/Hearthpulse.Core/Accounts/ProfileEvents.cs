using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Stats;

namespace Hearthpulse.Core.Accounts;

/// <summary>One event for <c>POST tracker/events/batch</c> (<c>schemaVersion</c> 2), ready to queue and send.</summary>
public sealed record ProfileEvent(string EventId, string Type, string OccurredAt, JsonObject Payload)
{
    public const int SchemaVersion = 2;

    /// <summary>The event as the site reads it: <c>{eventId, type, schemaVersion, occurredAt, payload}</c>.</summary>
    public JsonObject ToJson() => new()
    {
        ["eventId"] = EventId,
        ["type"] = Type,
        ["schemaVersion"] = SchemaVersion,
        ["occurredAt"] = OccurredAt,
        ["payload"] = Payload.DeepClone(),
    };
}

/// <summary>
/// Turns history rows into the site's player statistics events (contract: the site's
/// <c>docs/specs/player-game-stats.md</c>; mapping: docs/profile-upload.md). Only what the player saw of their own game
/// goes out: no BattleTags or names (the player's and the opponent's), no log lines, nothing hidden. Values the site
/// would reject are left out (an optional field) or the game is skipped (a required one), so a sent event is never
/// refused for its content.
/// </summary>
public static partial class ProfileEvents
{
    public const string Constructed = "constructed_match";
    public const string Battlegrounds = "battlegrounds_match";
    public const string ArenaMatch = "arena_match";
    public const string ArenaRun = "arena_run";

    private static readonly HashSet<string> Classes =
        ["DEATHKNIGHT", "DEMONHUNTER", "DRUID", "HUNTER", "MAGE", "PALADIN", "PRIEST", "ROGUE", "SHAMAN", "WARLOCK", "WARRIOR"];

    // The site's list of lobby tribes; ABERRATION and the like are not on it yet and are left out.
    private static readonly HashSet<string> BgRaces =
        ["BEAST", "DEMON", "DRAGON", "ELEMENTAL", "MECHANICAL", "MURLOC", "NAGA", "PIRATE", "QUILBOAR", "UNDEAD"];

    // Fixed namespace of the name-based event ids (RFC 4122 version 5): the same game gives the same id on every
    // install, so a backfill after a reinstall is acknowledged by the site instead of counted twice.
    private static readonly Guid Namespace = new("6f1d2c1e-4b5a-4f0e-9a51-3c7b8d2e9f10");

    [GeneratedRegex("^[A-Za-z0-9_]{2,64}$")]
    private static partial Regex CardIdPattern();

    [GeneratedRegex("^[A-Za-z0-9+/=_-]{4,256}$")]
    private static partial Regex DeckCodePattern();

    // The site refuses free text shaped like a BattleTag ("#" and 3-6 digits): a name must never reach a deck name.
    [GeneratedRegex(@"#\d{3,6}(?!\d)")]
    private static partial Regex BattleTagPattern();

    // Games are dated from the game's launch; the site ignores anything earlier (a broken clock).
    private static readonly DateTime EarliestGame = new(2014, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The event of one finished game, or null when it is not one the site keeps (Brawl, Practice, Classic, Twist...),
    /// was left before its end, or lacks a required field. Arena games need their run: see <see cref="ForArena"/>.
    /// </summary>
    public static ProfileEvent? ForGame(GameRecord game, Func<string, string>? baseHero = null)
    {
        if (game.IsAbandoned || game.StartedAt.ToUniversalTime() < EarliestGame)
        {
            return null;
        }

        return game.Mode switch
        {
            "Battlegrounds" or "Duos" => BattlegroundsEvent(game, baseHero ?? (id => id)),
            "Ranked" or "Casual" => ConstructedEvent(game),
            _ => null,
        };
    }

    /// <summary>
    /// The events of Arena games: one <c>arena_match</c> per game of <paramref name="only"/> (every game when null) and an
    /// <c>arena_run</c> with the score of each run those games belong to. Runs are split as My Stats splits them
    /// (<see cref="ArenaStats.Runs"/>), so pass every Arena game of the history (or at least the recent ones).
    /// </summary>
    public static IReadOnlyList<ProfileEvent> ForArena(IEnumerable<GameRecord> arenaGames, ISet<string>? only = null)
    {
        var events = new List<ProfileEvent>();
        foreach (var run in ArenaStats.Runs(arenaGames))
        {
            var touched = false;
            var runId = RunId(run);
            var underground = run.Games.Any(IsUnderground);
            for (var i = 0; i < run.Games.Count; i++)
            {
                var game = run.Games[i];
                if (only is not null && !only.Contains(game.Key))
                {
                    continue;
                }

                touched = true;
                if (ArenaMatchEvent(game, runId, i, run.Games.Take(i), underground) is { } match)
                {
                    events.Add(match);
                }
            }

            if (touched && ArenaRunEvent(run, runId, underground) is { } score)
            {
                events.Add(score);
            }
        }

        return events;
    }

    /// <summary>A UUID (version 5, RFC 4122) derived from <paramref name="name"/>: the same name, the same id.</summary>
    public static string EventId(string name)
    {
        var space = Namespace.ToByteArray(bigEndian: true);
        var hash = SHA1.HashData([.. space, .. Encoding.UTF8.GetBytes(name)]);
        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true).ToString("D");
    }

    /// <summary>
    /// The id of a game's event. The history key is "start|BattleTag|game type"; the BattleTag is left out of it so not
    /// even a hash of it leaves the machine (one player does not start two games of one type in the same second).
    /// </summary>
    public static string GameEventId(GameRecord game)
    {
        var parts = game.Key.Split('|');
        var name = parts.Length >= 3
            ? $"{parts[0]}|{parts[^1]}"
            : $"{game.StartedAt.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}|{game.GameType}";
        return EventId("game|" + name);
    }

    private static ProfileEvent? BattlegroundsEvent(GameRecord game, Func<string, string> baseHero)
    {
        var duos = game.Mode == "Duos";
        var hero = CardId(BaseHero(game.HeroCardId, baseHero));
        if (hero is null || game.Placement < 1 || game.Placement > (duos ? 4 : 8))
        {
            return null;
        }

        var payload = new JsonObject();
        AddCommon(payload, game);
        payload["heroCardId"] = hero;
        payload["placement"] = game.Placement;
        payload["duos"] = duos;
        payload["ratingBefore"] = Rating(game.RatingBefore);
        payload["ratingAfter"] = Rating(game.RatingAfter);
        if (CardId(game.AnomalyCardId) is { } anomaly)
        {
            payload["anomalyCardId"] = anomaly;
        }

        var races = Split(game.Races).Select(r => r.ToUpperInvariant()).Where(BgRaces.Contains).Distinct().Take(6).ToList();
        if (races.Count > 0)
        {
            payload["races"] = Strings(races);
        }

        if (game.TavernTier is >= 1 and <= 7)
        {
            payload["tavernTier"] = game.TavernTier;
        }

        var minions = FinalBoard(game.FinalBoard);
        if (minions.Count > 0)
        {
            payload["finalBoard"] = new JsonObject { ["minions"] = minions };
        }

        var combats = Combats(game.CombatResults, baseHero);
        if (combats.Count > 0)
        {
            payload["combats"] = combats;
        }

        var options = HeroOptions(game.OfferedHeroes, hero, baseHero);
        if (options.Count > 0)
        {
            payload["heroOptions"] = Strings(options);
        }

        return new ProfileEvent(GameEventId(game), Battlegrounds, Occurred(game.StartedAt), payload);
    }

    private static ProfileEvent? ConstructedEvent(GameRecord game)
    {
        var format = game.Format switch
        {
            "FT_STANDARD" => "standard",
            "FT_WILD" => "wild",
            _ => null,
        };
        if (format is null || Result(game.Result) is not { } result || !Classes.Contains(game.PlayerClass) || !Classes.Contains(game.OpponentClass))
        {
            return null;
        }

        var ranked = game.Mode == "Ranked";
        var payload = new JsonObject { ["format"] = format, ["ranked"] = ranked, ["result"] = result };
        AddCommon(payload, game);

        payload["playerClass"] = game.PlayerClass;
        payload["opponentClass"] = game.OpponentClass;
        if (CardId(game.HeroCardId) is { } hero)
        {
            payload["heroCardId"] = hero;
        }

        if (CardId(game.OpponentHeroCardId) is { } opponentHero)
        {
            payload["opponentHeroCardId"] = opponentHero;
        }

        if (DeckCode(game.DeckCode) is { } code)
        {
            payload["deckCode"] = code;
        }

        if (Text(game.DeckName) is { } deckName)
        {
            payload["deckName"] = deckName;
        }

        if (Text(game.OpponentArchetype) is { } opponentArchetype)
        {
            payload["opponentArchetype"] = opponentArchetype;
        }

        payload["wentFirst"] = game.WentFirst;
        if (ranked && Rank(game) is { } rank)
        {
            payload["rankBefore"] = rank;
        }

        return new ProfileEvent(GameEventId(game), Constructed, Occurred(game.StartedAt), payload);
    }

    private static ProfileEvent? ArenaMatchEvent(GameRecord game, string runId, int index, IEnumerable<GameRecord> before, bool underground)
    {
        if (game.IsAbandoned || game.StartedAt.ToUniversalTime() < EarliestGame || Result(game.Result) is not { } result || !Classes.Contains(game.PlayerClass)
            || !Classes.Contains(game.OpponentClass) || index >= 20)
        {
            return null;
        }

        var earlier = before.ToList();
        var payload = new JsonObject { ["result"] = result };
        AddCommon(payload, game);

        payload["runId"] = runId;
        payload["playerClass"] = game.PlayerClass;
        payload["opponentClass"] = game.OpponentClass;
        payload["gameNumber"] = index + 1;
        payload["winsBefore"] = Math.Min(earlier.Count(g => g.Result == "Win"), ArenaStats.MaxWins);
        // The site counts at most 3 losses; Underground Arena's further ones are capped (docs/profile-upload.md).
        payload["lossesBefore"] = Math.Min(earlier.Count(g => g.Result == "Loss"), 3);
        payload["underground"] = underground;
        payload["wentFirst"] = game.WentFirst;
        if (DeckCode(game.DeckCode) is { } code)
        {
            payload["deckCode"] = code;
        }

        return new ProfileEvent(GameEventId(game), ArenaMatch, Occurred(game.StartedAt), payload);
    }

    private static ProfileEvent? ArenaRunEvent(ArenaRun run, string runId, bool underground)
    {
        if (!Classes.Contains(run.PlayerClass) || run.Ended.ToUniversalTime() < EarliestGame)
        {
            return null;
        }

        var payload = new JsonObject
        {
            ["runId"] = runId,
            ["playerClass"] = run.PlayerClass,
            ["wins"] = Math.Min(run.Wins, ArenaStats.MaxWins),
            ["losses"] = Math.Min(run.Losses, 3),
            ["complete"] = run.Finished,
            ["underground"] = underground,
        };

        // A new score is a new record (the site keeps the newest one per run): the id names the number of games.
        var id = EventId($"arena_run|{runId}|{run.Games.Count.ToString(CultureInfo.InvariantCulture)}");
        return new ProfileEvent(id, ArenaRun, Occurred(run.Ended), payload);
    }

    /// <summary>
    /// The run's id: its first game's start in UTC ("arena-20261022T150411Z"). It stays the same while games are added,
    /// whether or not Decks.log named the deck.
    /// </summary>
    private static string RunId(ArenaRun run) =>
        "arena-" + run.Started.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    private static bool IsUnderground(GameRecord game) => game.GameType.Contains("UNDERGROUND", StringComparison.Ordinal);

    /// <summary>The fields every game has: <c>durationSeconds</c> (required, 0-14 400) and <c>turns</c>.</summary>
    private static void AddCommon(JsonObject payload, GameRecord game)
    {
        var seconds = (long)Math.Floor(game.Duration.TotalSeconds);
        payload["durationSeconds"] = (int)Math.Clamp(seconds, 0, 14_400);
        if (game.Turns is >= 0 and <= 200)
        {
            payload["turns"] = game.Turns;
        }
    }

    private static string Occurred(DateTime at) =>
        at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string? Result(string result) => result switch
    {
        "Win" => "win",
        "Loss" => "loss",
        "Tie" => "tie",
        _ => null,
    };

    private static int? Rating(int? rating) => rating is >= 0 and <= 30_000 ? rating : null;

    private static JsonObject? Rank(GameRecord game)
    {
        if (game.RankStarLevel is not > 0)
        {
            return null;
        }

        // The client counts Legend as 51 and beyond; the site wants exactly 51 with the Legend rank beside it.
        var level = Math.Min(game.RankStarLevel.Value, 51);
        var rank = new JsonObject { ["starLevel"] = level };
        if (level == 51 && game.RankLegend is >= 1 and <= 1_000_000)
        {
            rank["legendRank"] = game.RankLegend;
        }

        return rank;
    }

    private static JsonArray FinalBoard(string? json)
    {
        var minions = new JsonArray();
        foreach (var item in Items(json))
        {
            if (minions.Count == 7)
            {
                break;
            }

            if (CardId(Str(item, "id")) is not { } id)
            {
                continue;
            }

            minions.Add(new JsonObject
            {
                ["cardId"] = id,
                ["attack"] = Math.Clamp(Int(item, "atk") ?? 0, 0, 100_000),
                ["health"] = Math.Clamp(Int(item, "hp") ?? 0, 0, 100_000),
                ["golden"] = item.TryGetProperty("gold", out var gold) && gold.ValueKind == JsonValueKind.True,
            });
        }

        return minions;
    }

    private static JsonArray Combats(string? json, Func<string, string> baseHero)
    {
        var combats = new JsonArray();
        foreach (var item in Items(json))
        {
            if (combats.Count == 60)
            {
                break;
            }

            if (Int(item, "turn") is not (>= 1 and <= 60) || Result(Str(item, "result") ?? string.Empty) is not { } result)
            {
                continue;
            }

            var combat = new JsonObject
            {
                ["turn"] = Int(item, "turn"),
                ["result"] = result,
                ["damageDealt"] = Math.Clamp(Int(item, "dealt") ?? 0, 0, 200),
                ["damageTaken"] = Math.Clamp(Int(item, "taken") ?? 0, 0, 200),
            };
            if (CardId(BaseHero(Str(item, "opponent"), baseHero)) is { } opponent)
            {
                combat["opponentHeroCardId"] = opponent;
            }

            combats.Add(combat);
        }

        return combats;
    }

    /// <summary>
    /// The hero offer, at most four ids with the picked hero among them. Rerolls append to the stored list, so a longer
    /// list keeps its last heroes (the final offer) and the pick.
    /// </summary>
    private static List<string> HeroOptions(string? offered, string picked, Func<string, string> baseHero)
    {
        var ids = Split(offered).Select(id => CardId(BaseHero(id, baseHero))).OfType<string>().Distinct().ToList();
        if (ids.Count == 0)
        {
            return ids;
        }

        if (!ids.Contains(picked))
        {
            ids.Add(picked);
        }

        if (ids.Count <= 4)
        {
            return ids;
        }

        var others = ids.Where(id => id != picked).TakeLast(3).ToHashSet();
        return [.. ids.Where(id => id == picked || others.Contains(id))];
    }

    private static string? BaseHero(string? id, Func<string, string> baseHero)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        try
        {
            return baseHero(id) is { Length: > 0 } baseId ? baseId : id;
        }
        catch (Exception)
        {
            return id;
        }
    }

    private static IEnumerable<JsonElement> Items(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).Select(e => e.Clone()).ToList()
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? Str(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? Int(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    private static IEnumerable<string> Split(string? list) =>
        (list ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? CardId(string? id) => id is not null && CardIdPattern().IsMatch(id) ? id : null;

    private static string? DeckCode(string? code) => code is not null && DeckCodePattern().IsMatch(code) ? code : null;

    /// <summary>
    /// Free text for the site: no control or bidi characters, trimmed, at most 80 characters; null when empty or when it
    /// holds something shaped like a BattleTag (the site refuses it, and it may be a name).
    /// </summary>
    private static string? Text(string? text)
    {
        if (text is null || BattleTagPattern().IsMatch(text))
        {
            return null;
        }

        var clean = new string(text.Where(c => c >= 32 && c != 127 && c is not (>= '‪' and <= '‮') and not (>= '⁦' and <= '⁩')).ToArray()).Trim();
        if (clean.Length > 80)
        {
            // Never cut a surrogate pair in half.
            clean = clean[..(char.IsHighSurrogate(clean[79]) ? 79 : 80)].TrimEnd();
        }

        return clean.Length > 0 ? clean : null;
    }

    private static JsonArray Strings(IEnumerable<string> values) => new([.. values.Select(v => (JsonNode)JsonValue.Create(v))]);
}
