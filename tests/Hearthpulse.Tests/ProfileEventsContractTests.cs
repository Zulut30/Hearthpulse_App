using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Hearthpulse.Core.Accounts;
using Hearthpulse.Core.Data;

namespace Hearthpulse.Tests;

/// <summary>
/// The payload sent to hearthpulse.net against the site's contract (HeartPulse <c>docs/specs/player-game-stats.md</c>
/// and the strict check <c>server/modules/playerGames/schema/gameSchema.ts</c>, site commit 0ac2bcf). The fixture is the
/// spec's own example; history rows that describe the same games must map to the same names and values. Every
/// generated payload also passes <see cref="ProfileEventSchema"/> (the site's check, ported into Core), and uses only documented fields.
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
        Assert.Null(ProfileEventSchema.Validate(e.Type, e.Payload));
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
        Assert.Null(ProfileEventSchema.Validate(e.Type, e.Payload));
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

        Assert.Null(ProfileEventSchema.Validate(match.Type, match.Payload));
        Assert.Null(ProfileEventSchema.Validate(run.Type, run.Payload));
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

        Assert.Null(ProfileEventSchema.Validate(e.Type, p));
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

        Assert.Null(ProfileEventSchema.Validate(e.Type, p));
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
    public void Text_shaped_like_a_battletag_or_with_bidi_marks_is_not_sent_as_is()
    {
        var tagged = ProfileEvents.ForGame(ConstructedExample() with { DeckName = "Deck from Secret#1234", OpponentArchetype = "Rival#56789 deck" })!;
        Assert.False(tagged.Payload.ContainsKey("deckName"));
        Assert.False(tagged.Payload.ContainsKey("opponentArchetype"));

        var bidi = ProfileEvents.ForGame(ConstructedExample() with { DeckName = "\u202EKD doolB\u202C", OpponentArchetype = "Control #1 Priest" })!;
        Assert.Equal("KD doolB", (string)bidi.Payload["deckName"]!);
        Assert.Equal("Control #1 Priest", (string)bidi.Payload["opponentArchetype"]!);
        Assert.Null(ProfileEventSchema.Validate(bidi.Type, bidi.Payload));
    }

    [Fact]
    public void The_core_schema_checks_the_envelope_limits_like_the_site()
    {
        var e = ProfileEvents.ForGame(ConstructedExample())!;
        var now = new DateTime(2026, 10, 23, 0, 0, 0, DateTimeKind.Utc);

        Assert.Null(ProfileEventSchema.Validate(e, now));
        Assert.Equal("occurredAt", ProfileEventSchema.Validate(e with { OccurredAt = "2026-10-25T00:00:00Z" }, now));
        Assert.Equal("occurredAt", ProfileEventSchema.Validate(e with { OccurredAt = "2013-12-31T23:59:59Z" }, now));
        var big = (System.Text.Json.Nodes.JsonObject)e.Payload.DeepClone();
        big["deckName"] = new string('x', 40_000);
        Assert.Equal("payload", ProfileEventSchema.Validate(e with { Payload = big }, now));
        var unknown = (System.Text.Json.Nodes.JsonObject)e.Payload.DeepClone();
        unknown["playerName"] = "Secret";
        Assert.Equal("unknown:playerName", ProfileEventSchema.Validate(e with { Payload = unknown }, now));
        var tagged = (System.Text.Json.Nodes.JsonObject)e.Payload.DeepClone();
        tagged["deckName"] = "Secret#1234";
        Assert.Equal("deckName", ProfileEventSchema.Validate(e with { Payload = tagged }, now));
    }

    [Fact]
    public void Games_dated_before_hearthstone_are_not_sent()
    {
        var old = new DateTime(2010, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Null(ProfileEvents.ForGame(ConstructedExample() with { StartedAt = old, EndedAt = old.AddMinutes(5) }));
        Assert.Null(ProfileEvents.ForGame(BattlegroundsExample() with { StartedAt = old, EndedAt = old.AddMinutes(5) }));
        var arena = ArenaExample().Select(g => g with { StartedAt = old, EndedAt = old.AddMinutes(5) });
        Assert.Empty(ProfileEvents.ForArena(arena));
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
        Assert.All(all, e => Assert.Null(ProfileEventSchema.Validate(e.Type, e.Payload)));
    }

    /// <summary>
    /// The example's names and values are ours, except <paramref name="notSent"/> (dotted paths we knowingly leave out);
    /// fields we send beyond the example must be documented ones (<see cref="ProfileEventSchema.Fields"/>).
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
}
