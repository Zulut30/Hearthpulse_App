namespace Hearthpulse.Core.Secrets;

/// <summary>
/// Something the player can see happen on their own turn (or at the start of the opponent's) that some opponent
/// Secrets react to. "You" is the local player, "their" is the opponent who owns the Secrets.
/// </summary>
public enum SecretTrigger
{
    /// <summary>You played a minion from your hand and it is still on the board after the play.</summary>
    MinionPlayed,

    /// <summary>You played a minion while you already had three or more (Sacred Trial).</summary>
    MinionPlayedOnFourth,

    /// <summary>You cast a spell from your hand (the Coin counts).</summary>
    SpellCast,

    /// <summary>You cast a spell from your hand on a minion.</summary>
    SpellOnMinion,

    /// <summary>You played a weapon from your hand.</summary>
    WeaponPlayed,

    /// <summary>You played your third card this turn.</summary>
    ThirdCardInTurn,

    /// <summary>You used your Hero Power.</summary>
    HeroPowerUsed,

    /// <summary>You drew your second card this turn.</summary>
    SecondDraw,

    /// <summary>You played a card on the turn it entered your hand.</summary>
    CardPlayedSameTurnDrawn,

    /// <summary>A play of yours spent the last of your Mana.</summary>
    SpentAllMana,

    /// <summary>One of your characters attacked their hero.</summary>
    HeroAttacked,

    /// <summary>One of your minions attacked their hero.</summary>
    HeroAttackedByMinion,

    /// <summary>One of your characters attacked one of their minions.</summary>
    MinionAttacked,

    /// <summary>One of your characters attacked (anything of theirs).</summary>
    EnemyAttacks,

    /// <summary>One of your minions attacked.</summary>
    EnemyMinionAttacks,

    /// <summary>One of your minions dealt 3 or more damage with an attack.</summary>
    MinionDealtThreeDamage,

    /// <summary>Their hero lost Health on your turn.</summary>
    HeroDamaged,

    /// <summary>One of their minions died on your turn.</summary>
    FriendlyMinionDied,

    /// <summary>One of their minions died on your turn, the turn after they played it.</summary>
    FriendlyMinionDiedTurnAfterPlayed,

    /// <summary>Your turn ended.</summary>
    TurnEnded,

    /// <summary>Your turn ended after you played at least one card.</summary>
    TurnEndedAfterPlays,

    /// <summary>Your turn ended with no Mana left.</summary>
    TurnEndedNoMana,

    /// <summary>Your turn ended and their hero took no damage during it.</summary>
    TurnEndedOwnerUndamaged,

    /// <summary>Their turn started.</summary>
    OwnerTurnStarted,

    /// <summary>Their turn started while they had exactly two minions.</summary>
    OwnerTurnStartedTwoMinions,
}

/// <summary>What else must hold for a Secret to fire on its trigger; a Secret is only ruled out when all of it held.</summary>
[Flags]
public enum SecretNeeds
{
    None = 0,

    /// <summary>They had fewer than 7 minions before and after the event (the Secret summons).</summary>
    BoardSpace = 1,

    /// <summary>They had fewer than 10 cards in hand before and after (the Secret adds a card).</summary>
    HandSpace = 2,

    /// <summary>They had a minion before and after (for a death: another one that survived it).</summary>
    OwnerMinion = 4,

    /// <summary>You had a minion on the board before and after.</summary>
    EnemyMinion = 8,

    /// <summary>The attacked hero was not the only character the attack could be sent to (Misdirection).</summary>
    OtherTarget = 16,

    /// <summary>The attacking minion had a neighbour (Sudden Betrayal).</summary>
    AttackerNeighbor = 32,

    /// <summary>The attacked minion had no Divine Shield yet (Autodefense Matrix).</summary>
    DefenderUnshielded = 64,

    /// <summary>The minion the Secret acts on could be affected: not Elusive, not Dormant.</summary>
    Targetable = 128,
}

/// <summary>When a Secret fires: any of <see cref="Triggers"/>, provided <see cref="Needs"/> held.</summary>
public sealed record SecretRule(IReadOnlyList<SecretTrigger> Triggers, SecretNeeds Needs = SecretNeeds.None)
{
    public SecretRule(SecretTrigger trigger, SecretNeeds needs = SecretNeeds.None)
        : this([trigger], needs)
    {
    }
}

/// <summary>
/// Trigger table for every collectible Secret, keyed by the card id without its reprint prefix (<c>CORE_</c>, <c>VAN_</c>).
/// Written from the English card text (HearthstoneJSON). A Secret listed with a null rule fires on something the player
/// cannot see or check (a minion in their hand, fatal damage), so no event rules it out. A Secret missing from the
/// table is never ruled out either; <c>SecretsTests.Every_collectible_secret_has_a_rule_entry</c> fails until a new one is added.
/// </summary>
public static class SecretRules
{
    private static readonly IReadOnlyDictionary<string, SecretRule?> Table = new Dictionary<string, SecretRule?>(StringComparer.Ordinal)
    {
        // ---- Hunter -------------------------------------------------------------------------
        ["EX1_610"] = new(SecretTrigger.HeroAttacked),                                         // Explosive Trap
        ["EX1_611"] = new(SecretTrigger.EnemyMinionAttacks, SecretNeeds.Targetable),           // Freezing Trap
        ["GIL_577"] = new(SecretTrigger.ThirdCardInTurn, SecretNeeds.BoardSpace),              // Rat Trap
        ["ULD_152"] = new(SecretTrigger.SpellCast, SecretNeeds.EnemyMinion),                   // Pressure Plate
        ["TIME_620"] = new(SecretTrigger.FriendlyMinionDiedTurnAfterPlayed),                    // Untimely Death
        ["AT_060"] = new(SecretTrigger.HeroAttacked, SecretNeeds.BoardSpace),                  // Bear Trap
        ["AV_226"] = new(SecretTrigger.SpellCast),                                             // Ice Trap
        ["BT_203"] = new(SecretTrigger.MinionAttacked, SecretNeeds.BoardSpace),                // Pack Tactics
        ["CFM_026"] = null,                                                                    // Hidden Cache: needs a minion in their hand
        ["EX1_554"] = new(SecretTrigger.MinionAttacked, SecretNeeds.BoardSpace),               // Snake Trap
        ["ICC_200"] = new(SecretTrigger.MinionAttacked, SecretNeeds.BoardSpace),               // Venomstrike Trap
        ["KAR_004"] = new(SecretTrigger.SpellCast, SecretNeeds.BoardSpace),                    // Cat Trick
        ["LOOT_079"] = new(SecretTrigger.HeroAttacked, SecretNeeds.BoardSpace),                // Wandering Monster
        ["MAW_010"] = new(SecretTrigger.ThirdCardInTurn),                                      // Motion Denied
        ["DMF_123"] = new(SecretTrigger.OwnerTurnStartedTwoMinions, SecretNeeds.BoardSpace),   // Open the Cages
        ["EX1_533"] = new(SecretTrigger.HeroAttacked, SecretNeeds.OtherTarget),                // Misdirection
        ["EX1_609"] = new(SecretTrigger.MinionPlayed, SecretNeeds.Targetable),                 // Snipe
        ["WON_018"] = new(SecretTrigger.MinionPlayed, SecretNeeds.Targetable),                 // Snipe (Wonders)
        ["JAM_003"] = new(SecretTrigger.TurnEndedNoMana, SecretNeeds.BoardSpace),              // Hidden Meaning
        ["LOE_021"] = new(SecretTrigger.HeroPowerUsed),                                        // Dart Trap
        ["MIS_105"] = new([SecretTrigger.MinionPlayed, SecretTrigger.SpellCast, SecretTrigger.WeaponPlayed], SecretNeeds.HandSpace), // Bargain Bin
        ["NX2_013"] = new(SecretTrigger.MinionPlayed, SecretNeeds.BoardSpace),                 // ZOMBEEEES!!!
        ["TSC_929"] = new(SecretTrigger.FriendlyMinionDied),                                   // Emergency Maneuvers
        ["TTN_504"] = new(SecretTrigger.MinionAttacked),                                       // Bait and Switch

        // ---- Mage ---------------------------------------------------------------------------
        ["AT_002"] = new(SecretTrigger.FriendlyMinionDied),                                    // Effigy
        ["BAR_812"] = new(SecretTrigger.MinionAttacked, SecretNeeds.BoardSpace),               // Oasis Ally
        ["BT_003"] = new(SecretTrigger.SpellCast, SecretNeeds.BoardSpace),                     // Netherwind Portal
        ["CFM_620"] = new(SecretTrigger.MinionPlayed, SecretNeeds.Targetable),                 // Potion of Polymorph
        ["EX1_294"] = new(SecretTrigger.MinionPlayed, SecretNeeds.BoardSpace),                 // Mirror Entity
        ["ICC_082"] = new(SecretTrigger.MinionPlayed, SecretNeeds.HandSpace),                  // Frozen Clone
        ["MAW_006"] = new(SecretTrigger.MinionPlayed),                                         // Objection!
        ["REV_516"] = new(SecretTrigger.HeroAttackedByMinion, SecretNeeds.BoardSpace),         // Vengeful Visage
        ["DEEP_000"] = new(SecretTrigger.OwnerTurnStarted, SecretNeeds.OwnerMinion | SecretNeeds.BoardSpace), // Summoning Ward
        ["DMF_107"] = new(SecretTrigger.TurnEndedOwnerUndamaged, SecretNeeds.HandSpace),       // Rigged Faire Game
        ["EX1_287"] = new(SecretTrigger.SpellCast),                                            // Counterspell
        ["EX1_289"] = new(SecretTrigger.HeroAttacked),                                         // Ice Barrier
        ["EX1_295"] = null,                                                                    // Ice Block: fatal damage
        ["EX1_594"] = new(SecretTrigger.HeroAttackedByMinion, SecretNeeds.Targetable),         // Vaporize
        ["FP1_018"] = new(SecretTrigger.FriendlyMinionDied, SecretNeeds.HandSpace),            // Duplicate
        ["LOOT_101"] = new(SecretTrigger.MinionPlayed, SecretNeeds.Targetable),                // Explosive Runes
        ["TRL_400"] = new(SecretTrigger.MinionAttacked, SecretNeeds.BoardSpace),               // Splitting Image
        ["tt_010"] = new(SecretTrigger.SpellOnMinion, SecretNeeds.BoardSpace),                 // Spellbender
        ["ULD_239"] = new(SecretTrigger.HeroAttackedByMinion),                                 // Flame Ward
        ["UNG_024"] = new(SecretTrigger.SpellCast, SecretNeeds.HandSpace),                     // Mana Bind
        ["WW_422"] = new(SecretTrigger.CardPlayedSameTurnDrawn, SecretNeeds.HandSpace),        // Azerite Vein
        ["END_024"] = new(SecretTrigger.TurnEnded, SecretNeeds.EnemyMinion),                   // Flames of Infinity
        ["JAIL_315"] = new(SecretTrigger.EnemyMinionAttacks, SecretNeeds.Targetable),          // Mystic Misdirection

        // ---- Paladin ------------------------------------------------------------------------
        ["AT_073"] = new(SecretTrigger.OwnerTurnStarted, SecretNeeds.OwnerMinion),             // Competitive Spirit
        ["BAR_550"] = new(SecretTrigger.ThirdCardInTurn, SecretNeeds.BoardSpace),              // Galloping Savior
        ["BOT_908"] = new(SecretTrigger.MinionAttacked, SecretNeeds.DefenderUnshielded),       // Autodefense Matrix
        ["CFM_800"] = new(SecretTrigger.FriendlyMinionDied, SecretNeeds.HandSpace),            // Getaway Kodo
        ["CS3_016"] = new(SecretTrigger.MinionDealtThreeDamage, SecretNeeds.Targetable),       // Reckoning
        ["EX1_130"] = new(SecretTrigger.EnemyAttacks, SecretNeeds.BoardSpace),                 // Noble Sacrifice
        ["FP1_020"] = new(SecretTrigger.FriendlyMinionDied, SecretNeeds.OwnerMinion),          // Avenge
        ["DAL_570"] = new(SecretTrigger.SpellCast, SecretNeeds.OwnerMinion),                   // Never Surrender!
        ["DMF_236"] = new(SecretTrigger.SpellCast),                                            // Oh My Yogg!
        ["EX1_132"] = new(SecretTrigger.HeroDamaged),                                          // Eye for an Eye
        ["EX1_136"] = new(SecretTrigger.FriendlyMinionDied, SecretNeeds.BoardSpace),           // Redemption
        ["EX1_379"] = new(SecretTrigger.MinionPlayed, SecretNeeds.Targetable),                 // Repentance
        ["GIL_903"] = new(SecretTrigger.ThirdCardInTurn, SecretNeeds.HandSpace),               // Hidden Wisdom
        ["LOE_027"] = new(SecretTrigger.MinionPlayedOnFourth, SecretNeeds.Targetable),         // Sacred Trial
        ["WC_033"] = new(SecretTrigger.EnemyMinionAttacks, SecretNeeds.Targetable),            // Judgment of Justice

        // ---- Rogue --------------------------------------------------------------------------
        ["BT_042"] = new(SecretTrigger.MinionAttacked),                                        // Bamboozle
        ["BT_707"] = new(SecretTrigger.MinionPlayed, SecretNeeds.BoardSpace),                  // Ambush
        ["BT_709"] = new(SecretTrigger.SpellCast, SecretNeeds.HandSpace),                      // Dirty Tricks
        ["LOOT_204"] = new(SecretTrigger.FriendlyMinionDied, SecretNeeds.HandSpace),           // Cheat Death
        ["MAW_018"] = new(SecretTrigger.OwnerTurnStarted),                                     // Perjury
        ["REV_825"] = new(SecretTrigger.SpentAllMana, SecretNeeds.HandSpace),                  // Double Cross
        ["REV_827"] = new(SecretTrigger.SpellCast, SecretNeeds.BoardSpace),                    // Sticky Situation
        ["REV_828"] = new(SecretTrigger.MinionPlayed, SecretNeeds.Targetable),                 // Kidnap
        ["SCH_706"] = new(SecretTrigger.TurnEndedAfterPlays, SecretNeeds.HandSpace),           // Plagiarize
        ["DMF_513"] = new(SecretTrigger.HeroAttackedByMinion, SecretNeeds.BoardSpace),         // Shadow Clone
        ["LOOT_210"] = new(SecretTrigger.HeroAttackedByMinion, SecretNeeds.AttackerNeighbor),  // Sudden Betrayal
        ["LOOT_214"] = new(SecretTrigger.HeroDamaged),                                         // Evasion
        ["YOP_017"] = new(SecretTrigger.SecondDraw),                                           // Shenanigans
    };

    /// <summary>The card id without a reprint prefix: <c>CORE_EX1_287</c> and <c>VAN_EX1_287</c> are <c>EX1_287</c>.</summary>
    public static string BaseId(string cardId) =>
        cardId.StartsWith("CORE_", StringComparison.Ordinal) ? cardId[5..]
        : cardId.StartsWith("VAN_", StringComparison.Ordinal) ? cardId[4..]
        : cardId;

    /// <summary>True when the table has an entry for the Secret (its rule may still be null).</summary>
    public static bool Knows(string cardId) => Table.ContainsKey(BaseId(cardId));

    /// <summary>The Secret's rule; null when unknown or when nothing the player sees can rule it out.</summary>
    public static SecretRule? For(string cardId) => Table.GetValueOrDefault(BaseId(cardId));
}
