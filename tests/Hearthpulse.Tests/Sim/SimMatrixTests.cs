using System.Text.RegularExpressions;
using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Battlegrounds.Effects;

namespace Hearthpulse.Tests.Sim;

/// <summary>
/// The coverage matrix (plan S1, <see cref="SimCoverage"/>, docs/bg-sim-matrix.md): statuses agree with the effect
/// registry and the relevance tables, and the generated file agrees with the code.
/// </summary>
public sealed partial class SimMatrixTests
{
    private static readonly EffectRegistry Registry = EffectRegistry.Default;

    [Fact]
    public void Six_status_codes()
    {
        Assert.Equal(["no_effect", "resolved", "implemented", "missing_effect", "missing_input", "unknown"], SimCoverage.Codes);
        Assert.Equal(Enum.GetValues<CoverageStatus>().Length, SimCoverage.Codes.Count);
    }

    [Fact]
    public void Every_table_row_gets_the_status_its_relevance_and_effect_give()
    {
        foreach (var row in CombatRelevance.Table.Concat(PlayerCardGenerated.Entries).Concat(PlayerCardTable.Entries))
        {
            // The hand table wins over the generated one, as in CombatRelevance.
            var effective = CombatRelevance.Find(row.CardId)!;
            var kind = effective.Origin == Origin.Player ? CoverageKind.Trinket : CoverageKind.Minion;
            var status = SimCoverage.Classify(row.CardId, kind).Status;
            var effect = Registry.Get(row.CardId, golden: false);
            var implemented = effect is not null;
            var expected = effective.Relevance switch
            {
                Relevance.Resolved => CoverageStatus.Resolved,
                _ when effect?.UncapturedInput is not null => CoverageStatus.MissingInput,
                _ when implemented => CoverageStatus.Implemented,
                Relevance.Relevant or Relevance.Conditional => SimCoverage.InputGap(row.CardId) is not null ? CoverageStatus.MissingInput : CoverageStatus.MissingEffect,
                _ => CoverageStatus.NoEffect,
            };
            Assert.True(expected == status, $"{row.CardId} {row.Name}: {status}, expected {expected}");
        }
    }

    [Fact]
    public void Every_registered_effect_is_implemented_unless_its_card_resolves_before_capture()
    {
        foreach (var id in Registry.NormalCardIds)
        {
            var status = SimCoverage.Classify(id, CoverageKind.Minion).Status;
            var ok = Registry.Get(id, golden: false)!.UncapturedInput is null
                ? status is CoverageStatus.Implemented or CoverageStatus.Resolved
                : status == CoverageStatus.MissingInput;
            Assert.True(ok, $"{id}: {status}");
        }
    }

    [Theory]
    [InlineData("BG36_MidGameEffect_000t60e", CoverageStatus.Implemented)] // Invulnerability
    [InlineData("BG22_HERO_305p", CoverageStatus.MissingInput)] // Broodmother: Avenge progress before the capture
    [InlineData("BG23_HERO_201p", CoverageStatus.Implemented)] // Tentacular
    [InlineData("BG35_MagicItem_701", CoverageStatus.Implemented)] // Fang Anklet
    [InlineData("BG30_MagicItem_442", CoverageStatus.MissingInput)] // Blood Golem Sticker
    [InlineData("BG30_MagicItem_427", CoverageStatus.MissingEffect)] // Tiger Carving: out of the pool, no code
    public void Plan_candidates(string cardId, CoverageStatus status) =>
        Assert.Equal(status, SimCoverage.Classify(cardId, CoverageKind.Trinket).Status);

    [Fact]
    public void Growing_values_say_what_they_need()
    {
        Assert.Contains("missing_input", SimCoverage.Classify("BG23_HERO_201p", CoverageKind.HeroPower).Note, StringComparison.Ordinal);
        Assert.Contains("допущение", SimCoverage.Classify("BG35_MagicItem_701", CoverageKind.Trinket).Note, StringComparison.Ordinal);
        Assert.Contains("Avenge", SimCoverage.Classify("BG22_HERO_305p", CoverageKind.HeroPower).Note, StringComparison.Ordinal);
        Assert.Contains("рука скрыта", SimCoverage.Classify("BG34_140", CoverageKind.Minion).Note, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CoverageKind.Trinket, "Whenever a friendly minion dies, summon a 1/1.", CoverageStatus.Unknown)]
    [InlineData(CoverageKind.HeroPower, "Get a Coin.", CoverageStatus.Unknown)]
    [InlineData(CoverageKind.Minion, "<b>Deathrattle:</b> Summon a 2/2 Beetle.", CoverageStatus.Unknown)]
    [InlineData(CoverageKind.Minion, "<b>Start of Combat:</b> Gain +2/+2.", CoverageStatus.Resolved)]
    [InlineData(CoverageKind.Minion, "<b>Battlecry:</b> Get a Coin.", CoverageStatus.NoEffect)]
    [InlineData(CoverageKind.Minion, null, CoverageStatus.Unknown)]
    public void Cards_no_table_knows(CoverageKind kind, string? text, CoverageStatus status) =>
        Assert.Equal(status, SimCoverage.Classify("TEST_NEW_CARD", kind, text).Status);

    [Fact]
    public void The_generated_matrix_agrees_with_the_code()
    {
        var path = Path.Combine(RepoFiles.Root, "docs", "bg-sim-matrix.md");
        var lines = File.ReadAllLines(path);
        foreach (var code in SimCoverage.Codes)
        {
            Assert.Contains(lines, l => l.StartsWith("| Вид |", StringComparison.Ordinal) && l.Contains($" {code} |", StringComparison.Ordinal));
        }

        var rows = 0;
        string? section = null;
        foreach (var line in lines)
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                section = line[3..];
                continue;
            }

            if (RowRegex().Match(line) is not { Success: true } m)
            {
                continue;
            }

            rows++;
            var (id, status, pool) = (m.Groups["id"].Value, m.Groups["status"].Value, m.Groups["pool"].Value);
            Assert.Contains(pool, new[] { "да", "пул не подтверждён", "вне пула" });
            // A Dark Gift row is judged by the enchantment named first in its note.
            var subject = section == "Тёмные дары" ? m.Groups["note"].Value.Split(';')[0].Trim() : id;
            var known = CombatRelevance.Find(subject) is not null || Registry.Get(subject, golden: false) is not null || SimCoverage.InputGap(subject) is not null;
            if (!known)
            {
                // Classified from the card text, which the test does not have.
                Assert.Contains(status, new[] { "no_effect", "resolved", "unknown" });
                continue;
            }

            var kind = section switch
            {
                "Силы героя" => CoverageKind.HeroPower,
                "Тринкеты" => CoverageKind.Trinket,
                _ => CoverageKind.Minion,
            };
            Assert.True(SimCoverage.Code(SimCoverage.Classify(subject, kind).Status) == status, $"{id}: file says {status}; regenerate with Hearthpulse.Bench simmatrix");
        }

        Assert.True(rows > 300, $"only {rows} rows");
    }

    [Fact]
    public void Explain_counts_what_the_odds_count()
    {
        var ours = new SimSide([SimMinion.Create("BG36_207", 3, 6) with { HasUnsimulatedEffect = true }])
        {
            Extras = ["BG30_MagicItem_442", "BG30_MagicItem_925", "TEST_NEWER_THAN_DATA"],
        };
        var theirs = new SimSide([SimMinion.Create("BG36_207", 3, 6)]);
        var items = CombatSimulator.Explain(ours, isOpponent: false);

        Assert.Equal(["missing_effect", "missing_input", "unknown"], items.Select(i => i.Code).ToArray());
        Assert.Equal(items.Count, CombatSimulator.Run(ours, theirs, true, new SimOptions { Runs = 10 }).Unsimulated);
    }

    [GeneratedRegex(@"^\| (?<id>[A-Za-z0-9_]+) \| .*? \| (?<status>no_effect|resolved|implemented|missing_effect|missing_input|unknown) \| (?<pool>[^|]+?) \| (?<note>.*) \|$")]
    private static partial Regex RowRegex();
}
