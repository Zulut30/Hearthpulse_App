using System.Globalization;
using System.Text.Json;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Memory;
using Hearthpulse.Core.Tracking;
using Microsoft.Data.Sqlite;

namespace Hearthpulse.Core.Migration;

/// <summary>
/// Brings Firestone's match history into Hearthpulse when a copy of it is on this PC (<c>docs/migration.md</c>): the
/// desktop build's <c>FirestoneDB.sqlite</c> (table <c>matchHistory</c>, one JSON record per row) or the Overwolf build's
/// older <c>user-match-history.json</c>. Firestone's own source of truth is its server: an Overwolf install keeps at
/// most a cache in Chromium's IndexedDB, which is not read (<see cref="MigrationFound.ServerOnly"/>). Only those two
/// files are read, never written; never the settings or the session token.
/// </summary>
public static class FirestoneImport
{
    public const string Source = "firestone";

    public const long MaxJsonBytes = 64L * 1024 * 1024;
    public const long MaxDatabaseBytes = 500L * 1024 * 1024;

    private const string OverwolfId = "lnknbakkpommmjjdnelmfbjjdbocfpnpbkijjnob";

    /// <summary>The desktop build's history database.</summary>
    public static string DatabasePath => Path.Combine(AppData, "Firestone", "FirestoneDB.sqlite");

    /// <summary>The Overwolf build's older history file.</summary>
    public static string JsonPath => Path.Combine(AppData, "Overwolf", OverwolfId, "user-match-history.json");

    /// <summary>The Overwolf build's extension folder: installed, its history on the server.</summary>
    public static string OverwolfExtension => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Overwolf", "Extensions", OverwolfId);

    private static string AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    /// <summary>What this PC has of Firestone: a readable history (database first), an install without one, or nothing.</summary>
    public static MigrationFound? Scan(string? database = null, string? json = null, string? extension = null)
    {
        // The database is not copied or opened only to count it (up to 500 MB): the import counts it.
        if ((database ?? DatabasePath) is var db && File.Exists(db))
        {
            return new MigrationFound(MigrationSource.Firestone, db, 0, 0, Counted: false);
        }

        if ((json ?? JsonPath) is var file && File.Exists(file))
        {
            try
            {
                return new MigrationFound(MigrationSource.Firestone, file, Records(file, MigrationScan.WorkDir).Count(r => RecordOf(r, CardDb.Empty) is not null), 0);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            {
                Diag.Info($"Firestone history {Path.GetFileName(file)} could not be read: {ex.GetType().Name}");
                return new MigrationFound(MigrationSource.Firestone, file, 0, 0);
            }
        }

        var installed = extension ?? OverwolfExtension;
        return Directory.Exists(installed) ? new MigrationFound(MigrationSource.Firestone, installed, 0, 0, ServerOnly: true) : null;
    }

    /// <summary>Imports the games of a Firestone history file (<see cref="Scan"/>'s path) into History.</summary>
    public static MigrationResult Import(string path, HistoryStore history, CardDb cards, string workDir,
        IProgress<MigrationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        List<JsonElement> records;
        try
        {
            records = Records(path, workDir).ToList();
        }
        catch (InvalidDataException ex)
        {
            Diag.Info($"Firestone import stopped: {ex.GetType().Name}");
            return MigrationResult.Failed(ex.Message.Contains("too large", StringComparison.Ordinal) ? MigrationErrors.TooLarge : MigrationErrors.Damaged);
        }
        catch (Exception ex) when (ex is JsonException or SqliteException)
        {
            Diag.Info($"Firestone import stopped: {ex.GetType().Name}");
            return MigrationResult.Failed(MigrationErrors.Damaged);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diag.Error("Firestone import could not read its file", ex);
            return MigrationResult.Failed(MigrationErrors.Access);
        }

        int added = 0, already = 0, skipped = 0;
        var games = records.Select(r => RecordOf(r, cards)).ToList();
        for (var i = 0; i < games.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new MigrationProgress(i, games.Count));
            if (games[i] is not { } game)
            {
                skipped++;
            }
            else if (!MigrationRows.Add(history, game))
            {
                already++;
            }
            else
            {
                added++;
            }
        }

        progress?.Report(new MigrationProgress(games.Count, games.Count));
        return new MigrationResult(added, already, skipped, 0, 0);
    }

    /// <summary>The history records of a file: the database's JSON rows (read from a copy, as Firestone may hold it), or the JSON array.</summary>
    private static IEnumerable<JsonElement> Records(string path, string workDir)
    {
        var size = new FileInfo(path).Length;
        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            if (size > MaxJsonBytes)
            {
                throw new InvalidDataException($"{Path.GetFileName(path)} is too large");
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 64 });
            return document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().Select(e => e.Clone()).ToList()
                : throw new InvalidDataException($"{Path.GetFileName(path)} is no list of games");
        }

        if (size > MaxDatabaseBytes)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} is too large");
        }

        // A copy with its write-ahead log: the original is never opened by SQLite, so nothing of Firestone's changes.
        var copy = Path.Combine(workDir, $"firestone-{Guid.NewGuid():N}.sqlite");
        Directory.CreateDirectory(workDir);
        try
        {
            foreach (var suffix in new[] { string.Empty, "-wal" })
            {
                if (File.Exists(path + suffix))
                {
                    using var from = new FileStream(path + suffix, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var to = File.Create(copy + suffix);
                    from.CopyTo(to);
                }
            }

            var rows = new List<JsonElement>();
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = copy, Pooling = false }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT data FROM matchHistory;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    if (!reader.IsDBNull(0) && reader.GetString(0) is { Length: > 0 and < 1_000_000 } json)
                    {
                        using var row = JsonDocument.Parse(json);
                        rows.Add(row.RootElement.Clone());
                    }
                }
            }

            return rows;
        }
        finally
        {
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                try
                {
                    File.Delete(copy + suffix);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Diag.Debug($"Firestone copy {Path.GetFileName(copy)}{suffix} not deleted: {ex.GetType().Name}");
                }
            }
        }
    }

    /// <summary>A History row from one Firestone record, or null for a game that cannot be one (Mercenaries, no result).</summary>
    internal static GameRecord? RecordOf(JsonElement r, CardDb cards)
    {
        // A record of anybody's file: the time must be a number of milliseconds between 2014 and 2100.
        if (r.ValueKind != JsonValueKind.Object || GameTypeOf(Text(r, "gameMode")) is not { } gameType
            || !r.TryGetProperty("creationTimestamp", out var created) || created.ValueKind != JsonValueKind.Number
            || !created.TryGetInt64(out var ms) || ms is < 1_388_534_400_000 or > 4_102_444_800_000)
        {
            return null;
        }

        var seconds = Int(r, "gameDurationSeconds") ?? 0;
        var ended = DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
        var started = DateTime.SpecifyKind(ended.AddSeconds(-Math.Max(0, seconds)), DateTimeKind.Unspecified);
        ended = DateTime.SpecifyKind(ended, DateTimeKind.Unspecified);
        var player = Text(r, "playerName");
        var key = string.Create(CultureInfo.InvariantCulture, $"{started:yyyyMMddHHmmss}|{player}|{gameType}");
        var mode = GameRecords.ModeOf(gameType);
        var turns = Int(r, "gameDurationTurns") ?? 0;
        if (mode is "Battlegrounds" or "Duos")
        {
            var place = int.TryParse(Text(r, "additionalResult"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : 0;
            if (place <= 0 || Text(r, "playerCardId") is not { Length: > 0 } hero)
            {
                return null;
            }

            return new GameRecord
            {
                Key = key,
                StartedAt = started,
                EndedAt = ended,
                Mode = mode,
                GameType = gameType,
                PlayerName = player,
                HeroCardId = hero,
                PlayerClass = "NEUTRAL",
                Result = place <= (mode == "Duos" ? 2 : 4) ? "Win" : "Loss",
                Placement = place,
                RatingBefore = Number(Text(r, "playerRank")),
                RatingAfter = Number(Text(r, "newPlayerRank")),
                Turns = turns,
                Source = Source,
            };
        }

        var result = Text(r, "result") switch
        {
            "won" => "Win",
            "lost" => "Loss",
            "tied" => "Tie",
            _ => null,
        };
        if (result is null)
        {
            return null;
        }

        var heroId = Text(r, "playerCardId");
        var opponentId = Text(r, "opponentCardId");
        var code = Text(r, "playerDecklist") is { Length: > 0 } list && DeckString.TryDecode(list) is not null ? list : null;
        var legend = Text(r, "playerRank") is { } rank && rank.StartsWith("legend-", StringComparison.Ordinal) ? Number(rank[7..]) : null;
        return new GameRecord
        {
            Key = key,
            StartedAt = started,
            EndedAt = ended,
            Mode = mode,
            GameType = gameType,
            Format = Text(r, "gameFormat") is { Length: > 0 } format and not "unknown" ? "FT_" + format.ToUpperInvariant() : string.Empty,
            PlayerName = player,
            OpponentName = Text(r, "opponentName"),
            HeroCardId = heroId,
            OpponentHeroCardId = opponentId,
            PlayerClass = Text(r, "playerClass").ToUpperInvariant() is { Length: > 0 } own ? own : cards.Get(heroId)?.CardClass ?? string.Empty,
            OpponentClass = Text(r, "opponentClass").ToUpperInvariant() is { Length: > 0 } other ? other : cards.Get(opponentId)?.CardClass ?? string.Empty,
            Result = result,
            // Firestone counts turns as the game does; History keeps each player's turns.
            Turns = (turns + 1) / 2,
            WentFirst = Text(r, "coinPlay") switch { "play" => true, "coin" => false, _ => null },
            DeckName = Text(r, "playerDeckName") is { Length: > 0 } name ? name : null,
            DeckCode = code,
            Rank = legend is { } number ? new RankInfo(string.Empty, 5, 51, number, 0).Text : null,
            RankLegend = legend,
            Source = Source,
        };
    }

    /// <summary>Firestone's game mode as the client's game type; null for one History does not keep (Mercenaries, unknown).</summary>
    private static string? GameTypeOf(string mode) => mode switch
    {
        "ranked" => "GT_RANKED",
        "casual" => "GT_CASUAL",
        "friendly" => "GT_VS_FRIEND",
        "practice" => "GT_VS_AI",
        "arena" => "GT_ARENA",
        "arena-underground" => "GT_UNDERGROUND_ARENA",
        "tavern-brawl" => "GT_TAVERNBRAWL",
        "battlegrounds" => "GT_BATTLEGROUNDS",
        "battlegrounds-friendly" => "GT_BATTLEGROUNDS_FRIENDLY",
        "battlegrounds-duo" => "GT_BATTLEGROUNDS_DUO",
        _ => null,
    };

    private static string Text(JsonElement r, string name) =>
        r.ValueKind == JsonValueKind.Object && r.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty
        : r.ValueKind == JsonValueKind.Object && r.TryGetProperty(name, out var number) && number.ValueKind == JsonValueKind.Number ? number.GetRawText()
        : string.Empty;

    private static int? Int(JsonElement r, string name) =>
        r.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) && n is >= 0 and < 1_000_000 ? n : null;

    private static int? Number(string text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;
}
