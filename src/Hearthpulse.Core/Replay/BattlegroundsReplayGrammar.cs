using System.Text.RegularExpressions;

namespace Hearthpulse.Core.Replay;

/// <summary>
/// The grammar of a format 4 Battlegrounds event line, as <c>docs/match-replay.md</c> ("File format 4") writes it: what
/// <see cref="BattlegroundsReplayRecorder"/> writes and what an imported file may hold. Which card types a side and zone
/// can show is part of it (ours in play and in hand, Bob's offer, the combat opponent in play); the order of the lines
/// (Bob's offer only outside a combat...) is checked on top of it.
/// </summary>
internal static partial class BattlegroundsReplayGrammar
{
    private const string Id = "[1-9][0-9]{0,6}";
    private const string IdOrNone = "(?:[1-9][0-9]{0,6}|-)";
    private const string Number = "(?:0|[1-9][0-9]{0,8})";
    private const string Small = "(?:[0-9]|[1-9][0-9])";
    private const string Card = "[A-Za-z0-9_]{1,64}";
    private const string CardTag = "(?:ATK|HEALTH|DAMAGE|ARMOR|PREMIUM|TAUNT|DIVINE_SHIELD|REBORN|POISONOUS|VENOMOUS|WINDFURY|MEGA_WINDFURY|STEALTH|FROZEN|TECH_LEVEL)";
    private const string Table = "(?:HERO|HERO_POWER|MINION|BATTLEGROUND_TRINKET|BATTLEGROUND_QUEST_REWARD)";
    private const string Offer = "(?:MINION|BATTLEGROUND_SPELL|SPELL)";
    private const string Tags = "(?: " + CardTag + "=" + Number + ")*";
    private const string At = " " + Small + " " + Card + " ";

    /// <summary>True when <paramref name="line"/> is a Battlegrounds event line of format 4.</summary>
    public static bool IsLine(string line) => Line().IsMatch(line);

    [GeneratedRegex(
        "^(?:"
        + "[TF] [1-9][0-9]{0,2} " + Number
        + "|f (?:WIN|LOSS|TIE) " + Number + " " + Number
        + "|M (?:BUY|SELL|MOVE|TRIGGER) " + IdOrNone
        + "|M (?:PLAY|POWER|USE|ATTACK) " + IdOrNone + " " + IdOrNone
        + "|M (?:REFRESH|UPGRADE|FREEZE|DEATHS) -"
        + "|C (?:HERO|DISCOVER|TRINKET|QUEST) (?:" + Card + "|-) " + Card + "(?:," + Card + "){0,15}"
        + "|c " + Card
        + "|R " + Small
        + "|E " + Id + " (?:L PLAY" + At + Table + "|L HAND" + At + Offer + "|L SECRET" + At + "SPELL|B PLAY" + At + Offer + "|O PLAY" + At + Table + ")" + Tags
        + "|[XY] " + Id
        + "|Z " + Id + " " + Small
        + "|V " + Id + " " + CardTag + " " + Number
        + "|V L (?:RESOURCES|RESOURCES_USED|TEMP_RESOURCES|PLAYER_TECH_LEVEL) " + Number
        + "|L " + Small + " [L-] " + Card + " " + Small + " " + Number + " " + Number + " " + Small
        + ")$", RegexOptions.CultureInvariant)]
    private static partial Regex Line();
}
