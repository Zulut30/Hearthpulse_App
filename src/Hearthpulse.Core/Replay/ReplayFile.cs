using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hearthpulse.Core.Replay;

/// <summary>
/// What a replay file says about the game besides its events; the rest (heroes, result, deck...) is in its history row.
/// </summary>
public sealed record ReplayHeader
{
    /// <summary>
    /// "constructed" (event lines follow, then the post-game part) or "battlegrounds" (event lines follow, then one
    /// <c>G</c> line of <see cref="BattlegroundsReplayData"/>; a format 3 file has only that line, as bare JSON).
    /// </summary>
    public string Kind { get; init; } = ReplayFile.Constructed;

    /// <summary>The game the file is of (format 4); null in older files.</summary>
    public ReplaySummary? Summary { get; init; }

    /// <summary>The local player's rank at game start, as the client showed it ("Diamond 4", "Legend 1,204").</summary>
    public string? LocalRank { get; init; }

    /// <summary>The opponent's rank medal the client shows at game start, when it was read.</summary>
    public string? OpponentRank { get; init; }

    /// <summary>
    /// The game hit the event cap (<see cref="ReplayRecorder.MaxEvents"/>, <see cref="BattlegroundsReplayRecorder.MaxEvents"/>)
    /// or a combat hit <see cref="BattlegroundsReplayRecorder.MaxCombatSteps"/>; the replay stops there.
    /// </summary>
    public bool Truncated { get; init; }

    /// <summary>Number of event lines.</summary>
    public int Events { get; init; }

    /// <summary>The format the file was read as (2, 3 or 4); not written.</summary>
    [JsonIgnore]
    public int Version { get; init; } = ReplayFile.Version;
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
    /// The format line. Version 4 adds the header's <see cref="ReplayHeader.Summary"/> and records Battlegrounds games
    /// step by step (event lines, then the <c>G</c> line of rounds and lobby); its Constructed events and post-game part
    /// are those of version 3. Version 3 added the post-game part after the events (<c>N</c>: the game entity of an
    /// opponent card back, <c>K</c>: what the log revealed about an opponent entity by the end of the game). Versions 3
    /// and 2 (the same events, no post-game part) are still read. Version 1 gave each deck card an id in the events
    /// themselves, so a v1 file is never read and is deleted when met.
    /// </summary>
    public const string Magic = "HPR4";

    /// <summary>The number of the format written (<see cref="Magic"/>).</summary>
    public const int Version = 4;

    /// <summary>The third version's format line: read, never written (Battlegrounds games keep only rounds and lobby).</summary>
    public const string Magic3 = "HPR3";

    /// <summary>The second version's format line: read, never written (its games show without retroactive reveals).</summary>
    public const string PreviousMagic = "HPR2";

    /// <summary>The first version's format line (see <see cref="Magic"/>).</summary>
    public const string OldMagic = "HPR1";
    public const string Constructed = "constructed";
    public const string Battlegrounds = "battlegrounds";

    /// <summary>The start of the line holding a format 4 Battlegrounds file's <see cref="BattlegroundsReplayData"/>.</summary>
    public const string DataLine = "G ";

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

    /// <summary>A Battlegrounds file: <paramref name="events"/> (none for a game recorded without steps), then the rounds and lobby.</summary>
    public static string WriteBattlegrounds(ReplayHeader header, BattlegroundsReplayData data, IReadOnlyList<string>? events = null) =>
        Write(header with { Kind = Battlegrounds, Events = events?.Count ?? 0 }, (events ?? []).Append(DataLine + JsonSerializer.Serialize(data, Json)));

    /// <summary>Splits a replay into its header and body lines; null when it is not a replay this version reads.</summary>
    public static (ReplayHeader Header, IReadOnlyList<string> Body)? Read(string text)
    {
        var lines = text.Split('\n');
        var version = lines[0].TrimEnd('\r') switch
        {
            Magic => Version,
            Magic3 => 3,
            PreviousMagic => 2,
            _ => 0,
        };
        if (lines.Length < 2 || version == 0)
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
        return (header with { Version = version }, body);
    }

    /// <summary>The rounds and lobby of a Battlegrounds replay (format 4: the <c>G</c> line after the events; 3: the only line), or null.</summary>
    public static BattlegroundsReplayData? ReadBattlegrounds(IReadOnlyList<string> body)
    {
        var line = body.Count > 0 && body[^1].StartsWith(DataLine, StringComparison.Ordinal) ? body[^1][DataLine.Length..]
            : body.Count > 0 && body[0].StartsWith('{') ? body[0]
            : null;
        if (line is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<BattlegroundsReplayData>(line, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
