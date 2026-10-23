namespace Hearthlens.Core.Battlegrounds.Effects.Ongoing;

/// <summary>
/// The Deity (C'Thun, Y'Shaarj): every lobby has one, and each player has a sleeping copy of it with its own stats. After
/// four friendly Aberrations died in a combat it awakens (is summoned) in that combat.
/// <para>
/// Model, from the card texts and the research notes (log tags unverified, see the assumptions in the commit message):
/// </para>
/// <list type="bullet">
/// <item>The lobby's Deity card id comes in <see cref="SimSide.Extras"/> (the tracker adds it from BACON_GLOBAL_OLD_GOD_DBID);
/// its registered effect becomes an off-board "sleeping" holder, see <see cref="DeityEffect"/>.</item>
/// <item>The sleeping Deity's stats are the player's tags BACON_OLD_GOD_ATTACK / BACON_OLD_GOD_HEALTH (read as the Deity's
/// total stats; the printed 1/1 when a tag is missing, as for the opponent's dummy player). "Give your Deity +X/+Y" adds
/// to them in <see cref="Side.Counter"/> while it sleeps, and buffs the awakened minion once it is on the board.</item>
/// </list>
/// </summary>
internal static class Deity
{
    public const string AttackTag = "BACON_OLD_GOD_ATTACK";
    public const string HealthTag = "BACON_OLD_GOD_HEALTH";

    // Power.log prints a tag it has no name for as its number (4914 / 4915 in the research notes).
    private const string AttackTagNumber = "4914";
    private const string HealthTagNumber = "4915";

    /// <summary>Friendly Aberrations that must die in one combat to awaken the Deity.</summary>
    public const int AberrationDeathsToAwaken = 4;

    public static bool IsDeity(string cardId) => cardId == Tokens.CThunId || cardId == Tokens.YShaarjId;

    /// <summary>The sleeping Deity holder of a side (an off-board extra), or null when the side has no registered Deity.</summary>
    public static Unit? Holder(Side side)
    {
        foreach (var extra in side.Extras)
        {
            if (IsDeity(extra.CardId))
            {
                return extra;
            }
        }

        return null;
    }

    /// <summary>True once the Deity has been summoned this combat.</summary>
    public static bool IsAwake(Unit holder) => holder.Counter2 != 0;

    /// <summary>The awakened Deity on the board, if it is alive.</summary>
    public static Unit? Awakened(Side side, Unit holder)
    {
        foreach (var unit in side.Minions)
        {
            if (unit.CardId == holder.CardId && unit.IsAlive)
            {
                return unit;
            }
        }

        return null;
    }

    /// <summary>Attack of the sleeping Deity (at least 1, its printed value).</summary>
    public static int Attack(Side side) => Math.Max(1, Read(side, AttackTag, AttackTagNumber));

    /// <summary>Health of the sleeping Deity (at least 1, its printed value).</summary>
    public static int Health(Side side) => Math.Max(1, Read(side, HealthTag, HealthTagNumber));

    /// <summary>"Give your Deity +attack/+health": while it sleeps this raises its stats, once awake it buffs the minion.</summary>
    public static void Give(Fight fight, Side side, int attack, int health)
    {
        if (Holder(side) is not { } holder)
        {
            return;
        }

        if (!IsAwake(holder))
        {
            side.SetCounter(AttackTag, Attack(side) + attack);
            side.SetCounter(HealthTag, Health(side) + health);
            if (fight.Tracing) fight.Trace($"{(side.IsOpponent ? "their" : "our")} Deity gets +{attack}/+{health} ({Attack(side)}/{Health(side)})");
        }
        else if (Awakened(side, holder) is { } deity)
        {
            fight.Buff(deity, attack, health);
        }
    }

    private static int Read(Side side, string name, string number)
    {
        var value = side.Counter(name);
        return value > 0 ? value : side.Counter(number);
    }
}
