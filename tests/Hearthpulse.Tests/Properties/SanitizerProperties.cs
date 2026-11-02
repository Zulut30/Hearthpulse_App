using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using CsCheck;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Diagnostics;
using Hearthpulse.Core.Replay;

namespace Hearthpulse.Tests.Properties;

/// <summary>
/// Property tests of the sanitizers (Etalon T4, CsCheck): whatever a replay or an error message carries, the copy that
/// leaves the machine (import, Share, upload to the site, crash report) never holds a name, a BattleTag, a
/// GameAccountId, a user path, or a card in a hidden zone of the opponent.
/// </summary>
public sealed class SanitizerProperties
{
    /// <summary>What a BattleTag's name is made of (CsCheck takes the characters of the string as they are).</summary>
    private const string Letters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZабвгдеёжзийклмнопрстуфхцчшщъыьэюяАБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯ0123456789";

    /// <summary>
    /// A BattleTag's name: Latin or Cyrillic letters and digits, 5–14 long. It starts with "Zx" so that it can never be a
    /// word the replay holds anyway ("Win", a card id), and "Zx" itself never appears in a clean copy.
    /// </summary>
    private static readonly Gen<string> Name = Gen.Char[Letters].Array[3, 12].Select(rest => "Zx" + new string(rest));

    /// <summary>
    /// What else a name in a file may be made of: spaces, quotes, apostrophes, '#', an emoji, a combining accent, Hebrew
    /// and Arabic letters and a right-to-left mark. Whole strings, so that no surrogate pair is split.
    /// </summary>
    private static readonly string[] Pieces = [.. Letters.Select(c => c.ToString()), " ", "'", "\"", "#", "😀", "e\u0301", "ש", "م", "\u200F"];

    /// <summary>Any name a foreign file may carry: "Zx" and 3–12 pieces.</summary>
    private static readonly Gen<string> AnyName =
        Gen.Int[0, Pieces.Length - 1].Array[3, 12].Select(rest => "Zx" + string.Concat(rest.Select(i => Pieces[i])));

    /// <summary>A user folder's name: a BattleTag name, sometimes with an apostrophe inside ("O'Brien").</summary>
    private static readonly Gen<string> PathName = Gen.Select(Name, Gen.Bool).Select((name, apostrophe) => apostrophe ? name[..3] + "'" + name[3..] : name);

    private static readonly Gen<string> BattleTag = Gen.Select(Name, Gen.Int[1000, 99999]).Select((name, n) => $"{name}#{n}");

    /// <summary>JSON as a writer that does not escape letters writes it: the name stays readable in the file.</summary>
    private static readonly JsonSerializerOptions Readable = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly string[] HiddenZones = ["HAND", "SECRET"];

    /// <summary>What a name may come wrapped in when it reaches a replay; a header carrier leaves a replay the filter keeps.</summary>
    private sealed record Carrier(Func<string, string, string> Apply, bool Header);

    private static readonly Carrier[] CarrierList =
    [
        // an extra header field, as an old or foreign writer might add
        new((text, name) => WithHeader(text, header => header["playerName"] = name), true),
        // the ranks the header holds before the filter drops them
        new((text, name) => WithHeader(text, header => header["localRank"] = $"Legend 1 {name}"), true),
        // an unknown summary field
        new((text, name) => WithHeader(text, header => header["summary"]!["opponentName"] = name), true),
        // a card id field holding a name: a damaged file, refused or cleaned
        new((text, name) => WithHeader(text, header => header["summary"]!["heroCardId"] = name), false),
        // an event line with the name, or with the game account id the log prints
        new((text, name) => text.Replace("B\n", $"B\nE 9 O PLAY 2 {name} MINION\n", StringComparison.Ordinal), false),
        new((text, name) => text.Replace("B\n", $"B\nGameAccountId=[hi=1 lo={name}]\n", StringComparison.Ordinal), false),
    ];

    private static readonly Gen<Carrier> Carriers = Gen.Int[0, CarrierList.Length - 1].Select(i => CarrierList[i]);

    /// <summary>Changes the header line (the second) of a replay, written back without escaping letters.</summary>
    internal static string WithHeader(string text, Action<JsonObject> change)
    {
        var lines = text.Split('\n');
        var header = JsonNode.Parse(lines[1])!.AsObject();
        change(header);
        lines[1] = header.ToJsonString(Readable);
        return string.Join('\n', lines);
    }

    /// <summary>How <paramref name="name"/> reads inside a JSON string of the file.</summary>
    internal static string InJson(string name) => JsonSerializer.Serialize(name, Readable)[1..^1];

    /// <summary>Whether <paramref name="copy"/> holds the name in either form, or any piece of it ("Zx" starts every name).</summary>
    internal static bool Leaks(string copy, string name) =>
        copy.Contains("Zx", StringComparison.Ordinal) || copy.Contains(name, StringComparison.Ordinal) || copy.Contains(InJson(name), StringComparison.Ordinal);

    /// <summary>The name is in <paramref name="text"/> as the file holds it (raw in an event line, JSON in the header).</summary>
    internal static bool Holds(string text, string name) =>
        text.Contains(name, StringComparison.Ordinal) || text.Contains(InJson(name), StringComparison.Ordinal);

    /// <summary>The copies that leave the machine: the import filter, the Share copy (date only) and the upload.</summary>
    private static IEnumerable<string?> Copies(string text, CardDb cards)
    {
        yield return ReplayImport.Sanitize(text, cards, out _);
        yield return ReplayImport.Sanitize(text, cards, out _, dateOnly: true);
        yield return ReplayUpload.Prepare(text, cards);
    }

    [Fact]
    public void A_replay_copy_never_carries_a_name_a_battletag_or_a_game_account() =>
        Gen.Select(Gen.OneOf(AnyName, BattleTag), Carriers).Sample((name, carrier) =>
        {
            var text = carrier.Apply(ReplayImportTests.Constructed(), name);
            Assert.True(Holds(text, name), $"«{name}» is not in the input");
            foreach (var copy in Copies(text, CardDb.Empty))
            {
                if (carrier.Header)
                {
                    Assert.NotNull(copy);
                }

                Assert.True(copy is null || (!Leaks(copy, name) && !copy.Contains("GameAccountId", StringComparison.Ordinal)), $"«{name}» left in: {copy}");
            }
        }, iter: 400);

    /// <summary>Cards of a real HearthstoneJSON database (enUS): every collectible quest, and Secrets and other cards.</summary>
    private static readonly Lazy<(CardDb Cards, string[] Quests, string[] Others)> RealCards = new(() =>
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Replay", "hidden-zones.enUS.json");
        var json = File.ReadAllText(path);
        var entries = JsonNode.Parse(json)!.AsArray().Select(e => e!.AsObject()).ToList();
        bool IsQuest(JsonObject e) => e["mechanics"]?.AsArray().Any(m => (string?)m is "QUEST" or "QUESTLINE") == true;
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        return (CardDb.Parse("enUS", stream), [.. entries.Where(IsQuest).Select(e => (string)e["id"]!)], [.. entries.Where(e => !IsQuest(e)).Select(e => (string)e["id"]!)]);
    });

    [Fact]
    public void A_card_in_the_opponent_s_hand_or_secrets_leaves_only_as_a_card_back() =>
        Gen.Select(Gen.Int[0, HiddenZones.Length - 1], Gen.Int[1, 9], Gen.Int[20, 99], Gen.OneOf("EX1_116", "CS2_032", "EX1_130", "BG_TEST_1")).Sample((zone, position, entity, card) =>
        {
            var text = WithHidden(HiddenZones[zone], position, entity, card);
            foreach (var copy in Copies(text, CardDb.Empty))
            {
                AssertCardBack(copy, HiddenZones[zone], position, entity, card);
            }
        }, iter: 200);

    [Fact]
    public void With_the_real_card_database_a_quest_stays_open_and_everything_else_is_hidden() =>
        Gen.Select(Gen.Int[0, HiddenZones.Length - 1], Gen.Int[1, 9], Gen.Int[20, 99], Gen.Bool, Gen.Int[0, 1000]).Sample((zone, position, entity, quest, pick) =>
        {
            var (cards, quests, others) = RealCards.Value;
            var card = quest ? quests[pick % quests.Length] : others[pick % others.Length];
            var text = WithHidden(HiddenZones[zone], position, entity, card);
            foreach (var copy in Copies(text, cards))
            {
                if (quest && HiddenZones[zone] == "SECRET")
                {
                    // a quest is shown to both players: it stays as it is
                    Assert.NotNull(copy);
                    Assert.Contains($"E {entity} O SECRET {position} {card} MINION ATK=3", copy!.Split('\n'));
                }
                else
                {
                    AssertCardBack(copy, HiddenZones[zone], position, entity, card);
                }
            }
        }, iter: 200);

    private static string WithHidden(string zone, int position, int entity, string card) =>
        ReplayImportTests.Constructed([.. ReplayImportTests.ConstructedEvents.Take(4), $"E {entity} O {zone} {position} {card} MINION ATK=3", $"V {entity} COST 5", .. ReplayImportTests.ConstructedEvents.Skip(4)]);

    private static void AssertCardBack(string? copy, string zone, int position, int entity, string card)
    {
        Assert.NotNull(copy);
        var events = copy!.Split('\n');
        Assert.Contains($"E {entity} O {zone} {position} - -", events);
        Assert.DoesNotContain(events, e => e.StartsWith($"E {entity} ", StringComparison.Ordinal) && e.Contains(card, StringComparison.Ordinal));
        Assert.DoesNotContain($"V {entity} COST 5", events);
    }

    [Fact]
    public void A_crash_report_never_carries_a_name_a_battletag_a_mail_or_a_user_path() =>
        Gen.Select(PathName, BattleTag, Gen.Int[0, 4]).Sample((name, tag, shape) =>
        {
            var message = shape switch
            {
                0 => $"The given key '{name}' was not present in the dictionary.",
                1 => $"Player {tag} left; Entity={name} tag=ZONE",
                2 => $"Could not open D:\\{name} {name}\\Hearthstone\\Logs\\Power.log",
                3 => $"Bad file C:\\Users\\{name}\\OneDrive - {name}\\x.hpr in use",
                _ => $"Mail {name.ToLowerInvariant()}@example.com from /home/{name} files/x",
            };
            var scrubbed = CrashReport.Scrub(message, 300);
            Assert.DoesNotContain(name, scrubbed, StringComparison.Ordinal);
            Assert.DoesNotContain(tag, scrubbed, StringComparison.Ordinal);
            Assert.DoesNotContain("@example.com", scrubbed, StringComparison.Ordinal);
        }, iter: 500);
}
