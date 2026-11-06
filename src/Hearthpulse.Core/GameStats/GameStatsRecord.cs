using System.Text.Json.Nodes;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.CardPool;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Pool;
using Hearthpulse.Core.Replay;

namespace Hearthpulse.Core.GameStats;

/// <summary>
/// One finished game as a game record for Hearthpulse's own statistics (docs/game-stats.md, ADR 0011): mode, format,
/// rank tier, classes, deck code, result, the mulligan, every card played with its turn and mana, every Discover-style
/// choice, the cards that came into hand and the Excavate treasures; for Battlegrounds the hero pick, lobby, tier-ups,
/// buys, sells, picks, trinkets, quests, final board and placement.
/// </summary>
/// <remarks>
/// Only what the local player saw during the game (ADR 0005): a Constructed replay is read without its post-game part
/// (<see cref="MatchReplay.ParseSeen"/>), so an opponent card is in the record only once the game showed it. No name,
/// BattleTag, account, deck name or legend number: the rank is its league, the rating is rounded down to 500.
/// </remarks>
public static class GameStatsRecord
{
    /// <summary>The record's version (the server's <c>v</c>).</summary>
    public const int Version = 1;

    /// <summary>Lists are cut at the server's limits (server/pool/src/events.js).</summary>
    internal const int MaxPlays = 400;

    internal const int MaxOpponentPlays = 200;

    internal const int MaxChoices = 120;

    internal const int MaxDraws = 300;

    internal const int MaxTurn = 200;

    private static readonly HashSet<string> Treasures = Excavate.Treasures.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);

    /// <summary>The server's mode for a history mode; null for a mode that is not sent (Friendly, Practice, Mercenaries...).</summary>
    public static string? ModeOf(string mode) => mode switch
    {
        "Ranked" => "ranked",
        "Casual" => "casual",
        "Arena" => "arena",
        "Brawl" => "brawl",
        "Battlegrounds" => "bg",
        "Duos" => "duos",
        _ => null,
    };

    internal static string? FormatOf(string format) => format switch
    {
        "FT_STANDARD" => "standard",
        "FT_WILD" => "wild",
        "FT_CLASSIC" => "classic",
        "FT_TWIST" => "twist",
        _ => null,
    };

    private static string? ResultOf(string result) => result switch
    {
        "Win" => "W",
        "Loss" => "L",
        "Tie" => "T",
        _ => null,
    };

    /// <summary>The league of a stored rank ("Diamond 4" → diamond, "Legend 1,204" → legend); null without one.</summary>
    internal static string? RankTier(string? rank)
    {
        var league = rank?.Split(' ', 2)[0].ToLowerInvariant();
        return league is "bronze" or "silver" or "gold" or "platinum" or "diamond" or "legend" ? league : null;
    }

    /// <summary>The kind of a Constructed choice from its source card's mechanics.</summary>
    internal static string KindOf(IReadOnlyList<string> mechanics) =>
        mechanics.Contains("DISCOVER") ? "discover"
        : mechanics.Contains("ADAPT") ? "adapt"
        : mechanics.Contains("CHOOSE_ONE") ? "chooseone"
        : mechanics.Contains("DREDGE") ? "dredge"
        : mechanics.Contains(Excavate.Mechanic) ? "excavate"
        : "choice";

    /// <summary>
    /// The record of a finished game, or null for a game that is not sent: another mode, no result (abandoned), or a
    /// Battlegrounds game without its placement.
    /// </summary>
    /// <param name="game">The game's history row.</param>
    /// <param name="id">The record id (<see cref="GameStatsClient.RecordId"/>).</param>
    /// <param name="replayText">The game's replay file (constructed or battlegrounds), when it was saved.</param>
    /// <param name="pool">For Battlegrounds: the game as the stats pool gets it (hero pick, lobby, picks, fights).</param>
    /// <param name="cards">Card data: hero skins to base heroes, choice kinds, deck cards (none: left out).</param>
    /// <param name="appVersion">The app's version, x.y.z.</param>
    public static JsonObject? Build(GameRecord game, string id, string? replayText, PoolGame? pool, CardDb? cards, string? appVersion)
    {
        if (ModeOf(game.Mode) is not { } mode || ResultOf(game.Result) is not { } result)
        {
            return null;
        }

        var record = new JsonObject
        {
            ["id"] = id,
            ["day"] = Day(game.StartedAt),
            ["mode"] = mode,
            ["res"] = result,
            ["turns"] = Math.Clamp(game.Turns, 0, MaxTurn),
            ["secs"] = Math.Clamp((int)(game.EndedAt - game.StartedAt).TotalSeconds, 0, 6 * 3600),
        };
        if (appVersion is { Length: > 0 })
        {
            record["app"] = appVersion;
        }

        if (mode is "bg" or "duos")
        {
            return Battlegrounds(game, pool, replayText, cards) is { } bg ? With(record, "bg", bg) : null;
        }

        if (FormatOf(game.Format) is { } format)
        {
            record["fmt"] = format;
        }

        if (RankTier(game.Rank) is { } rank && mode == "ranked")
        {
            record["rank"] = rank;
        }

        Put(record, "cls", game.PlayerClass);
        Put(record, "ocls", game.OpponentClass);
        Put(record, "hero", cards?.BaseHeroId(game.HeroCardId) ?? game.HeroCardId);
        Put(record, "deck", game.DeckCode);
        Put(record, "oarch", game.OpponentArchetype);
        if (game.WentFirst is { } first)
        {
            record["first"] = first;
        }

        if (replayText is not null && MatchReplay.ParseSeen(replayText) is { } replay)
        {
            Constructed(record, replay, DeckCards(game.DeckCode, cards), cards);
        }

        return record;
    }

    private static JsonObject With(JsonObject record, string key, JsonNode value)
    {
        record[key] = value;
        return record;
    }

    private static void Put(JsonObject record, string key, string? value)
    {
        if (value is { Length: > 0 })
        {
            record[key] = value;
        }
    }

    /// <summary>The card ids of the deck the game was played with, or null when it is not known.</summary>
    private static HashSet<string>? DeckCards(string? code, CardDb? cards)
    {
        if (code is not { Length: > 0 } || cards is null || DeckString.TryDecode(code) is not { } deck)
        {
            return null;
        }

        return deck.Cards.Keys.Select(dbf => cards.Get(dbf)?.Id).OfType<string>().ToHashSet(StringComparer.Ordinal);
    }

    private static void Constructed(JsonObject record, MatchReplay replay, HashSet<string>? deck, CardDb? cards)
    {
        if (replay.Mulligan is { Offered.Count: > 0 } m)
        {
            record["mull"] = new JsonObject
            {
                ["o"] = Cards(m.Offered.Where(NotCoin)),
                ["k"] = Cards(m.Kept.Where(NotCoin)),
                ["r"] = Cards(m.Replaced.Where(NotCoin)),
                ["n"] = Cards(m.Received.Where(NotCoin)),
            };
        }

        // 1 = not in the deck list (created during the game); 0 = a deck card, or the deck is not known.
        int Generated(string card) => deck is not null && !deck.Contains(card) ? 1 : 0;

        var plays = new JsonArray();
        var opponent = new JsonArray();
        for (var i = 1; i < replay.Frames.Count && plays.Count < MaxPlays; i++)
        {
            var frame = replay.Frames[i];
            if (frame.Action is not { Kind: ReplayActionKind.Play, CardId: { } card } action || frame.Mulligan)
            {
                continue;
            }

            if (action.Player == ReplayPlayer.Local)
            {
                var mana = Math.Clamp(frame.Local.ManaUsed - replay.Frames[i - 1].Local.ManaUsed, 0, 30);
                plays.Add(new JsonArray(Turn(frame.PlayerTurn), card, mana, Generated(card)));
            }
            else if (action.Player == ReplayPlayer.Opponent && opponent.Count < MaxOpponentPlays)
            {
                opponent.Add(new JsonArray(Turn(frame.PlayerTurn), card));
            }
        }

        record["plays"] = plays;
        record["oplays"] = opponent;

        var choices = new JsonArray();
        foreach (var frame in replay.Frames)
        {
            var choice = frame.Action;
            if (choices.Count >= MaxChoices || choice.Kind != ReplayActionKind.Choice || choice.Player != ReplayPlayer.Local
                || choice.Options.Count < 2 || choice.Picked.Count == 0 || !choice.Picked.All(choice.Options.Contains))
            {
                continue;
            }

            var turn = frame.PlayerTurn;
            var kind = choice.CardId is { } source && cards?.Get(source) is { } sourceCard ? KindOf(sourceCard.Mechanics) : "choice";
            choices.Add(new JsonArray(Turn(turn), kind, choice.CardId, Cards(choice.Options.Take(8)), Cards(choice.Picked.Take(4))));
        }

        record["choices"] = choices;

        var draws = new JsonArray();
        var excavated = new JsonArray();
        foreach (var (card, turn) in replay.LocalHandEntries.Take(MaxDraws))
        {
            draws.Add(new JsonArray(Turn(turn), card, Generated(card)));
            if (Treasures.Contains(card))
            {
                excavated.Add(new JsonArray(Turn(turn), card));
            }
        }

        record["draws"] = draws;
        record["exc"] = excavated;
    }

    private static int Turn(int turn) => Math.Clamp(turn, 0, MaxTurn);

    /// <summary>The Coin is in the second player's opening hand but is not a mulligan card.</summary>
    private static bool NotCoin(string card) => card != "GAME_005" && !card.Contains("_COIN", StringComparison.Ordinal);

    private static JsonArray Cards(IEnumerable<string> cards) => new(cards.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray());

    private static JsonObject? Battlegrounds(GameRecord game, PoolGame? pool, string? replayText, CardDb? cards)
    {
        if (game.Placement is < 1 or > 8)
        {
            return null;
        }

        var bg = new JsonObject { ["placement"] = game.Placement };
        if (game.RatingBefore is { } rating and > 0)
        {
            bg["mmr"] = rating / 500 * 500;
        }

        if (pool is not null)
        {
            var row = PoolClient.Row(pool);
            bg["picked"] = pool.Picked;
            bg["offered"] = Cards(pool.Offered);
            foreach (var key in new[] { "anomaly", "tier", "triples", "board", "trinkets", "tierups", "combats", "choices", "plays" })
            {
                if (row.TryGetValue(key, out var value) && value is not null)
                {
                    bg[key] = System.Text.Json.JsonSerializer.SerializeToNode(value);
                }
            }

            if (pool.Details is { } d)
            {
                bg["tribes"] = Cards(d.Tribes);
                bg["quests"] = Cards(d.Choices.Where(c => c.Kind == "quest").SelectMany(c => c.Picked).Distinct(StringComparer.Ordinal).Take(8));
            }
        }
        else
        {
            // Without the live game (no pool view): what the history row has.
            if (game.HeroCardId is { Length: > 0 } hero)
            {
                bg["picked"] = cards?.BaseHeroId(hero) ?? hero;
            }

            if (game.AnomalyCardId is { Length: > 0 } anomaly)
            {
                bg["anomaly"] = anomaly;
            }

            if (game.Races is { Length: > 0 } races)
            {
                bg["tribes"] = Cards(races.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(8));
            }

            if (game.TavernTier is >= 1 and <= 7)
            {
                bg["tier"] = game.TavernTier;
            }
        }

        if (replayText is not null && BattlegroundsMatchReplay.Parse(replayText) is { } replay)
        {
            var buys = new JsonArray();
            var sells = new JsonArray();
            foreach (var frame in replay.Frames)
            {
                if (frame.Actor?.CardId is not { Length: > 0 } card)
                {
                    continue;
                }

                if (frame.Kind == MatchStepKind.Buy && buys.Count < 300)
                {
                    buys.Add(new JsonArray(Turn(frame.Turn), card));
                }
                else if (frame.Kind == MatchStepKind.Sell && sells.Count < 300)
                {
                    sells.Add(new JsonArray(Turn(frame.Turn), card));
                }
            }

            bg["buys"] = buys;
            bg["sells"] = sells;
        }

        return bg;
    }

    /// <summary>The day number the server stores (days since 1970-01-01, UTC).</summary>
    internal static int Day(DateTime startedAt) => (int)(startedAt.ToUniversalTime().Date - DateTime.UnixEpoch).TotalDays;
}
