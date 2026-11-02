using System.Globalization;
using Hearthpulse.Core.Arena;
using Microsoft.Data.Sqlite;

namespace Hearthpulse.Core.Data;

public sealed partial class HistoryStore
{
    /// <summary>The rewards the player entered for Arena runs, by run key (<see cref="Stats.ArenaRun.Key"/>).</summary>
    private static void CreateArenaRewardTables(SqliteConnection connection) => Execute(connection, """
        CREATE TABLE IF NOT EXISTS arena_rewards (
            run_key TEXT PRIMARY KEY,
            gold INTEGER NOT NULL DEFAULT 0,
            dust INTEGER NOT NULL DEFAULT 0,
            packs INTEGER NOT NULL DEFAULT 0,
            tickets INTEGER NOT NULL DEFAULT 0,
            cards INTEGER NOT NULL DEFAULT 0,
            edited_at TEXT NOT NULL
        );
        """);

    /// <summary>Every run's entered rewards, by run key.</summary>
    public IReadOnlyDictionary<string, ArenaRewards> ArenaRewards()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT run_key, gold, dust, packs, tickets, cards FROM arena_rewards;";
            using var reader = command.ExecuteReader();
            var rewards = new Dictionary<string, ArenaRewards>(StringComparer.Ordinal);
            while (reader.Read())
            {
                rewards[reader.GetString(0)] = new ArenaRewards(reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5));
            }

            return rewards;
        }
    }

    /// <summary>Saves a run's rewards; none (or all zero) clears them.</summary>
    /// <exception cref="ArgumentException">A value is below 0 or above <see cref="Arena.ArenaRewards.MaxValue"/>.</exception>
    public void SetArenaRewards(string runKey, ArenaRewards? rewards)
    {
        ArgumentException.ThrowIfNullOrEmpty(runKey);
        if (rewards is { IsValid: false })
        {
            throw new ArgumentException("Arena rewards out of range.", nameof(rewards));
        }

        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.Parameters.AddWithValue("$key", runKey);
            if (rewards is null || rewards.IsEmpty)
            {
                command.CommandText = "DELETE FROM arena_rewards WHERE run_key = $key;";
            }
            else
            {
                command.CommandText = """
                    INSERT INTO arena_rewards (run_key, gold, dust, packs, tickets, cards, edited_at)
                    VALUES ($key, $gold, $dust, $packs, $tickets, $cards, $edited)
                    ON CONFLICT (run_key) DO UPDATE SET gold = $gold, dust = $dust, packs = $packs, tickets = $tickets,
                        cards = $cards, edited_at = $edited;
                    """;
                command.Parameters.AddWithValue("$gold", rewards.Gold);
                command.Parameters.AddWithValue("$dust", rewards.Dust);
                command.Parameters.AddWithValue("$packs", rewards.Packs);
                command.Parameters.AddWithValue("$tickets", rewards.Tickets);
                command.Parameters.AddWithValue("$cards", rewards.Cards);
                command.Parameters.AddWithValue("$edited", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            }

            command.ExecuteNonQuery();
        }
    }
}
