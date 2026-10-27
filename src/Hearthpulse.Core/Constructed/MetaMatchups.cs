namespace Hearthpulse.Core.Constructed;

/// <summary>One archetype of the matchup matrix. <see cref="Key"/> is the API's (English) name, <see cref="Name"/> the shown one.</summary>
public sealed record MatchupArchetype(string Key, string Name, string Class, int Games, double WinRate, double Popularity);

/// <summary>How <see cref="Archetype"/> did against <see cref="Opponent"/> (both <see cref="MatchupArchetype.Key"/>s); rates as fractions.</summary>
public sealed record MatchupCell(string Archetype, string Opponent, double WinRate, int Games);

/// <summary>
/// The most played archetypes of a format against each other (HSGuru's matchup matrix through the Hearthpulse data
/// API), for the heat map of the Standard and Wild pages.
/// </summary>
public sealed class MetaMatchups
{
    private readonly Dictionary<(string, string), MatchupCell> _cells;

    public MetaMatchups(IReadOnlyList<MatchupArchetype> archetypes, IEnumerable<MatchupCell> cells, int games)
    {
        Archetypes = archetypes;
        Games = games;
        _cells = new Dictionary<(string, string), MatchupCell>();
        foreach (var cell in cells)
        {
            _cells[(cell.Archetype.ToUpperInvariant(), cell.Opponent.ToUpperInvariant())] = cell;
        }
    }

    public static MetaMatchups Empty { get; } = new([], [], 0);

    /// <summary>Most played first.</summary>
    public IReadOnlyList<MatchupArchetype> Archetypes { get; }

    /// <summary>Games behind the matrix.</summary>
    public int Games { get; }

    /// <summary>The cell of an archetype against an opponent (API keys, any case); null when the source has no games for it.</summary>
    public MatchupCell? Cell(string archetype, string opponent) =>
        _cells.GetValueOrDefault((archetype.ToUpperInvariant(), opponent.ToUpperInvariant()));
}
