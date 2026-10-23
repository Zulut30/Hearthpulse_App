using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Hearthpulse.Core.Accounts;
using Hearthpulse.Core.Data;

namespace Hearthpulse.Tests;

/// <summary>
/// The payload sent to hearthpulse.net against the site's contract (HeartPulse <c>docs/specs/player-game-stats.md</c>
/// and the strict check <c>server/modules/playerGames/schema/gameSchema.ts</c>, site commit c0f0ca0). The fixture is the
/// spec's own example; history rows that describe the same games must map to the same names and values. Every
/// generated payload also passes <see cref="SiteSchema"/>, a port of the site's check, and uses only documented fields.
/// </summary>
public sealed class ProfileEventsContractTests
{
    private static readonly DateTime Day = new(2026, 10, 22, 0, 0, 0, DateTimeKind.Utc);

    private static JsonArray Example() =>
        (JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ProfileUpload", "player-game-stats-example.json")))!["events"] as JsonArray)!;

    private static JsonObject ExampleOf(string type) => (JsonObject)Example().Single(e => (string?)e!["type"] == type)!;

    private static GameRecord Game(string key, DateTime start, int seconds, string mode, string result) => new()
    {
        Key = key,
        StartedAt = start,
        EndedAt = start.AddSeconds(seconds),
        Mode = mode,
        Result = result,
        PlayerName = "Secret#1234",
        OpponentName = "Rival#5678",
    };

    private static GameRecord BattlegroundsExample() => Game("20261022150411|Secret#1234|GT_BATTLEGROUNDS", Day.AddHours(15).AddMinutes(4).AddSeconds(11), 1260, "Battlegrounds", "Win") with
    {
        GameType = "GT_BATTLEGROUNDS",
        HeroCardId = "BG20_HERO_242",
        PlayerClass = "NEUTRAL",
        Placement = 2,
        RatingBefore = 6412,
        RatingAfter = 6451,
        Turns = 13,
        TavernTier = 6,
        Races = "BEAST,DEMON,MECHANICAL,NAGA,UNDEAD",
        FinalBoard = """[{"id":"BG25_008","atk":48,"hp":51,"gold":true}]""",
        CombatResults = """[{"turn":1,"result":"Tie","taken":0,"dealt":0},{"turn":2,"result":"Win","taken":0,"dealt":3}]""",
        OfferedHeroes = "BG20_HERO_242,BG22_HERO_000",
    };

    private static GameRecord ConstructedExample() => Game("20261022191000|Secret#1234|GT_RANKED", Day.AddHours(19).AddMinutes(10), 540, "Ranked", "Win") with
    {
        GameType = "GT_RANKED",
        Format = "FT_STANDARD",
        PlayerClass = "DEATHKNIGHT",
        OpponentClass = "MAGE",
        Turns = 9,
        DeckCode = "AAECAfHhBAA=",
        DeckName = "Blood DK",
        WentFirst = false,
        RankStarLevel = 46,
    };

    /// <summary>Paladin Arena run: 3 wins and 1 loss, the fifth game lost at 20:00, then three wins and the third loss (6-3).</summary>
    private static List<GameRecord> ArenaExample()
    {
        string[] results = ["Win", "Win", "Loss", "Win", "Loss", "Win", "Win", "Win", "Loss"];
        var fifth = Day.AddHours(20);
        return results.Select((result, i) =>
        {
            var start = fifth.AddMinutes((i - 4) * 15);
            var seconds = i == 4 ? 720 : 600;
            if (i == results.Length - 1)
            {
                // The run's last record (its end) is the arena_run's occurredAt: 21:30.
                start = Day.AddHours(21).AddMinutes(20);
            }

            return Game($"{start:yyyyMMddHHmmss}|Secret#1234|GT_ARENA", start, seconds, "Arena", result) with
            {
                GameType = "GT_ARENA",
                PlayerClass = "PALADIN",
                OpponentClass = "ROGUE",
                DeckId = 77,
                Turns = 10,
            };
        }).ToList();
    }

    [Fact]
    public void Battlegrounds_row_maps_to_the_spec_example()
    {
        var expected = ExampleOf(ProfileEvents.Battlegrounds);
        var e = ProfileEvents.ForGame(BattlegroundsExample())!;

        Assert.Equal(ProfileEvents.Battlegrounds, e.Type);
        Assert.Equal((string?)expected["occurredAt"], e.OccurredAt);
        Assert.Equal(2, (int?)e.ToJson()["schemaVersion"]);
        // Every name and value of the example, and nothing else apart from the documented optional fields.
        AssertSameFields(expected["payload"]!.AsObject(), e.Payload, notSent: []);
        Assert.Null(SiteSchema.Validate(e.Type, e.Payload));
    }

    [Fact]
    public void Constructed_row_maps_to_the_spec_example()
    {
        var expected = ExampleOf(ProfileEvents.Constructed);
        var e = ProfileEvents.ForGame(ConstructedExample())!;

        Assert.Equal((string?)expected["occurredAt"], e.OccurredAt);
        // The history has no own archetype, no stars within a level, no rank after the game (it is only read in game)
        // and keeps the mulligan for the deck page: those example fields are not sent (docs/profile-upload.md).
        AssertSameFields(expected["payload"]!.AsObject(), e.Payload, notSent: ["archetype", "rankAfter", "playerMulligan", "rankBefore.stars"]);
        Assert.Null(SiteSchema.Validate(e.Type, e.Payload));
    }

    [Fact]
    public void Arena_rows_map_to_the_spec_example_match_and_run()
    {
        var games = ArenaExample();
        var events = ProfileEvents.ForArena(games, new HashSet<string> { games[4].Key });
        var match = events.Single(e => e.Type == ProfileEvents.ArenaMatch);
        var run = events.Single(e => e.Type == ProfileEvents.ArenaRun);

        var expectedMatch = ExampleOf(ProfileEvents.ArenaMatch);
        Assert.Equal((string?)expectedMatch["occurredAt"], match.OccurredAt);
        // Our run id is the first game's start; the example's is made up. Both fit the site's pattern.
        Assert.Matches("^[A-Za-z0-9][A-Za-z0-9._:-]{7,63}$", (string)match.Payload["runId"]!);
        AssertSameFields(expectedMatch["payload"]!.AsObject(), match.Payload, notSent: [], ignore: ["runId"]);

        var expectedRun = ExampleOf(ProfileEvents.ArenaRun);
        Assert.Equal((string?)expectedRun["occurredAt"], run.OccurredAt);
        Assert.Equal((string)match.Payload["runId"]!, (string)run.Payload["runId"]!);
        AssertSameFields(expectedRun["payload"]!.AsObject(), run.Payload, notSent: [], ignore: ["runId"]);

        Assert.Null(SiteSchema.Validate(match.Type, match.Payload));
        Assert.Null(SiteSchema.Validate(run.Type, run.Payload));
    }

    [Fact]
    public void Event_envelope_matches_the_ingestion_contract()
    {
        var e = ProfileEvents.ForGame(ConstructedExample())!;
        var json = e.ToJson();

        Assert.Equal(["eventId", "type", "schemaVersion", "occurredAt", "payload"], json.Select(p => p.Key));
        // The site's UUID check: versions 1-8, RFC 4122 variant.
        Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", e.EventId);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$", e.OccurredAt);
        var body = JsonNode.Parse(ProfileUploader.Body([e]))!;
        Assert.Single(body["events"]!.AsArray());
    }

    [Fact]
    public void No_names_or_battletags_leave_the_machine()
    {
        var games = ArenaExample();
        var events = new List<ProfileEvent> { ProfileEvents.ForGame(BattlegroundsExample())!, ProfileEvents.ForGame(ConstructedExample())! };
        events.AddRange(ProfileEvents.ForArena(games));
        var body = ProfileUploader.Body(events);

        Assert.DoesNotContain("Secret", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Rival", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("#1234", body, StringComparison.Ordinal);
        // Not even through the id: the same game under another BattleTag has the same event id.
        var renamed = BattlegroundsExample() with { Key = "20261022150411|Other#9999|GT_BATTLEGROUNDS", PlayerName = "Other#9999" };
        Assert.Equal(ProfileEvents.ForGame(BattlegroundsExample())!.EventId, ProfileEvents.ForGame(renamed)!.EventId);
        Assert.NotEqual(ProfileEvents.ForGame(BattlegroundsExample())!.EventId, ProfileEvents.ForGame(ConstructedExample())!.EventId);
    }

    [Fact]
    public void Games_the_site_does_not_keep_are_not_sent()
    {
        var ranked = ConstructedExample();
        Assert.Null(ProfileEvents.ForGame(ranked with { Result = GameRecord.AbandonedResult }));
        Assert.Null(ProfileEvents.ForGame(ranked with { Result = "Unknown" }));
        Assert.Null(ProfileEvents.ForGame(ranked with { Format = "FT_TWIST" }));
        Assert.Null(ProfileEvents.ForGame(ranked with { Mode = "Brawl" }));
        Assert.Null(ProfileEvents.ForGame(ranked with { OpponentClass = "" }));
        Assert.Null(ProfileEvents.ForGame(BattlegroundsExample() with { Placement = 0 }));
        Assert.Null(ProfileEvents.ForGame(BattlegroundsExample() with { Mode = "Duos", Placement = 5 }));
        Assert.Empty(ProfileEvents.ForArena([ArenaExample()[0] with { PlayerClass = "NEUTRAL" }]));
    }

    [Fact]
    public void Out_of_range_values_are_fitted_to_the_contract()
    {
        var bg = BattlegroundsExample() with
        {
            Mode = "Duos",
            Placement = 3,
            HeroCardId = "BG20_HERO_242_SKIN_A",
            EndedAt = BattlegroundsExample().StartedAt.AddHours(9),
            Turns = 250,
            TavernTier = 0,
            RatingAfter = 40_000,
            Races = "BEAST,ABERRATION,beast,MURLOC",
            AnomalyCardId = "BG27_Anomaly_100",
            FinalBoard = """[{"id":"A1","atk":1,"hp":1},{"id":"A2","atk":-5,"hp":200000},{"id":"bad id"},{"id":"A3"},{"id":"A4"},{"id":"A5"},{"id":"A6"},{"id":"A7"},{"id":"A8"}]""",
            CombatResults = """[{"turn":0,"result":"Win"},{"turn":3,"result":"Loss","opponent":"TB_BaconShop_HERO_08","taken":500,"dealt":2},{"turn":4,"result":"Unknown"}]""",
            OfferedHeroes = "H1,H2,H3,H4,H5,H6",
        };
        var e = ProfileEvents.ForGame(bg, id => id.Replace("_SKIN_A", string.Empty))!;
        var p = e.Payload;

        Assert.Null(SiteSchema.Validate(e.Type, p));
        Assert.True((bool)p["duos"]!);
        Assert.Equal("BG20_HERO_242", (string)p["heroCardId"]!);
        Assert.Equal(14_400, (int)p["durationSeconds"]!);
        Assert.False(p.ContainsKey("turns"));
        Assert.False(p.ContainsKey("tavernTier"));
        Assert.Null(p["ratingAfter"]);
        Assert.Equal(["BEAST", "MURLOC"], p["races"]!.AsArray().Select(r => (string)r!));
        Assert.Equal("BG27_Anomaly_100", (string)p["anomalyCardId"]!);
        var minions = p["finalBoard"]!["minions"]!.AsArray();
        Assert.Equal(7, minions.Count);
        Assert.Equal((0, 100_000, false), ((int)minions[1]!["attack"]!, (int)minions[1]!["health"]!, (bool)minions[1]!["golden"]!));
        var combat = Assert.Single(p["combats"]!.AsArray())!;
        Assert.Equal((3, "loss", 200, 2, "TB_BaconShop_HERO_08"),
            ((int)combat["turn"]!, (string)combat["result"]!, (int)combat["damageTaken"]!, (int)combat["damageDealt"]!, (string)combat["opponentHeroCardId"]!));
        // Rerolls made six offers: the last three plus the pick.
        Assert.Equal(["BG20_HERO_242", "H4", "H5", "H6"], p["heroOptions"]!.AsArray().Select(h => (string)h!).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Legend_rank_and_free_text_fit_the_contract()
    {
        var game = ConstructedExample() with
        {
            Mode = "Ranked",
            Format = "FT_WILD",
            RankStarLevel = 53,
            RankLegend = 1204,
            DeckName = "  My\tdeck " + new string('x', 100),
            DeckCode = "not a deck code!",
            OpponentArchetype = "Big Spell Mage",
            HeroCardId = "HERO_11",
            OpponentHeroCardId = "HERO_08",
            WentFirst = true,
        };
        var e = ProfileEvents.ForGame(game)!;
        var p = e.Payload;

        Assert.Null(SiteSchema.Validate(e.Type, p));
        Assert.Equal("wild", (string)p["format"]!);
        Assert.Equal((51, 1204), ((int)p["rankBefore"]!["starLevel"]!, (int)p["rankBefore"]!["legendRank"]!));
        Assert.Equal(80, ((string)p["deckName"]!).Length);
        Assert.StartsWith("Mydeck", (string)p["deckName"]!);
        Assert.False(p.ContainsKey("deckCode"));
        Assert.Equal("Big Spell Mage", (string)p["opponentArchetype"]!);

        var casual = ProfileEvents.ForGame(game with { Mode = "Casual" })!.Payload;
        Assert.False((bool)casual["ranked"]!);
        Assert.False(casual.ContainsKey("rankBefore"));
    }

    [Fact]
    public void Arena_run_score_is_a_new_record_per_game_and_counts_the_run()
    {
        var games = ArenaExample();
        var first = ProfileEvents.ForArena(games.Take(4), new HashSet<string> { games[3].Key });
        var later = ProfileEvents.ForArena(games, new HashSet<string> { games[8].Key });

        var firstRun = first.Single(e => e.Type == ProfileEvents.ArenaRun);
        var laterRun = later.Single(e => e.Type == ProfileEvents.ArenaRun);
        Assert.NotEqual(firstRun.EventId, laterRun.EventId);
        Assert.Equal((3, 1, false), ((int)firstRun.Payload["wins"]!, (int)firstRun.Payload["losses"]!, (bool)firstRun.Payload["complete"]!));
        var match = later.Single(e => e.Type == ProfileEvents.ArenaMatch);
        Assert.Equal((9, 6, 2), ((int)match.Payload["gameNumber"]!, (int)match.Payload["winsBefore"]!, (int)match.Payload["lossesBefore"]!));

        // All games: one match per game, one run record.
        var all = ProfileEvents.ForArena(games);
        Assert.Equal(9, all.Count(e => e.Type == ProfileEvents.ArenaMatch));
        Assert.Single(all, e => e.Type == ProfileEvents.ArenaRun);
        Assert.All(all, e => Assert.Null(SiteSchema.Validate(e.Type, e.Payload)));
    }

    /// <summary>
    /// The example's names and values are ours, except <paramref name="notSent"/> (dotted paths we knowingly leave out);
    /// fields we send beyond the example must be documented ones (<see cref="SiteSchema.Fields"/>).
    /// </summary>
    private static void AssertSameFields(JsonObject expected, JsonObject actual, string[] notSent, string[]? ignore = null)
    {
        Compare(expected, actual, string.Empty, notSent, ignore ?? []);
    }

    private static void Compare(JsonObject expected, JsonObject actual, string prefix, string[] notSent, string[] ignore)
    {
        foreach (var (name, value) in expected)
        {
            var path = prefix + name;
            if (ignore.Contains(path))
            {
                continue;
            }

            if (notSent.Contains(path))
            {
                Assert.False(actual.ContainsKey(name), $"{path} is listed as not sent but is sent");
                continue;
            }

            Assert.True(actual.ContainsKey(name), $"{path} is in the spec example but not sent");
            if (value is JsonObject inner && actual[name] is JsonObject actualInner)
            {
                Compare(inner, actualInner, path + ".", notSent, ignore);
            }
            else
            {
                Assert.True(JsonNode.DeepEquals(value, actual[name]), $"{path}: expected {value?.ToJsonString()}, sent {actual[name]?.ToJsonString()}");
            }
        }
    }

    /// <summary>
    /// A port of the site's strict v2 check (<c>gameSchema.ts</c>, <c>fields.ts</c>) plus the documented field names:
    /// returns the first failing field like the site's <c>INVALID_EVENT</c> answer, or "unknown:name" for a field the spec
    /// does not list.
    /// </summary>
    internal static class SiteSchema
    {
        public static readonly Dictionary<string, string[]> Fields = new()
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
        private static readonly Regex CardId = new("^[A-Za-z0-9_]{2,64}$");
        private static readonly Regex ClientId = new("^[A-Za-z0-9][A-Za-z0-9._:-]{7,63}$");
        private static readonly Regex DeckCode = new("^[A-Za-z0-9+/=_-]{4,256}$");

        private sealed class Invalid(string field) : Exception(field)
        {
            public string Field { get; } = field;
        }

        public static string? Validate(string type, JsonObject p)
        {
            if (p.Select(x => x.Key).FirstOrDefault(k => !Fields[type].Contains(k)) is { } unknown)
            {
                return "unknown:" + unknown;
            }

            try
            {
                if (type == ProfileEvents.ArenaRun)
                {
                    Pattern(p["runId"], "runId", ClientId);
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
                    if (p["deckCode"] is not null) Pattern(p["deckCode"], "deckCode", DeckCode);
                    OptText(p["deckName"], "deckName");
                    OptText(p["archetype"], "archetype");
                    OptText(p["opponentArchetype"], "opponentArchetype");
                    OptBool(p["wentFirst"], "wentFirst");
                    Rank(p["rankBefore"], "rankBefore");
                    Rank(p["rankAfter"], "rankAfter");
                }
                else
                {
                    Pattern(p["runId"], "runId", ClientId);
                    OneOf(p["playerClass"], "playerClass", Classes);
                    OneOf(p["opponentClass"], "opponentClass", Classes);
                    Int(p["gameNumber"], "gameNumber", 1, 20);
                    OptInt(p["winsBefore"], "winsBefore", 0, 12);
                    OptInt(p["lossesBefore"], "lossesBefore", 0, 3);
                    OptBool(p["underground"], "underground");
                    OptBool(p["wentFirst"], "wentFirst");
                    if (p["deckCode"] is not null) Pattern(p["deckCode"], "deckCode", DeckCode);
                }

                return null;
            }
            catch (Invalid ex)
            {
                return ex.Field;
            }
        }

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
            value is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number && v.TryGetValue<int>(out var n) && n >= min && n <= max
                ? n
                : throw new Invalid(field);

        private static int? OptInt(JsonNode? value, string field, int min, int max) => value is null ? null : Int(value, field, min, max);

        private static bool? OptBool(JsonNode? value, string field) =>
            value is null ? null
            : value.GetValueKind() is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False ? (bool)value
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

        private static void Card(JsonNode? value, string field) => Pattern(value, field, CardId);

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

            if (value is not JsonValue v || !v.TryGetValue<string>(out var s) || s.Length > 80 || s.Any(c => c < 32 || c == 127))
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
}
