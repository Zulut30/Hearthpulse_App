using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.Core.Pool;

public sealed record PoolMinion(string Card, bool Golden, int Attack, int Health);

/// <summary>One fight: W, T or L, damage both ways, and the win chance the simulator gave before it started.</summary>
public sealed record PoolCombat(int Turn, string Result, int Taken, int Dealt, double? Win);

/// <summary>The rest of a shared game (consent version 2 and up): lobby, final board, trinkets, tier-ups, fights, picks and plays.</summary>
public sealed record PoolDetails(
    int Turns,
    int Tier,
    int Triples,
    string? Anomaly,
    IReadOnlyList<string> Tribes,
    string? Comp,
    IReadOnlyList<PoolMinion> Board,
    IReadOnlyList<string> TrinketsOffered,
    IReadOnlyList<string> Trinkets,
    IReadOnlyList<TierStep> TierUps,
    IReadOnlyList<PoolCombat> Combats)
{
    /// <summary>The most picks one game records and shares (the tracker stops there too).</summary>
    public const int MaxChoices = 120;

    /// <summary>The most cards played from hand one game records and shares.</summary>
    public const int MaxPlays = 300;

    /// <summary>Every pick offered (Discover, triple rewards, trinkets...) and what was taken.</summary>
    public IReadOnlyList<BgChoice> Choices { get; init; } = [];

    /// <summary>Cards played from hand.</summary>
    public IReadOnlyList<BgPlay> Plays { get; init; } = [];

    internal static PoolDetails From(BgGameDetails d, Func<string, IReadOnlyList<string>>? races)
    {
        var board = d.Board
            .Where(m => m.CardType == "MINION" && m.CardId.Length > 0)
            .Take(7)
            .Select(m => new PoolMinion(m.CardId, m.Golden, Math.Max(0, m.Attack), Math.Max(0, m.Health)))
            .ToList();
        var tierUps = d.TierUps
            .Where(t => t.Tier is >= 2 and <= 7)
            .GroupBy(t => t.Tier)
            .Select(g => g.First())
            .OrderBy(t => t.Tier)
            .ToList();
        var combats = d.Combats
            .TakeLast(60)
            .Select(c => new PoolCombat(
                c.Turn,
                c.Result switch { "Win" => "W", "Loss" => "L", _ => "T" },
                Math.Clamp(c.DamageTaken, 0, 200),
                Math.Clamp(c.DamageDealt, 0, 200),
                c.Odds?.Win))
            .ToList();
        var picked = d.Trinkets.Distinct(StringComparer.Ordinal).Take(4).ToList();
        return new PoolDetails(
            Math.Clamp(d.Turns, 0, 99),
            Math.Clamp(d.Tier, 1, 7),
            Math.Clamp(d.Triples, 0, 99),
            string.IsNullOrEmpty(d.Anomaly) ? null : d.Anomaly,
            CleanTribes(d.Tribes),
            races is null ? null : MainTribe(board, races),
            board,
            d.TrinketsOffered.Concat(picked).Distinct(StringComparer.Ordinal).Take(12).ToList(),
            picked,
            tierUps,
            combats)
        {
            Choices = d.Choices
                .Where(c => c.Offered.Count is > 0 and <= 8)
                .Take(MaxChoices)
                .Select(c => c with { Picked = c.Picked.Where(c.Offered.Contains).Take(4).ToList() })
                .ToList(),
            Plays = d.Plays.Take(MaxPlays).ToList(),
        };
    }

    /// <summary>What a game saved in the local history still holds: final board, tier, length and lobby tribes.</summary>
    internal static PoolDetails? FromHistory(GameRecord record, Func<string, IReadOnlyList<string>>? races)
    {
        if (record.TavernTier is < 1 or > 7)
        {
            return null;
        }

        var board = new List<PoolMinion>();
        if (record.FinalBoard is { Length: > 0 } json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                foreach (var m in doc.RootElement.EnumerateArray().Take(7))
                {
                    if (m.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } card)
                    {
                        board.Add(new PoolMinion(
                            card,
                            m.TryGetProperty("gold", out var gold) && gold.ValueKind == JsonValueKind.True,
                            m.TryGetProperty("atk", out var atk) && atk.TryGetInt32(out var a) ? Math.Max(0, a) : 0,
                            m.TryGetProperty("hp", out var hp) && hp.TryGetInt32(out var h) ? Math.Max(0, h) : 0));
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                board.Clear();
            }
        }

        return new PoolDetails(
            Math.Clamp(record.Turns, 0, 99),
            record.TavernTier,
            0,
            null,
            CleanTribes((record.Races ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
            races is null ? null : MainTribe(board, races),
            board,
            [],
            [],
            [],
            []);
    }

    private static List<string> CleanTribes(IEnumerable<string> tribes) =>
        tribes.Where(t => t.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(8).ToList();

    /// <summary>The tribe of at least 4 minions on the final board (amalgams count for every tribe), or MIXED.</summary>
    internal static string? MainTribe(IReadOnlyList<PoolMinion> board, Func<string, IReadOnlyList<string>> races)
    {
        if (board.Count == 0)
        {
            return null;
        }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var all = 0;
        foreach (var minion in board)
        {
            var list = races(minion.Card);
            if (list.Contains("ALL"))
            {
                all++;
                continue;
            }

            foreach (var race in list.Where(r => r is { Length: > 0 } and not "INVALID").Distinct())
            {
                counts[race] = counts.GetValueOrDefault(race) + 1;
            }
        }

        var best = counts.OrderByDescending(c => c.Value).ThenBy(c => c.Key, StringComparer.Ordinal).FirstOrDefault();
        return best.Key is not null && best.Value + all >= 4 ? best.Key : "MIXED";
    }
}

/// <summary>One shared Battlegrounds game, exactly what the pool server stores. Holds no player identity.</summary>
public sealed record PoolGame(string GameId, string Mode, int Bracket, IReadOnlyList<string> Offered, string Picked, int Placement)
{
    /// <summary>Only sent when the player agreed to the longer list (consent version 2 and up).</summary>
    public PoolDetails? Details { get; init; }

    /// <summary>Day the game was played, in days since 1970-01-01 (UTC).</summary>
    public int Day { get; init; } = Today;

    /// <summary>"live" when sent as the game ended, "history" for an earlier game sent once.</summary>
    public string Source { get; init; } = "live";

    internal static int Today => (int)(DateTime.UtcNow - DateTime.UnixEpoch).TotalDays;

    /// <summary>The record for a finished game, or null when it is not a Battlegrounds game with a placement.</summary>
    public static PoolGame? From(
        GameOutcome outcome,
        Func<string, string> baseHero,
        Func<string, IReadOnlyList<string>>? races = null,
        bool details = false,
        string? secret = null)
    {
        var game = Basic(outcome.Mode, outcome.Placement, outcome.HeroCardId, outcome.RatingBefore, outcome.OfferedHeroes, outcome.Key, baseHero, secret);
        return game is null ? null : game with { Details = details && outcome.Details is { } d ? PoolDetails.From(d, races) : null };
    }

    /// <summary>A game from the local history, sent once when the player agrees to share.</summary>
    public static PoolGame? FromHistory(GameRecord record, string secret, Func<string, string> baseHero, Func<string, IReadOnlyList<string>>? races = null)
    {
        // Games brought over from another tracker stay on this PC (docs/migration.md).
        if (record.FromOtherTracker)
        {
            return null;
        }

        var game = Basic(record.Mode, record.Placement, record.HeroCardId, record.RatingBefore, null, record.Key, baseHero, secret);
        return game is null ? null : game with
        {
            Details = PoolDetails.FromHistory(record, races),
            Day = (int)(record.StartedAt.ToUniversalTime() - DateTime.UnixEpoch).TotalDays,
            Source = "history",
        };
    }

    private static PoolGame? Basic(
        string modeName,
        int placement,
        string heroCardId,
        int? ratingBefore,
        IReadOnlyList<string>? offeredHeroes,
        string? key,
        Func<string, string> baseHero,
        string? secret)
    {
        var mode = modeName switch
        {
            "Battlegrounds" => "solo",
            "Duos" => "duos",
            _ => null,
        };
        if (mode is null || placement is < 1 or > 8 || string.IsNullOrEmpty(heroCardId))
        {
            return null;
        }

        var picked = baseHero(heroCardId);
        var offered = (offeredHeroes ?? []).Select(baseHero).Distinct(StringComparer.Ordinal).Take(4).ToList();

        // An offer list without the hero played would be rejected; the placement alone still counts.
        if (!offered.Contains(picked, StringComparer.Ordinal))
        {
            offered = [];
        }

        var bracket = ratingBefore is > 0 and var rating ? Math.Min(20000, rating / 500 * 500) : 0;
        return new PoolGame(Id(key, secret), mode, bracket, offered, picked, placement);
    }

    /// <summary>
    /// The same game always gets the same id from this install, so sending it twice stores it once. The id is a keyed
    /// hash with a secret that never leaves this PC: it cannot be turned back into the game, the player or the account.
    /// </summary>
    internal static string Id(string? key, string? secret) =>
        key is { Length: > 0 } && secret is { Length: > 0 }
            ? Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(key)).AsSpan(0, 16)).ToLowerInvariant()
            : Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
}

/// <summary>Totals for the main tribe of final boards.</summary>
public sealed record PoolTribe(string Tribe, int Games, double AveragePlacement, double Top4Rate, double FirstRate);
