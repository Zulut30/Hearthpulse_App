using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Hearthpulse.Core.Accounts;

/// <summary>
/// The site's check of a <c>schemaVersion</c> 2 game or run event, ported from HeartPulse
/// <c>server/modules/playerGames/schema/{gameSchema,fields}.ts</c> and <c>trackerIngestion/model.ts</c> (site commit
/// 0ac2bcf), plus the documented field names. The site stores an event that fails it forever without statistics (the
/// first copy of an id wins), so <see cref="ProfileUploader"/> runs every event through it before queuing and logs
/// instead of sending one that fails. Update it with the site's schema (docs/profile-upload.md).
/// </summary>
public static partial class ProfileEventSchema
{
    /// <summary>The site's size limit of a version 2 game or run payload.</summary>
    public const int MaxPayloadBytes = 32 * 1024;

    /// <summary>The documented payload fields per event type.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Fields = new Dictionary<string, string[]>
    {
        [ProfileEvents.Battlegrounds] = ["durationSeconds", "turns", "result", "heroCardId", "placement", "duos", "ratingBefore", "ratingAfter",
            "anomalyCardId", "races", "tavernTier", "finalBoard", "combats", "heroOptions", "offers"],
        [ProfileEvents.Constructed] = ["durationSeconds", "turns", "result", "format", "ranked", "playerClass", "opponentClass", "heroCardId",
            "opponentHeroCardId", "deckCode", "deckName", "archetype", "opponentArchetype", "wentFirst", "rankBefore", "rankAfter",
            "playerMulligan", "opponentDeck"],
        [ProfileEvents.ArenaMatch] = ["durationSeconds", "turns", "result", "runId", "playerClass", "opponentClass", "gameNumber", "winsBefore",
            "lossesBefore", "underground", "wentFirst", "deckCode", "playerMulligan"],
        [ProfileEvents.ArenaRun] = ["runId", "playerClass", "wins", "losses", "complete", "underground", "finalDeckCardIds"],
    };

    private static readonly string[] Classes = ["DEATHKNIGHT", "DEMONHUNTER", "DRUID", "HUNTER", "MAGE", "PALADIN", "PRIEST", "ROGUE", "SHAMAN", "WARLOCK", "WARRIOR"];
    private static readonly string[] Races = ["BEAST", "DEMON", "DRAGON", "ELEMENTAL", "MECHANICAL", "MURLOC", "NAGA", "PIRATE", "QUILBOAR", "UNDEAD"];
    private static readonly string[] Results = ["win", "loss", "tie"];
    private static readonly DateTime EarliestGame = new(2014, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [GeneratedRegex("^[A-Za-z0-9_]{2,64}$")]
    private static partial Regex CardIdPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:-]{7,63}$")]
    private static partial Regex ClientIdPattern();

    [GeneratedRegex("^[A-Za-z0-9+/=_-]{4,256}$")]
    private static partial Regex DeckCodePattern();

    [GeneratedRegex(@"#\d{3,6}(?!\d)")]
    private static partial Regex BattleTagPattern();

    [GeneratedRegex("[‪-‮⁦-⁩]")]
    private static partial Regex BidiPattern();

    private sealed class Invalid(string field) : Exception(field)
    {
        public string Field { get; } = field;
    }

    /// <summary>
    /// The first field the site would refuse (<c>occurredAt</c>, <c>payload</c> for the size, a dotted payload path, or
    /// "unknown:name" for an undocumented field), or null when the event passes. <paramref name="now"/> is UTC.
    /// </summary>
    public static string? Validate(ProfileEvent e, DateTime now)
    {
        if (!DateTime.TryParse(e.OccurredAt, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at)
            || at < EarliestGame || at > now.AddDays(1))
        {
            return "occurredAt";
        }

        if (Encoding.UTF8.GetByteCount(e.Payload.ToJsonString()) > MaxPayloadBytes)
        {
            return "payload";
        }

        return Validate(e.Type, e.Payload);
    }

    /// <summary>The payload part of <see cref="Validate(ProfileEvent, DateTime)"/>.</summary>
    public static string? Validate(string type, JsonObject p)
    {
        if (!Fields.TryGetValue(type, out var fields))
        {
            return "type";
        }

        if (p.Select(x => x.Key).FirstOrDefault(k => !fields.Contains(k)) is { } unknown)
        {
            return "unknown:" + unknown;
        }

        try
        {
            if (type == ProfileEvents.ArenaRun)
            {
                Pattern(p["runId"], "runId", ClientIdPattern());
                OneOf(p["playerClass"], "playerClass", Classes);
                Int(p["wins"], "wins", 0, 12);
                Int(p["losses"], "losses", 0, 3);
                OptBool(p["complete"], "complete");
                OptBool(p["underground"], "underground");
                return null;
            }

            Int(p["durationSeconds"], "durationSeconds", 0, 14_400);
            OptInt(p["turns"], "turns", 0, 200);
            if (type != ProfileEvents.Battlegrounds)
            {
                OneOf(p["result"], "result", Results);
            }

            if (type == ProfileEvents.Battlegrounds)
            {
                var duos = OptBool(p["duos"], "duos") ?? false;
                Card(p["heroCardId"], "heroCardId");
                Int(p["placement"], "placement", 1, duos ? 4 : 8);
                OptInt(p["ratingBefore"], "ratingBefore", 0, 30_000);
                OptInt(p["ratingAfter"], "ratingAfter", 0, 30_000);
                OptCard(p["anomalyCardId"], "anomalyCardId");
                List(p["races"], "races", 6, (r, f) => OneOf(r, f, Races));
                OptInt(p["tavernTier"], "tavernTier", 1, 7);
                var board = p["finalBoard"] is null ? null : Obj(p["finalBoard"], "finalBoard");
                List(board?["minions"], "finalBoard.minions", 7, (m, f) =>
                {
                    var minion = Obj(m, f);
                    Card(minion["cardId"], f + ".cardId");
                    Int(minion["attack"], f + ".attack", 0, 100_000);
                    Int(minion["health"], f + ".health", 0, 100_000);
                    OptBool(minion["golden"], f + ".golden");
                });
                List(p["combats"], "combats", 60, (c, f) =>
                {
                    var combat = Obj(c, f);
                    Int(combat["turn"], f + ".turn", 1, 60);
                    OneOf(combat["result"], f + ".result", Results);
                    Int(combat["damageDealt"] ?? 0, f + ".damageDealt", 0, 200);
                    Int(combat["damageTaken"] ?? 0, f + ".damageTaken", 0, 200);
                    OptCard(combat["opponentHeroCardId"], f + ".opponentHeroCardId");
                });
                List(p["heroOptions"], "heroOptions", 4, Card);
            }
            else if (type == ProfileEvents.Constructed)
            {
                OneOf(p["format"], "format", ["standard", "wild"]);
                OptBool(p["ranked"], "ranked");
                OneOf(p["playerClass"], "playerClass", Classes);
                OneOf(p["opponentClass"], "opponentClass", Classes);
                OptCard(p["heroCardId"], "heroCardId");
                OptCard(p["opponentHeroCardId"], "opponentHeroCardId");
                OptPattern(p["deckCode"], "deckCode", DeckCodePattern());
                OptText(p["deckName"], "deckName");
                OptText(p["archetype"], "archetype");
                OptText(p["opponentArchetype"], "opponentArchetype");
                OptBool(p["wentFirst"], "wentFirst");
                Rank(p["rankBefore"], "rankBefore");
                Rank(p["rankAfter"], "rankAfter");
            }
            else
            {
                Pattern(p["runId"], "runId", ClientIdPattern());
                OneOf(p["playerClass"], "playerClass", Classes);
                OneOf(p["opponentClass"], "opponentClass", Classes);
                Int(p["gameNumber"], "gameNumber", 1, 20);
                OptInt(p["winsBefore"], "winsBefore", 0, 12);
                OptInt(p["lossesBefore"], "lossesBefore", 0, 3);
                OptBool(p["underground"], "underground");
                OptBool(p["wentFirst"], "wentFirst");
                OptPattern(p["deckCode"], "deckCode", DeckCodePattern());
            }

            return null;
        }
        catch (Invalid ex)
        {
            return ex.Field;
        }
    }

    /// <summary>Free text the site accepts as is: no control or bidi characters, nothing shaped like a BattleTag.</summary>
    public static bool IsCleanText(string text) =>
        !text.Any(c => c < 32 || c == 127) && !BidiPattern().IsMatch(text) && !BattleTagPattern().IsMatch(text);

    private static void Rank(JsonNode? value, string field)
    {
        if (value is null)
        {
            return;
        }

        var rank = Obj(value, field);
        var level = Int(rank["starLevel"], field + ".starLevel", 1, 51);
        var legend = OptInt(rank["legendRank"], field + ".legendRank", 1, 1_000_000);
        if (legend is not null && level != 51)
        {
            throw new Invalid(field + ".legendRank");
        }

        OptInt(rank["stars"], field + ".stars", 0, 3);
    }

    private static JsonObject Obj(JsonNode? value, string field) => value as JsonObject ?? throw new Invalid(field);

    private static int Int(JsonNode? value, string field, int min, int max) =>
        value is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue<int>(out var n) && n >= min && n <= max
            ? n
            : throw new Invalid(field);

    private static int? OptInt(JsonNode? value, string field, int min, int max) => value is null ? null : Int(value, field, min, max);

    private static bool? OptBool(JsonNode? value, string field) =>
        value is null ? null
        : value.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? (bool)value
        : throw new Invalid(field);

    private static void OneOf(JsonNode? value, string field, string[] allowed)
    {
        if (value is not JsonValue v || !v.TryGetValue<string>(out var s) || !allowed.Contains(s))
        {
            throw new Invalid(field);
        }
    }

    private static void Pattern(JsonNode? value, string field, Regex pattern)
    {
        if (value is not JsonValue v || !v.TryGetValue<string>(out var s) || !pattern.IsMatch(s))
        {
            throw new Invalid(field);
        }
    }

    private static void OptPattern(JsonNode? value, string field, Regex pattern)
    {
        if (value is not null)
        {
            Pattern(value, field, pattern);
        }
    }

    private static void Card(JsonNode? value, string field) => Pattern(value, field, CardIdPattern());

    private static void OptCard(JsonNode? value, string field)
    {
        if (value is not null)
        {
            Card(value, field);
        }
    }

    private static void OptText(JsonNode? value, string field)
    {
        if (value is null)
        {
            return;
        }

        if (value is not JsonValue v || !v.TryGetValue<string>(out var s) || s.Length > 80 || !IsCleanText(s))
        {
            throw new Invalid(field);
        }
    }

    private static void List(JsonNode? value, string field, int max, Action<JsonNode?, string> read)
    {
        if (value is null)
        {
            return;
        }

        if (value is not JsonArray array || array.Count > max)
        {
            throw new Invalid(field);
        }

        for (var i = 0; i < array.Count; i++)
        {
            read(array[i], $"{field}.{i}");
        }
    }
}
