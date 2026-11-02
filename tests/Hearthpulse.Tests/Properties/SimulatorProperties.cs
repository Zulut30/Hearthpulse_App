using CsCheck;
using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Battlegrounds.Effects;

namespace Hearthpulse.Tests.Properties;

/// <summary>
/// Property tests of the combat simulator (Etalon T4, CsCheck): on random boards, the odds are a distribution, damage
/// is never negative, a fight is the same fight seen from the other side, and a fixed seed gives a fixed result.
/// </summary>
public sealed class SimulatorProperties
{
    private static readonly string[] CardIds = [.. EffectRegistry.Default.NormalCardIds.Order(StringComparer.Ordinal)];

    private static readonly Keyword[] Keywords = [Keyword.None, Keyword.Taunt, Keyword.DivineShield, Keyword.Reborn, Keyword.Windfury, Keyword.Venomous, Keyword.Poisonous];

    private static readonly Tribe[] Tribes = [Tribe.None, Tribe.Beast, Tribe.Demon, Tribe.Dragon, Tribe.Elemental, Tribe.Mech, Tribe.Murloc, Tribe.Naga, Tribe.Pirate, Tribe.Quilboar, Tribe.Undead];

    /// <summary>A minion with a real card's effects, or none ("" card id), and any printed stats and keyword.</summary>
    private static readonly Gen<SimMinion> Minion =
        Gen.Select(Gen.Int[-1, CardIds.Length - 1], Gen.Int[0, 40], Gen.Int[1, 40], Gen.Int[0, Keywords.Length - 1], Gen.Int[0, Tribes.Length - 1], Gen.Bool)
            .Select((card, attack, health, keyword, tribe, golden) =>
                SimMinion.Create(card < 0 ? string.Empty : CardIds[card], attack, health, Tribes[tribe], Keywords[keyword], golden));

    /// <summary>A minion without a card: keywords only, so nothing on the board depends on which side it is.</summary>
    private static readonly Gen<SimMinion> Vanilla =
        Gen.Select(Gen.Int[0, 30], Gen.Int[1, 30], Gen.Int[0, Keywords.Length - 1])
            .Select((attack, health, keyword) => SimMinion.Create(string.Empty, attack, health, Tribe.None, Keywords[keyword]));

    private static Gen<SimSide> Board(Gen<SimMinion> minion, int min) => minion.Array[min, 7].Select(m => new SimSide(m));

    [Fact]
    public void The_odds_are_a_distribution_and_damage_is_never_negative() =>
        Gen.Select(Board(Minion, 0), Board(Minion, 0), Gen.Bool).Sample((ours, theirs, oursFirst) =>
        {
            var odds = CombatSimulator.Run(ours, theirs, oursFirst, new SimOptions { Runs = 200 });
            Assert.InRange(odds.Win, 0, 1);
            Assert.InRange(odds.Tie, 0, 1);
            Assert.InRange(odds.Loss, 0, 1);
            Assert.Equal(1.0, odds.Win + odds.Tie + odds.Loss, 9);
            foreach (var spread in new[] { odds.DamageTaken, odds.DamageDealt })
            {
                Assert.True(spread is null || (spread.Low >= 0 && spread.Mean >= 0 && spread.Counts.All(c => c >= 0)), "damage is never negative");
            }
        }, iter: 60);

    [Fact]
    public void A_fight_seen_from_the_other_side_is_the_same_fight() =>
        Gen.Select(Board(Vanilla, 1), Board(Vanilla, 1), Gen.Int).Sample((a, b, seed) =>
        {
            // A attacks first in both: once as "ours", once as "theirs", with the same random picks.
            var asOurs = CombatSimulator.RunSingle(a, b, true, new Random(seed), EffectRegistry.Default, trace: false);
            var asTheirs = CombatSimulator.RunSingle(b, a, false, new Random(seed), EffectRegistry.Default, trace: false);
            Assert.Equal(asOurs.Outcome, -asTheirs.Outcome);
            Assert.Equal(Stats(asOurs.Ours), Stats(asTheirs.Theirs));
            Assert.Equal(Stats(asOurs.Theirs), Stats(asTheirs.Ours));
        }, iter: 300);

    [Fact]
    public void A_fight_of_minions_with_effects_has_the_same_result_from_the_other_side() =>
        Gen.Select(Board(Minion, 1), Board(Minion, 1), Gen.Int).Sample((a, b, seed) =>
        {
            var asOurs = CombatSimulator.RunSingle(a, b, true, new Random(seed), EffectRegistry.Default, trace: false);
            var asTheirs = CombatSimulator.RunSingle(b, a, false, new Random(seed), EffectRegistry.Default, trace: false);
            Assert.Equal(asOurs.Outcome, -asTheirs.Outcome);
        }, iter: 300);

    /// <summary>Stats a log or a broken capture can hand over: negative attack, health of zero or below.</summary>
    private static readonly Gen<SimMinion> Broken =
        Gen.Select(Gen.Int[-1, CardIds.Length - 1], Gen.Int[-10, 30], Gen.Int[-10, 30], Gen.Int[0, Keywords.Length - 1])
            .Select((card, attack, health, keyword) => SimMinion.Create(card < 0 ? string.Empty : CardIds[card], attack, health, Tribe.None, Keywords[keyword]));

    [Fact]
    public void Broken_stats_still_give_odds_damage_and_lethal_in_range() =>
        Gen.Select(Board(Broken, 0), Board(Broken, 0), Gen.Int[1, 40], Gen.Int[1, 40], Gen.Bool).Sample((ours, theirs, ourHealth, theirHealth, oursFirst) =>
        {
            var odds = CombatSimulator.Run(ours with { Health = ourHealth }, theirs with { Health = theirHealth }, oursFirst, new SimOptions { Runs = 100 });
            Assert.Equal(1.0, odds.Win + odds.Tie + odds.Loss, 9);
            foreach (var spread in new[] { odds.DamageTaken, odds.DamageDealt })
            {
                Assert.True(spread is null || (spread.Low >= 0 && spread.Low <= spread.High && spread.High <= spread.Max && spread.Mean >= 0 && spread.Mean <= spread.Max),
                    $"spread {spread}");
            }

            foreach (var lethal in new[] { odds.LethalTaken, odds.LethalDealt })
            {
                Assert.True(lethal is null || lethal is >= 0 and <= 1, $"lethal {lethal}");
            }
        }, iter: 80);

    [Fact]
    public void A_fixed_seed_gives_the_same_fight() =>
        Gen.Select(Board(Minion, 0), Board(Minion, 0), Gen.Bool, Gen.Int).Sample((ours, theirs, oursFirst, seed) =>
        {
            var first = CombatSimulator.RunSingle(ours, theirs, oursFirst, new Random(seed), EffectRegistry.Default, trace: true);
            var again = CombatSimulator.RunSingle(ours, theirs, oursFirst, new Random(seed), EffectRegistry.Default, trace: true);
            Assert.Equal(first.Outcome, again.Outcome);
            Assert.Equal(first.Log, again.Log);
            Assert.Equal(Stats(first.Ours), Stats(again.Ours));
        }, iter: 200);

    [Fact]
    public void The_odds_of_the_same_boards_are_the_same_odds() =>
        Gen.Select(Board(Minion, 1), Board(Minion, 1), Gen.Bool).Sample((ours, theirs, oursFirst) =>
        {
            var options = new SimOptions { Runs = 200 };
            Assert.Equal(Odds(CombatSimulator.Run(ours, theirs, oursFirst, options)), Odds(CombatSimulator.Run(ours, theirs, oursFirst, options)));
        }, iter: 30);

    private static string Odds(CombatOdds o) => string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"{o.Win:R} {o.Tie:R} {o.Loss:R} {o.Runs} {o.Unsimulated} {o.DamageTaken?.Mean:R} {o.DamageDealt?.Mean:R} {o.LethalTaken:R} {o.LethalDealt:R}");

    private static string Stats(IEnumerable<SimMinion> minions) =>
        string.Join(',', minions.Select(m => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{m.CardId}:{m.Attack}/{m.Health}")));
}
