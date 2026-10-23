namespace Hearthlens.Core.Battlegrounds.Effects;

/// <summary>
/// Minions that effects summon, with their printed stats (golden versions doubled, as printed on the golden cards of
/// patch 37.0.0.255130). Player-wide bonuses (e.g. "your Beetles have +X/+Y") are not included: the summoning effect
/// or the token's own effect adds them.
/// </summary>
public static class Tokens
{
    public const string BeetleId = "BG28_603t";
    public const string SkeletonId = "BG_ICC_026t";
    public const string MicrobotId = "BG_BOT_312t";
    public const string HelpingHandId = "BG25_010t";
    public const string AberrantTentacleId = "BGFYM_002t";
    public const string ForagingBatId = "BG36_200t";
    public const string SewerRatId = "BG19_010";
    public const string HalfShellId = "BG19_010t";
    public const string EternalKnightId = "BG25_008";
    public const string TastyLobsterId = "BG36_202";
    public const string DesertedGolemId = "BG29_864t";
    public const string CThunId = "BGFYM_000";
    public const string YShaarjId = "BGFYM_011";
    public const string FishOfNZothId = "TB_BaconShop_HP_105t";
    public const string SneedsShredderId = "BG21_HERO_030t";
    public const string AmalgamId = "TB_BaconShop_HP_033t";
    public const string OzumatsTentacleId = "BG23_HERO_201pt";
    public const string OnyxianWhelpId = "BG22_HERO_305t";

    /// <summary>2/2 Beast (golden 4/4). Buzzing Vermin, Forest Rover, Turquoise Skitterer, Ravaging Scorpid.</summary>
    public static SimMinion Beetle(bool golden = false) => Make(BeetleId, 2, 2, Tribe.Beast, golden);

    /// <summary>1/1 Undead (golden 2/2). Harmless Bonehead, Cadaver Caretaker.</summary>
    public static SimMinion Skeleton(bool golden = false) => Make(SkeletonId, 1, 1, Tribe.Undead, golden);

    /// <summary>1/1 Mech (golden 2/2). Cord Puller.</summary>
    public static SimMinion Microbot(bool golden = false) => Make(MicrobotId, 1, 1, Tribe.Mech, golden);

    /// <summary>2/1 Undead with Reborn (golden 4/2). Handless Forsaken.</summary>
    public static SimMinion HelpingHand(bool golden = false) => Make(HelpingHandId, 2, 1, Tribe.Undead, golden, Keyword.Reborn);

    /// <summary>0/2 Aberration with Taunt (golden 0/4). Underrot Spawn.</summary>
    public static SimMinion AberrantTentacle(bool golden = false) => Make(AberrantTentacleId, 0, 2, Tribe.Aberration, golden, Keyword.Taunt);

    /// <summary>1/1 Beast (golden 2/2). Flittering Bat.</summary>
    public static SimMinion ForagingBat(bool golden = false) => Make(ForagingBatId, 1, 1, Tribe.Beast, golden);

    /// <summary>3/2 Beast, "Deathrattle: Summon a 2/3 Turtle with Taunt" (golden 6/4). Sewer Lord.</summary>
    public static SimMinion SewerRat(bool golden = false) => Make(SewerRatId, 3, 2, Tribe.Beast, golden, mechanics: CardMechanic.Deathrattle);

    /// <summary>2/3 Beast with Taunt (golden 4/6). Sewer Rat.</summary>
    public static SimMinion HalfShell(bool golden = false) => Make(HalfShellId, 2, 3, Tribe.Beast, golden, Keyword.Taunt);

    /// <summary>
    /// 4/2 Undead pool minion (golden 8/4) before its "+4/+2 (golden +8/+4) for each friendly Eternal Knight that died this
    /// game" bonus. Eternal Summoner, Eternal Tycoon.
    /// </summary>
    public static SimMinion EternalKnight(bool golden = false) => Make(EternalKnightId, 4, 2, Tribe.Undead, golden);

    /// <summary>2/1 Beast pool minion with a deathrattle (golden 4/2). Hoarding Hyena.</summary>
    public static SimMinion TastyLobster(bool golden = false) => Make(TastyLobsterId, 2, 1, Tribe.Beast, golden, mechanics: CardMechanic.Deathrattle);

    /// <summary>5/5 with no type (golden 10/10); summoners usually overwrite its stats. Jailbird Juggernaut, Golemancy.</summary>
    public static SimMinion DesertedGolem(bool golden = false) => Make(DesertedGolemId, 5, 5, Tribe.None, golden);

    /// <summary>2/2 Venomous with every type (golden 4/4). The Curator.</summary>
    public static SimMinion Amalgam(bool golden = false) => Make(AmalgamId, 2, 2, Tribe.All, golden, Keyword.Venomous);

    /// <summary>X/X Beast with Taunt (base 2/2). Ozumat's hero power.</summary>
    public static SimMinion OzumatsTentacle(bool golden = false) => Make(OzumatsTentacleId, 2, 2, Tribe.Beast, golden, Keyword.Taunt);

    /// <summary>1/1 Dragon (golden 2/2) that attacks immediately. Onyxia's hero power.</summary>
    public static SimMinion OnyxianWhelp(bool golden = false) => Make(OnyxianWhelpId, 1, 1, Tribe.Dragon, golden);

    /// <summary>A token with printed stats; golden doubles them.</summary>
    public static SimMinion Make(string cardId, int attack, int health, Tribe tribes, bool golden = false, Keyword keywords = Keyword.None,
                                 CardMechanic mechanics = CardMechanic.None)
    {
        var factor = golden ? 2 : 1;
        return SimMinion.Create(cardId, attack * factor, health * factor, tribes, keywords, golden, mechanics);
    }
}
