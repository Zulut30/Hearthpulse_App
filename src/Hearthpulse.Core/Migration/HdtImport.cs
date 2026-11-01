using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Memory;
using Hearthpulse.Core.Replay;
using Hearthpulse.Core.Stats;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.Core.Migration;

/// <summary>
/// Brings a Hearthstone Deck Tracker history into Hearthpulse (<c>docs/migration.md</c>): its finished games into History
/// (<c>source = "hdt"</c>) and My Stats, its decks into the deck library. A game HDT kept a replay of is rebuilt from that
/// replay's Power.log by our own parser (the match page then works as for our games); the others from DeckStats.xml.
/// Only HDT's data files are read, never written, never its code; <c>ServerInfo</c>, tokens and config are never read.
/// </summary>
public static class HdtImport
{
    public const string Source = "hdt";

    /// <summary>A replay's log larger than this is not unpacked (real ones are tens of KB).</summary>
    private const long MaxReplayLogBytes = 64L * 1024 * 1024;

    /// <summary>HDT's data folder of an installed copy: <c>%APPDATA%\HearthstoneDeckTracker</c>.</summary>
    public static string DefaultFolder =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HearthstoneDeckTracker");

    /// <summary>What <paramref name="folder"/> holds, or null when it is no HDT data folder.</summary>
    public static MigrationFound? Scan(string folder)
    {
        if (!File.Exists(System.IO.Path.Combine(folder, "DeckStats.xml")) && !File.Exists(System.IO.Path.Combine(folder, "PlayerDecks.xml"))
            && !File.Exists(System.IO.Path.Combine(folder, "DefaultDeckStats.xml")))
        {
            return null;
        }

        try
        {
            var games = ImportableGames(folder).Count;
            var decks = HdtData.Decks(HdtData.Load(System.IO.Path.Combine(folder, "PlayerDecks.xml"))).Count(d => !d.Special);
            return new MigrationFound(MigrationSource.Hdt, folder, games, decks);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Diag.Info($"HDT data in {folder} could not be read: {ex.Message}");
            return new MigrationFound(MigrationSource.Hdt, folder, 0, 0);
        }
    }

    /// <summary>
    /// Imports the games and decks of <paramref name="folder"/>. <paramref name="replays"/> receives the replays rebuilt
    /// from HDT's replay files; <paramref name="workDir"/> holds their unpacked logs for a moment and is emptied.
    /// </summary>
    public static MigrationResult Import(string folder, HistoryStore history, ReplayStore replays, CardDb cards, string workDir,
        IProgress<MigrationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<HdtGame> games;
        IReadOnlyList<HdtDeck> decks;
        IReadOnlyList<HdtBgGame> bgGames;
        try
        {
            games = ImportableGames(folder);
            decks = HdtData.Decks(HdtData.Load(System.IO.Path.Combine(folder, "PlayerDecks.xml"))).ToList();
            bgGames = HdtData.BattlegroundsGames(HdtData.Load(System.IO.Path.Combine(folder, "BgsLastGames.xml"))).ToList();
        }
        catch (InvalidDataException ex)
        {
            Diag.Info($"HDT import stopped: {ex.Message}");
            return MigrationResult.Failed(ex.Message.Contains(" MB", StringComparison.Ordinal) ? MigrationErrors.TooLarge : MigrationErrors.Damaged);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diag.Error("HDT import could not read its files", ex);
            return MigrationResult.Failed(MigrationErrors.Access);
        }

        var byId = decks.Where(d => d.DeckId != Guid.Empty).GroupBy(d => d.DeckId).ToDictionary(g => g.Key, g => g.First());
        var store = new ReplayStore(replays.Directory) { AutoPrune = false, MaxTotalBytes = replays.MaxTotalBytes, MaxTotalFiles = replays.MaxTotalFiles };
        var work = System.IO.Path.Combine(workDir, Guid.NewGuid().ToString("N"));
        int added = 0, already = 0, skipped = 0, rebuilt = 0;
        var versionKeys = new Dictionary<Guid, HashSet<string>>();
        try
        {
            for (var i = 0; i < games.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new MigrationProgress(i, games.Count));
                var game = games[i];
                var deck = byId.GetValueOrDefault(game.DeckId);
                var record = FromReplay(folder, game, deck, cards, store, work, cancellationToken) is { } fromReplay
                    ? fromReplay
                    : RecordOf(game, deck, bgGames, cards);
                if (record is null)
                {
                    skipped++;
                    continue;
                }

                if (history.HasSameGame(record))
                {
                    // The replay file just written stays: it is the same game's, and History's row may point at it.
                    already++;
                    continue;
                }

                if (!history.Add(record))
                {
                    already++;
                    continue;
                }

                added++;
                rebuilt += record.Replay is not null ? 1 : 0;
                if (deck is not null && DeckFamilies.IsDeckGame(record))
                {
                    (versionKeys.TryGetValue(deck.DeckId, out var keys) ? keys : versionKeys[deck.DeckId] = new HashSet<string>(StringComparer.Ordinal))
                        .Add(DeckFamilies.VersionKey(record));
                }
            }

            progress?.Report(new MigrationProgress(games.Count, games.Count));
            var saved = history.SaveDecks(decks.Where(d => !d.Special && d.DeckId != Guid.Empty).Select(d => SavedDeckOf(d, games, cards)).ToList());

            // The versions of one HDT deck played in History are one deck family (named after the HDT deck).
            foreach (var (id, keys) in versionKeys.Where(v => v.Value.Count > 1))
            {
                history.MergeDecks(keys, byId[id].Name);
            }

            store.Prune();
            return new MigrationResult(added, already, skipped, saved, rebuilt);
        }
        finally
        {
            try
            {
                if (Directory.Exists(work))
                {
                    Directory.Delete(work, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Diag.Error("Deleting the HDT import's work folder failed", ex);
            }
        }
    }

    /// <summary>The games worth importing: finished, not a clone, not Mercenaries or a spectated game, each once, oldest first.</summary>
    private static List<HdtGame> ImportableGames(string folder) =>
        HdtData.Games(HdtData.Load(System.IO.Path.Combine(folder, "DeckStats.xml")))
            .Concat(HdtData.Games(HdtData.Load(System.IO.Path.Combine(folder, "DefaultDeckStats.xml"))))
            .Where(g => !g.IsClone && g.Result is "Win" or "Loss" or "Draw" && g.Start != default
                && g.GameMode is not ("Mercenaries" or "Spectator" or "None") && !g.GameType.StartsWith("GT_MERCENARIES", StringComparison.Ordinal))
            .GroupBy(g => g.GameId == Guid.Empty ? Guid.NewGuid() : g.GameId)
            .Select(g => g.First())
            .OrderBy(g => g.Start)
            .ToList();

    /// <summary>The game rebuilt from HDT's replay of it (its Power.log), or null when there is none or it does not parse.</summary>
    private static GameRecord? FromReplay(string folder, HdtGame game, HdtDeck? deck, CardDb cards, ReplayStore replays, string work,
        CancellationToken cancellationToken)
    {
        if (game.ReplayFile is not { Length: > 0 } name || name != System.IO.Path.GetFileName(name)
            || System.IO.Path.Combine(folder, "Replays", name) is var path && !File.Exists(path))
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(work);
            var log = System.IO.Path.Combine(work, "Power.log");
            using (var zip = ZipFile.OpenRead(path))
            {
                if (zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) is not { } entry || entry.Length > MaxReplayLogBytes)
                {
                    return null;
                }

                entry.ExtractToFile(log, overwrite: true);
            }

            // The log has times of day only: the session starts just before the game HDT says it started.
            var start = game.Start.LocalDateTime.AddMinutes(-1);
            var record = LogBackfill.Parse(log, start, cards, cancellationToken, replays)
                .Where(r => !r.IsAbandoned)
                .MinBy(r => Math.Abs((r.StartedAt - game.Start.LocalDateTime).TotalSeconds));
            if (record is null || Math.Abs((record.StartedAt - game.Start.LocalDateTime).TotalMinutes) > 10)
            {
                return null;
            }

            var (deckName, deckCode) = DeckOf(game, deck, cards);
            return record with
            {
                Source = Source,
                DeckName = record.IsBattlegrounds ? record.DeckName : deckName ?? record.DeckName,
                DeckCode = record.IsBattlegrounds ? record.DeckCode : deckCode ?? record.DeckCode,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Diag.Info($"HDT replay {name} not used: {ex.Message}");
            return null;
        }
    }

    /// <summary>A History row from what DeckStats.xml says about a game (no replay); null for a game that cannot be one.</summary>
    internal static GameRecord? RecordOf(HdtGame game, HdtDeck? deck, IReadOnlyList<HdtBgGame> bgGames, CardDb cards)
    {
        var gameType = game.GameType.Length > 0 ? game.GameType : game.GameMode switch
        {
            "Ranked" => "GT_RANKED",
            "Casual" => "GT_CASUAL",
            "Arena" => "GT_ARENA",
            "Brawl" => "GT_TAVERNBRAWL",
            "Friendly" => "GT_VS_FRIEND",
            "Practice" => "GT_VS_AI",
            "Battlegrounds" => "GT_BATTLEGROUNDS",
            _ => string.Empty,
        };
        if (gameType.Length == 0)
        {
            return null;
        }

        var started = DateTime.SpecifyKind(game.Start.LocalDateTime, DateTimeKind.Unspecified);
        var ended = game.End == default ? started : DateTime.SpecifyKind(game.End.LocalDateTime, DateTimeKind.Unspecified);
        var mode = GameRecords.ModeOf(gameType);
        var key = string.Create(CultureInfo.InvariantCulture, $"{started:yyyyMMddHHmmss}|{game.PlayerName}|{gameType}");
        if (mode is "Battlegrounds" or "Duos")
        {
            // BgsLastGames.xml has the same start instant for the games it kept: hero, rating, place, final board.
            var bg = bgGames.FirstOrDefault(b => Math.Abs((b.Start - game.Start).TotalSeconds) < 2);
            var place = game.Placement ?? bg?.Placement ?? 0;
            var hero = game.PlayerHeroCardId ?? bg?.Hero ?? string.Empty;
            if (hero.Length == 0 || place <= 0)
            {
                return null;
            }

            var board = bg?.Board.Where(m => m.CardId.Length > 0).Select(m => new { id = m.CardId, atk = m.Attack, hp = m.Health, gold = m.Golden }).ToList();
            return new GameRecord
            {
                Key = key,
                StartedAt = started,
                EndedAt = ended,
                Mode = mode,
                GameType = gameType,
                PlayerName = game.PlayerName,
                HeroCardId = hero,
                PlayerClass = "NEUTRAL",
                Result = place <= (mode == "Duos" ? 2 : 4) ? "Win" : "Loss",
                Placement = place,
                RatingBefore = game.BattlegroundsRating ?? bg?.Rating,
                RatingAfter = game.BattlegroundsRatingAfter ?? bg?.RatingAfter,
                Turns = (game.Turns + 1) / 2,
                FinalBoard = board is { Count: > 0 } ? JsonSerializer.Serialize(board) : null,
                AnomalyCardId = game.AnomalyDbfId is > 0 and var anomaly ? cards.Get(anomaly)?.Id : null,
                Source = Source,
            };
        }

        var playerHero = game.PlayerHeroCardId ?? DeckString.ClassHero(ClassName(game.PlayerHero)) ?? string.Empty;
        var opponentHero = game.OpponentHeroCardId ?? DeckString.ClassHero(ClassName(game.OpponentHero)) ?? string.Empty;
        var (deckName, deckCode) = DeckOf(game, deck, cards);
        var ranked = gameType == "GT_RANKED" && game.LeagueId >= 5 && (game.StarLevel > 0 || game.LegendRank > 0);
        var rank = ranked ? new RankInfo(game.Format ?? string.Empty, game.LeagueId, game.StarLevel, game.LegendRank, 0) : null;
        return new GameRecord
        {
            Key = key,
            StartedAt = started,
            EndedAt = ended,
            Mode = mode,
            GameType = gameType,
            Format = FormatType(game.Format),
            PlayerName = game.PlayerName,
            OpponentName = game.OpponentName,
            HeroCardId = playerHero,
            OpponentHeroCardId = opponentHero,
            PlayerClass = ClassOf(game.PlayerHero, playerHero, cards),
            OpponentClass = ClassOf(game.OpponentHero, opponentHero, cards),
            Result = game.Result == "Draw" ? "Tie" : game.Result,
            Turns = (game.Turns + 1) / 2,
            WentFirst = !game.Coin,
            DeckName = deckName,
            DeckCode = deckCode,
            Rank = rank?.Text,
            RankStarLevel = rank is { StarLevel: > 0 } ? rank.StarLevel : null,
            RankLegend = rank is { LegendRank: > 0 } ? rank.LegendRank : null,
            Source = Source,
        };
    }

    /// <summary>The deck a game was played with: HDT's name, and the code of the version it played (null without card data).</summary>
    private static (string? Name, string? Code) DeckOf(HdtGame game, HdtDeck? deck, CardDb cards)
    {
        if (deck is null)
        {
            return (game.DeckName is { Length: > 0 } and not "none" ? game.DeckName : null, null);
        }

        var code = DeckString.Encode(deck.CardsOf(game.DeckVersion), ClassName(deck.Class), game.Format ?? "Wild", cards);
        return (deck.Name, code);
    }

    private static SavedDeck SavedDeckOf(HdtDeck deck, IReadOnlyList<HdtGame> games, CardDb cards)
    {
        // HDT keeps no format: the one its last game was played in, else Wild (a Wild code takes any card).
        var format = games.Where(g => g.DeckId == deck.DeckId && g.Format is { Length: > 0 }).MaxBy(g => g.Start)?.Format ?? "Wild";
        return new SavedDeck(
            Source,
            deck.DeckId.ToString("D"),
            deck.Name,
            ClassName(deck.Class),
            DeckString.Encode(deck.Cards, ClassName(deck.Class), format, cards),
            FormatType(format),
            deck.Tags,
            deck.Note,
            deck.Archived,
            (deck.LastEdited ?? DateTimeOffset.UtcNow).UtcDateTime);
    }

    /// <summary>HDT's class name (<c>DemonHunter</c>, <c>Deathknight</c>) as ours (<c>DEMONHUNTER</c>); a hero name is left as it is.</summary>
    private static string ClassName(string hdtClass) => hdtClass.ToUpperInvariant() switch
    {
        var upper and ("WARRIOR" or "SHAMAN" or "ROGUE" or "PALADIN" or "HUNTER" or "DRUID" or "WARLOCK" or "MAGE" or "PRIEST"
            or "DEMONHUNTER" or "DEATHKNIGHT") => upper,
        _ => hdtClass,
    };

    private static string ClassOf(string hdtHero, string heroCardId, CardDb cards) =>
        DeckString.ClassHero(ClassName(hdtHero)) is not null ? ClassName(hdtHero) : cards.Get(heroCardId)?.CardClass ?? string.Empty;

    private static string FormatType(string? format) => format switch
    {
        "Standard" => "FT_STANDARD",
        "Wild" => "FT_WILD",
        "Classic" => "FT_CLASSIC",
        "Twist" => "FT_TWIST",
        _ => string.Empty,
    };
}

/// <summary>Why a migration stopped (the wizard shows <c>Migration.Error.*</c>).</summary>
public static class MigrationErrors
{
    public const string Damaged = "Damaged";
    public const string TooLarge = "TooLarge";
    public const string Access = "Access";
    public const string Missing = "Missing";
}
