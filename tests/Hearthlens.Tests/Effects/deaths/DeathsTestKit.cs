using Hearthlens.Core.Battlegrounds;
using Hearthlens.Core.Battlegrounds.Effects;
using static Hearthlens.Tests.Sim.SimTestKit;

namespace Hearthlens.Tests.Sim.Deaths;

/// <summary>
/// Board pieces for deathrattle tests. The usual setup: their <see cref="Killer"/> attacks first and trades with our
/// deathrattle minion; then only their <see cref="Wall"/> is left, which has 0 Attack, so nothing hurts our side any
/// more and the survivors are exactly the board right after the deathrattles (the fight ends as a tie at the attack cap).
/// <para>
/// Namespace note: these tests live in Hearthlens.Tests.Sim.Deaths. A namespace named Hearthlens.Tests.Effects would
/// shadow SimTestKit.Effects(...) for every test under Hearthlens.Tests, so <see cref="Only"/> is used here instead.
/// </para>
/// </summary>
internal static class DeathsTestKit
{
    /// <summary>A registry with only these effect classes.</summary>
    public static EffectRegistry Only(params Type[] types) => EffectRegistry.FromTypes(types);

    /// <summary>A 0-Attack minion nothing can kill in 400 attacks.</summary>
    public static SimMinion Wall => Minion(0, 1_000_000, cardId: "WALL");

    public static SimMinion Killer(int attack, int health, Keyword keywords = Keyword.None) => Minion(attack, health, keywords, "KILLER");

    /// <summary>"CARD 2/3" / "CARD(G) 4/6 Taunt" per minion, left to right.</summary>
    public static List<string> Board(IReadOnlyList<SimMinion> minions) => minions.Select(Describe).ToList();

    public static string Describe(SimMinion m) =>
        $"{m.CardId}{(m.Golden ? "(G)" : string.Empty)} {m.Attack}/{m.Health}{(m.Keywords != Keyword.None ? " " + m.Keywords : string.Empty)}";

    /// <summary><paramref name="count"/> times the same description.</summary>
    public static IEnumerable<string> Times(int count, string description) => Enumerable.Repeat(description, count);
}

/// <summary>Card ids used by the deathrattle tests (normal ids; golden minions use them with golden: true).</summary>
internal static class Ids
{
    public const string Vermin = "BG31_803";
    public const string CordPuller = "BG29_611";
    public const string Bonehead = "BG28_300";
    public const string Underrot = "BG36_116";
    public const string Caretaker = "BG30_125";
    public const string Handless = "BG25_010";
    public const string Rover = "BG31_801";
    public const string Ghastcoiler = "BGS_008";
    public const string SewerLord = "BG35_604";
    public const string SewerRat = "BG19_010";
    public const string Skitterer = "BG31_809";
    public const string Salvager = "BG31_999";
    public const string ScarletSkull = "BG25_022";
    public const string Lobster = "BG36_202";
    public const string Plaguerunner = "BG34_690";
    public const string Goldrinn = "BGS_018";
    public const string Eudora = "BG33_828";
    public const string Cyclist = "BG31_925";
    public const string Mummifier = "BG28_309";
    public const string Nadina = "BGS_040";
    public const string Leeroy = "BG23_318";
    public const string Titus = "BG25_354";
    public const string Scorpid = "BG36_209";

    public const string Beetle = "BG28_603t";
    public const string Skeleton = "BG_ICC_026t";
    public const string Microbot = "BG_BOT_312t";
    public const string Hand = "BG25_010t";
    public const string Tentacle = "BGFYM_002t";
    public const string HalfShell = "BG19_010t";
}
