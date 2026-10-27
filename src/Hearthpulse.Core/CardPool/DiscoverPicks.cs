using System.Collections.Concurrent;
using Hearthpulse.Core.Api;
using Hearthpulse.Core.Cards;

namespace Hearthpulse.Core.CardPool;

/// <summary>A card with a choice the Hearthpulse API knows, and whether it has numbers for it.</summary>
/// <param name="Formats">"standard", "wild": the formats the card is played in.</param>
public sealed record ChoiceCard(string CardId, int DbfId, IReadOnlySet<string> Formats, bool HasStats);

/// <summary>One option of a card's choices: how often players took it when offered and how often they won after taking it.</summary>
/// <param name="PickRate">Share of offers in which it was taken (0..1), null when unknown.</param>
/// <param name="WinRate">Share of games won after taking it (0..1), null when unknown.</param>
/// <param name="LowSample">The server's mark that the numbers rest on few games.</param>
public sealed record CardChoiceOption(string CardId, int DbfId, double? PickRate, double? WinRate, bool LowSample);

/// <summary>What players took from one card's choices in one format and rank (Hearthpulse API, <c>v1/hsreplay/card-choices</c>).</summary>
/// <param name="Options">Options with numbers; empty when the server has not collected the card.</param>
public sealed record CardChoices(string CardId, string Format, string Rank, IReadOnlyList<CardChoiceOption> Options, DateTime? AsOf)
{
    /// <summary>The rank the pick hint asks for.</summary>
    public const string DefaultRank = "legend";
}

/// <summary>One offered card of an open choice, with what players did when it was offered by the same card.</summary>
/// <param name="PickRate">Share of offers in which it was taken (0..1); null when the API has no number for it.</param>
/// <param name="WinRate">Share of games won after taking it (0..1); null when the API has no number for it.</param>
/// <param name="LowSample">The numbers rest on few games (the API's <c>low_sample</c>).</param>
/// <param name="Top">The most taken option, when its numbers are shown and it leads the others clearly.</param>
public sealed record DiscoverPickRow(string CardId, string Name, double? PickRate, double? WinRate, bool LowSample, bool Top);

/// <summary>The pick hint of an open choice, in offer order.</summary>
/// <param name="Rank">The rank the numbers are from ("legend").</param>
public sealed record DiscoverPicksView(string SourceCardId, string Rank, IReadOnlyList<DiscoverPickRow> Rows)
{
    /// <summary>Whether any option has a number.</summary>
    public bool HasNumbers => Rows.Any(r => r.PickRate is not null);

    /// <summary>Whether any option with a number rests on few games.</summary>
    public bool HasLowSample => Rows.Any(r => r.PickRate is not null && r.LowSample);
}

/// <summary>
/// Builds the pick hint of the player's own open choice from the Hearthpulse API's card choices
/// (<see cref="CardChoices"/>): only the cards on the player's screen. Numbers, no advice.
/// </summary>
public static class DiscoverPicks
{
    /// <summary>How far the most taken option must lead the next for the "most taken" mark (pick rate points).</summary>
    public const double TopLead = 0.05;

    /// <param name="source">The card id the log named as the choice's source, as the tracker shares it.</param>
    /// <param name="offered">Card ids of the options on screen, in offer order; matched to the API's options by card id, then by dbfId.</param>
    /// <returns>Null when the API has no numbers for the card.</returns>
    public static DiscoverPicksView? Build(CardChoices choices, string source, IReadOnlyList<string> offered, CardDb cards)
    {
        if (choices.Options.Count == 0)
        {
            return null;
        }

        var byId = new Dictionary<string, CardChoiceOption>(StringComparer.OrdinalIgnoreCase);
        var byDbf = new Dictionary<int, CardChoiceOption>();
        foreach (var option in choices.Options)
        {
            if (option.CardId.Length > 0)
            {
                byId.TryAdd(option.CardId, option);
            }

            if (option.DbfId > 0)
            {
                byDbf.TryAdd(option.DbfId, option);
            }
        }

        var rows = offered.Select(id =>
        {
            var card = cards.Get(id);
            var option = byId.TryGetValue(id, out var o) ? o
                : card is { DbfId: > 0 } && byDbf.TryGetValue(card.DbfId, out o) ? o
                : null;
            return new DiscoverPickRow(id, card?.Name ?? id, option?.PickRate, option?.PickRate is null ? null : option.WinRate, option?.LowSample ?? false, false);
        }).ToList();

        // Few games make no favourite.
        var ranked = rows.Where(r => r.PickRate is not null && !r.LowSample).OrderByDescending(r => r.PickRate).ToList();
        if (ranked.Count >= 2 && ranked[0].PickRate - ranked[1].PickRate >= TopLead)
        {
            var top = rows.IndexOf(ranked[0]);
            rows[top] = rows[top] with { Top = true };
        }

        return new DiscoverPicksView(source, choices.Rank, rows);
    }

    /// <summary>
    /// --render-ui only: a hint for three options of <paramref name="pool"/> with made-up numbers (one clearly most
    /// taken, one on few games), so both UIs can be checked without the API.
    /// </summary>
    public static DiscoverPicksView RenderSample(CardPoolView pool, CardDb cards)
    {
        var offered = pool.Cards.Take(3).Select(c => c.CardId).ToList();
        var numbers = new (double Pick, double Win, bool Low)[] { (0.57, 0.555, false), (0.27, 0.477, false), (0.035, 0.43, true) };
        var options = offered.Select((id, i) => new CardChoiceOption(id, 0, numbers[i].Pick, numbers[i].Win, numbers[i].Low)).ToList();
        return Build(new CardChoices(pool.SourceCardId, "standard", CardChoices.DefaultRank, options, null), pool.SourceCardId, offered, cards)!;
    }

    /// <summary>The API's format key of a game format, or null for formats the pick hint does not cover.</summary>
    public static string? FormatOf(string formatType) => formatType switch
    {
        "FT_STANDARD" => "standard",
        "FT_WILD" => "wild",
        _ => null,
    };
}

/// <summary>
/// The Hearthpulse API's card choices for the pick hint, kept in memory: the list of cards with numbers is loaded
/// once, then one card's choices when its choice opens (never per frame), read from the disk cache of
/// <see cref="HearthpulseData"/> when fresh. <see cref="TryGet"/> answers from memory and never waits.
/// </summary>
public sealed class DiscoverChoiceSource(HearthpulseData data)
{
    /// <summary>Answers in memory are asked for again after this long, like the disk cache.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(6);

    /// <summary>A failed load is tried again after this long.</summary>
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(5);

    private const string ListKey = "*";

    private readonly ConcurrentDictionary<string, (CardChoices Choices, DateTime At)> _known = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime> _asked = new(StringComparer.Ordinal);
    private volatile ChoiceList? _list;

    /// <summary>Raised on a worker thread when the list or a card's choices arrived (the overlay builds its pick hint again).</summary>
    public event Action? Changed;

    /// <summary>
    /// The choices of <paramref name="sourceCardId"/> in <paramref name="format"/> when loaded; starts loading them
    /// (or the list first) otherwise. Null too when the API has no numbers for the card in that format.
    /// </summary>
    public CardChoices? TryGet(string format, string sourceCardId, CardDb cards)
    {
        var now = DateTime.UtcNow;
        if (_list is not { } list)
        {
            RequestList(now);
            return null;
        }

        if (list.Find(sourceCardId, cards) is not { HasStats: true } card || !card.Formats.Contains(format))
        {
            return null;
        }

        var key = format + "|" + card.CardId;
        var known = _known.TryGetValue(key, out var k) ? k : default;
        if (known.Choices is null || now - known.At > MaxAge)
        {
            Request(key, card.CardId, format, now);
        }

        return known.Choices;
    }

    /// <summary>For tests and the render scenes: the list and a card's choices as if they had been loaded.</summary>
    public void Set(IReadOnlyList<ChoiceCard> list, params CardChoices[] choices)
    {
        _list = new ChoiceList(list);
        foreach (var c in choices)
        {
            _known[c.Format + "|" + c.CardId] = (c, DateTime.UtcNow);
        }

        Changed?.Invoke();
    }

    private void RequestList(DateTime now)
    {
        if (!Ask(ListKey, now))
        {
            return;
        }

        Run(async () =>
        {
            var cards = await data.LoadChoiceCardsAsync().ConfigureAwait(false);
            if (cards.Count > 0)
            {
                _list = new ChoiceList(cards);
                Changed?.Invoke();
            }
        }, "Card choice list");
    }

    private void Request(string key, string cardId, string format, DateTime now)
    {
        if (!Ask(key, now))
        {
            return;
        }

        Run(async () =>
        {
            if (await data.LoadCardChoicesAsync(cardId, format).ConfigureAwait(false) is { } choices)
            {
                _known[key] = (choices, DateTime.UtcNow);
                Changed?.Invoke();
            }
        }, "Card choices");
    }

    private bool Ask(string key, DateTime now)
    {
        if (_asked.TryGetValue(key, out var at) && now - at < RetryAfter)
        {
            return false;
        }

        _asked[key] = now;
        return true;
    }

    private static void Run(Func<Task> load, string what) => Task.Run(load).Forget(what);

    /// <summary>The API's cards by card id and by dbfId.</summary>
    private sealed class ChoiceList
    {
        private readonly Dictionary<string, ChoiceCard> _byId = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, ChoiceCard> _byDbf = [];

        public ChoiceList(IReadOnlyList<ChoiceCard> cards)
        {
            foreach (var card in cards)
            {
                _byId.TryAdd(card.CardId, card);
                if (card.DbfId > 0)
                {
                    _byDbf.TryAdd(card.DbfId, card);
                }
            }
        }

        public ChoiceCard? Find(string cardId, CardDb cards) =>
            _byId.TryGetValue(cardId, out var card) ? card
            : cards.Get(cardId) is { DbfId: > 0 } c && _byDbf.TryGetValue(c.DbfId, out card) ? card
            : null;
    }
}
