using System.Globalization;

namespace Hearthpulse.Core.Data;

public sealed partial class HistoryStore
{
    /// <summary>
    /// How many times each of the player's own deck cards was played from hand in games that started in
    /// [<paramref name="from"/>, <paramref name="to"/>) (the <c>cards_played</c> column, Constructed only), for the year in
    /// review. Reads only that column, one row at a time.
    /// </summary>
    public IReadOnlyDictionary<string, int> PlayedCardCounts(DateTime from, DateTime to)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT cards_played FROM games WHERE cards_played IS NOT NULL AND started_at >= $from AND started_at < $to;";
            command.Parameters.AddWithValue("$from", from.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$to", to.ToString("O", CultureInfo.InvariantCulture));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                foreach (var card in GameCards.Turns(reader.GetString(0)))
                {
                    counts[card.CardId] = counts.GetValueOrDefault(card.CardId) + 1;
                }
            }
        }

        return counts;
    }
}
