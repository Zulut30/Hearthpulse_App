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
    /// <summary>
    /// A player's name: Latin or Cyrillic letters and digits, 5–14 long. It starts with "Zx" so that it can never be a
    /// word the replay holds anyway ("Win", a card id).
    /// </summary>
    private static readonly Gen<string> Name = Gen.Char[Letters].Array[3, 12].Select(rest => "Zx" + new string(rest));

    /// <summary>What a BattleTag's name is made of (CsCheck takes the characters of the string as they are).</summary>
    private const string Letters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZабвгдеёжзийклмнопрстуфхцчшщъыьэюяАБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯ0123456789";

    private static readonly Gen<string> BattleTag = Gen.Select(Name, Gen.Int[1000, 99999]).Select((name, n) => $"{name}#{n}");

    private static readonly string[] HiddenZones = ["HAND", "SECRET"];

    /// <summary>What a name may come wrapped in when it reaches a replay.</summary>
    private static readonly Gen<Func<string, string, string>> Carrier = Gen.OneOf<Func<string, string, string>>(
        // an extra header field, as an old or foreign writer might add
        (text, name) => WithHeader(text, header => header["playerName"] = name),
        // the ranks the header holds before the filter drops them
        (text, name) => WithHeader(text, header => header["localRank"] = $"Legend 1 {name}"),
        // an unknown summary field
        (text, name) => WithHeader(text, header => header["summary"]!["opponentName"] = name),
        // a card id field holding a name
        (text, name) => WithHeader(text, header => header["summary"]!["heroCardId"] = name),
        // an event line with the name, or with the game account id the log prints
        (text, name) => text.Replace("B\n", $"B\nE 9 O PLAY 2 {name} MINION\n", StringComparison.Ordinal),
        (text, name) => text.Replace("B\n", $"B\nGameAccountId=[hi=1 lo={name}]\n", StringComparison.Ordinal));

    private static string WithHeader(string text, Action<JsonObject> change)
    {
        var lines = text.Split('\n');
        var header = JsonNode.Parse(lines[1])!.AsObject();
        change(header);
        lines[1] = header.ToJsonString();
        return string.Join('\n', lines);
    }

    /// <summary>The copies that leave the machine: the import filter, the Share copy (date only) and the upload.</summary>
    private static IEnumerable<string?> Copies(string text)
    {
        yield return ReplayImport.Sanitize(text, CardDb.Empty, out _);
        yield return ReplayImport.Sanitize(text, CardDb.Empty, out _, dateOnly: true);
        yield return ReplayUpload.Prepare(text, CardDb.Empty);
    }

    [Fact]
    public void A_replay_copy_never_carries_a_name_a_battletag_or_a_game_account() =>
        Gen.Select(Gen.OneOf(Name, BattleTag), Carrier).Sample((name, carrier) =>
        {
            var text = carrier(ReplayImportTests.Constructed(), name);
            foreach (var copy in Copies(text))
            {
                Assert.True(copy is null || (!copy.Contains(name, StringComparison.Ordinal) && !copy.Contains("GameAccountId", StringComparison.Ordinal)),
                    $"«{name}» left in: {copy}");
            }
        }, iter: 300);

    [Fact]
    public void A_card_in_the_opponent_s_hand_or_secrets_leaves_only_as_a_card_back() =>
        Gen.Select(Gen.Int[0, HiddenZones.Length - 1], Gen.Int[1, 9], Gen.Int[20, 99], Gen.OneOf("EX1_116", "CS2_032", "EX1_130", "BG_TEST_1")).Sample((zone, position, entity, card) =>
        {
            var line = $"E {entity} O {HiddenZones[zone]} {position} {card} MINION ATK=3";
            var text = ReplayImportTests.Constructed([.. ReplayImportTests.ConstructedEvents.Take(4), line, $"V {entity} COST 5", .. ReplayImportTests.ConstructedEvents.Skip(4)]);
            foreach (var copy in Copies(text))
            {
                Assert.NotNull(copy);
                var events = copy!.Split('\n');
                Assert.Contains($"E {entity} O {HiddenZones[zone]} {position} - -", events);
                Assert.DoesNotContain(events, e => e.StartsWith($"E {entity} ", StringComparison.Ordinal) && e.Contains(card, StringComparison.Ordinal));
                Assert.DoesNotContain($"V {entity} COST 5", events);
            }
        }, iter: 200);

    [Fact]
    public void A_crash_report_never_carries_a_name_a_battletag_a_mail_or_a_user_path() =>
        Gen.Select(Name, BattleTag, Gen.Int[0, 4]).Sample((name, tag, shape) =>
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
