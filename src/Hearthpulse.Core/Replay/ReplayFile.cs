using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hearthpulse.Core.Replay;

/// <summary>
/// What a replay file says about the game besides its events; the rest (heroes, result, deck...) is in its history row.
/// </summary>
public sealed record ReplayHeader
{
    /// <summary>"constructed" (event lines follow) or "battlegrounds" (one <see cref="BattlegroundsReplayData"/> line follows).</summary>
    public string Kind { get; init; } = ReplayFile.Constructed;

    /// <summary>The local player's rank at game start, as the client showed it ("Diamond 4", "Legend 1,204").</summary>
    public string? LocalRank { get; init; }

    /// <summary>The opponent's rank medal the client shows at game start, when it was read.</summary>
    public string? OpponentRank { get; init; }

    /// <summary>The game hit <see cref="ReplayRecorder.MaxEvents"/>; the replay stops there.</summary>
    public bool Truncated { get; init; }

    /// <summary>Number of event lines.</summary>
    public int Events { get; init; }
}

/// <summary>One Battlegrounds round: the board that fought, the combat opponent's board as fought, the tavern offers.</summary>
public sealed record BattlegroundsRound
{
    public int Turn { get; init; }

    public int Tier { get; init; }

    /// <summary>Health plus armor when the combat started.</summary>
    public int Health { get; init; }

    public IReadOnlyList<ReplayMinion> Board { get; init; } = [];

    public string? OpponentHero { get; init; }

    public IReadOnlyList<ReplayMinion> OpponentBoard { get; init; } = [];

    /// <summary>Every card Bob offered during the round (refreshes included), in the order seen.</summary>
    public IReadOnlyList<string> Shop { get; init; } = [];

    /// <summary>Win / Loss / Tie from the player's side, null when the round had no combat result.</summary>
    public string? Result { get; init; }

    public int DamageTaken { get; init; }

    public int DamageDealt { get; init; }
}

/// <summary>A minion as shown on a board.</summary>
public sealed record ReplayMinion(string CardId, int Attack, int Health, bool Golden);

/// <summary>A hero of the lobby and its final (or last known) place.</summary>
public sealed record BattlegroundsLobbyHero(string HeroCardId, int Place, bool Local);

/// <summary>A Battlegrounds game's replay: its rounds and the lobby.</summary>
public sealed record BattlegroundsReplayData
{
    public IReadOnlyList<BattlegroundsRound> Rounds { get; init; } = [];

    public IReadOnlyList<BattlegroundsLobbyHero> Lobby { get; init; } = [];
}

/// <summary>The text form of a replay: a magic line, the header as JSON, then the body (<c>docs/match-replay.md</c>).</summary>
public static class ReplayFile
{
    /// <summary>
    /// The format line. Version 2 keeps a deck as a count (<c>D L n</c>); version 1 gave each deck card an id, which
    /// linked a card sent back into a deck with its later draw, so a v1 file is never read and is deleted when met.
    /// </summary>
    public const string Magic = "HPR2";

    /// <summary>The first version's format line (see <see cref="Magic"/>).</summary>
    public const string OldMagic = "HPR1";
    public const string Constructed = "constructed";
    public const string Battlegrounds = "battlegrounds";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Write(ReplayHeader header, IEnumerable<string> events)
    {
        var text = new StringBuilder();
        text.Append(Magic).Append('\n');
        text.Append(JsonSerializer.Serialize(header, Json)).Append('\n');
        foreach (var line in events)
        {
            text.Append(line).Append('\n');
        }

        return text.ToString();
    }

    public static string WriteBattlegrounds(ReplayHeader header, BattlegroundsReplayData data) =>
        Write(header with { Kind = Battlegrounds, Events = 0 }, [JsonSerializer.Serialize(data, Json)]);

    /// <summary>Splits a replay into its header and body lines; null when it is not a replay this version reads.</summary>
    public static (ReplayHeader Header, IReadOnlyList<string> Body)? Read(string text)
    {
        var lines = text.Split('\n');
        if (lines.Length < 2 || lines[0].TrimEnd('\r') != Magic)
        {
            return null;
        }

        ReplayHeader? header;
        try
        {
            header = JsonSerializer.Deserialize<ReplayHeader>(lines[1], Json);
        }
        catch (JsonException)
        {
            return null;
        }

        if (header is null)
        {
            return null;
        }

        var body = lines.Skip(2).Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
        return (header, body);
    }

    /// <summary>The Battlegrounds body of a replay, or null.</summary>
    public static BattlegroundsReplayData? ReadBattlegrounds(IReadOnlyList<string> body)
    {
        if (body.Count == 0)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<BattlegroundsReplayData>(body[0], Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
