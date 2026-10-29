using Hearthpulse.Core.Power;

namespace Hearthpulse.Core.Memory;

public sealed partial class HearthstoneMemory
{
    /// <summary>
    /// Names of the Battlegrounds lobby's players by player id (the <c>PLAYER_ID</c> of their leaderboard hero), as the
    /// client shows them on a hovered leaderboard tile. Power.log names only the local player and the Innkeeper, so the
    /// names come from the client's per-player info: <c>GameState.s_instance.m_playerInfoMap</c> (Map&lt;int,
    /// SharedPlayerInfo&gt; keyed by player id), each entry's <c>m_name</c>. Read only, like every other value here; read
    /// only while the opponent MMR setting is on (docs/bg-opponent-mmr.md). Null when the map is not there (not in a
    /// game, or the client moved these fields). Not yet checked on a live game.
    /// </summary>
    public IReadOnlyDictionary<int, string>? GetBattlegroundsLobbyNames() => Safe<IReadOnlyDictionary<int, string>?>(quiet: true, read: rt =>
    {
        var state = GameStateInstance(rt);
        var mapField = Field(state.Class, "playerInfoMap", ["m_playerInfoMap"]);
        var map = mapField is null ? default : state.Object(mapField);
        if (map.IsNull)
        {
            return null;
        }

        var names = new Dictionary<int, string>();
        foreach (var (playerId, info) in map.MapIntEntries())
        {
            var nameField = Field(info.Class, "sharedPlayerName", ["m_name"]);
            if (playerId > 0 && nameField is not null && LobbyName(info.String(nameField)) is { } name)
            {
                names.TryAdd(playerId, name);
            }

            if (names.Count >= 16)
            {
                break;
            }
        }

        return names;
    });

    /// <summary>
    /// A lobby name as the leaderboard spells it: without a "#1234" BattleTag number, trimmed. Null for no name, the
    /// client's placeholder for a player it has no name for yet, or something too long to be a name.
    /// </summary>
    internal static string? LobbyName(string? raw)
    {
        if (raw is null)
        {
            return null;
        }

        var hash = raw.LastIndexOf('#');
        var name = (hash > 0 && hash < raw.Length - 1 && IsDigits(raw.AsSpan(hash + 1)) ? raw[..hash] : raw).Trim();
        return name.Length is 0 or > 64 || name == GameState.UnknownPlayerName ? null : name;
    }

    private static bool IsDigits(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }
}
