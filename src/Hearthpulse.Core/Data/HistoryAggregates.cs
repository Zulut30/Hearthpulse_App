using System.Globalization;
using Hearthpulse.Core.Memory;
using Microsoft.Data.Sqlite;

namespace Hearthpulse.Core.Data;

public sealed record HeroAggregate(string HeroCardId, int Games, double AveragePlace, double Top4Rate, double WinRate, int? NetRating);

public sealed record ClassAggregate(string PlayerClass, string Mode, int Games, int Wins, int Losses)
{
    // Ties and unknown results count in Games but not in the rate, as for decks and matchups.
    public double WinRate => Wins + Losses == 0 ? 0 : (double)Wins / (Wins + Losses);
}

public sealed record DeckAggregate(string DeckName, string? DeckCode, string PlayerClass, int Games, int Wins, int Losses, DateTime LastPlayed)
{
    public double WinRate => Wins + Losses == 0 ? 0 : (double)Wins / (Wins + Losses);
}

public sealed record MatchupAggregate(string OpponentClass, int Games, int Wins, int Losses)
{
    public double WinRate => Wins + Losses == 0 ? 0 : (double)Wins / (Wins + Losses);
}

public sealed record RatingPoint(DateTime At, int Rating);
