namespace Hearthlens.Core.Power;

/// <summary>Tag names as printed by Power.log.</summary>
public static class GameTag
{
    public const string Zone = "ZONE";
    public const string ZonePosition = "ZONE_POSITION";
    public const string Controller = "CONTROLLER";
    public const string CardType = "CARDTYPE";
    public const string Turn = "TURN";
    public const string Step = "STEP";
    public const string State = "STATE";
    public const string PlayState = "PLAYSTATE";
    public const string MulliganState = "MULLIGAN_STATE";
    public const string HeroEntity = "HERO_ENTITY";
    public const string FirstPlayer = "FIRST_PLAYER";
    public const string Creator = "CREATOR";
    public const string CreatorDbId = "CREATOR_DBID";
    public const string DisplayedCreator = "DISPLAYED_CREATOR";
    public const string Atk = "ATK";
    public const string Health = "HEALTH";
    public const string Damage = "DAMAGE";
    public const string Armor = "ARMOR";
    public const string Cost = "COST";
    public const string Premium = "PREMIUM";
    public const string Taunt = "TAUNT";
    public const string DivineShield = "DIVINE_SHIELD";
    public const string Poisonous = "POISONOUS";
    public const string Venomous = "VENOMOUS";
    public const string Reborn = "REBORN";
    public const string Windfury = "WINDFURY";
    public const string MegaWindfury = "MEGA_WINDFURY";
    public const string Stealth = "STEALTH";
    public const string Secret = "SECRET";
    public const string Class = "CLASS";
    public const string CardRace = "CARDRACE";
    public const string TechLevel = "TECH_LEVEL";
    public const string Attached = "ATTACHED";
    public const string ScriptData1 = "TAG_SCRIPT_DATA_NUM_1";
    public const string ScriptData2 = "TAG_SCRIPT_DATA_NUM_2";

    // Battlegrounds
    public const string PlayerId = "PLAYER_ID";
    public const string PlayerTechLevel = "PLAYER_TECH_LEVEL";
    public const string PlayerLeaderboardPlace = "PLAYER_LEADERBOARD_PLACE";
    public const string PlayerTriples = "PLAYER_TRIPLES";
    public const string NextOpponentPlayerId = "NEXT_OPPONENT_PLAYER_ID";
    public const string CurrentCombatPlayerId = "BACON_CURRENT_COMBAT_PLAYER_ID";
    public const string BoardVisualState = "BOARD_VISUAL_STATE";
    public const string BaconDummyPlayer = "BACON_DUMMY_PLAYER";
    public const string BaconAnomaly = "BACON_GLOBAL_ANOMALY_DBID";
    public const string BaconDeity = "BACON_GLOBAL_OLD_GOD_DBID";
    public const string BaconMaxTechLevel = "BACON_MAX_PLAYER_TECH_LEVEL";
    public const string BaconDuoTeamId = "BACON_DUO_TEAM_ID";

    private static readonly Dictionary<string, int> Zones = new(StringComparer.Ordinal)
    {
        ["INVALID"] = 0, ["PLAY"] = 1, ["DECK"] = 2, ["HAND"] = 3, ["GRAVEYARD"] = 4,
        ["REMOVEDFROMGAME"] = 5, ["SETASIDE"] = 6, ["SECRET"] = 7,
    };

    private static readonly Dictionary<string, int> PlayStates = new(StringComparer.Ordinal)
    {
        ["INVALID"] = 0, ["PLAYING"] = 1, ["WINNING"] = 2, ["LOSING"] = 3, ["WON"] = 4, ["LOST"] = 5,
        ["TIED"] = 6, ["DISCONNECTED"] = 7, ["CONCEDED"] = 8,
    };

    private static readonly Dictionary<string, int> Booleanish = new(StringComparer.Ordinal)
    {
        ["False"] = 0, ["True"] = 1,
    };

    /// <summary>Maps an enum-named value back to its integer when the tag is known.</summary>
    public static int EnumValue(string tag, string value)
    {
        var table = tag switch
        {
            Zone => Zones,
            PlayState => PlayStates,
            _ => Booleanish,
        };

        return table.TryGetValue(value, out var number) ? number : 0;
    }
}
