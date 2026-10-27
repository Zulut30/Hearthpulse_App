using System.Globalization;
using System.Text.Json;
using Hearthpulse.Core.CardPool;

namespace Hearthpulse.Core.Api;

/// <summary>What players take from Discover-style choices (<c>v1/hsreplay/card-choices</c>), for the card pool panel's pick hint.</summary>
public sealed partial class HearthpulseData
{
    /// <summary>Every card with a choice the API knows (<c>v1/hsreplay/card-choices/cards</c>); empty when unavailable.</summary>
    public async Task<IReadOnlyList<ChoiceCard>> LoadChoiceCardsAsync(CancellationToken cancellationToken = default)
    {
        using var doc = await FetchAsync("v1/hsreplay/card-choices/cards", "card-choices-cards.json", cancellationToken, ShrinkChoiceCards).ConfigureAwait(false);
        return doc is null ? [] : Read(() => ParseChoiceCards(Data(doc)), "card choice list", new List<ChoiceCard>());
    }

    /// <summary>
    /// What players took from one card's choices in a format ("standard"/"wild") at a rank ("legend", "bronze_gold"):
    /// options without numbers when the server has not collected them; null when the API has no answer.
    /// </summary>
    public async Task<CardChoices?> LoadCardChoicesAsync(string cardId, string format, string rank = CardChoices.DefaultRank, CancellationToken cancellationToken = default)
    {
        if (!CardIdKey.IsMatch(cardId) || !SafeKey.IsMatch(format) || !SafeKey.IsMatch(rank))
        {
            return null;
        }

        using var doc = await FetchAsync($"v1/hsreplay/card-choices?card={cardId}&format={format}&rank={rank}", $"card-choices-{format}-{rank}-{cardId}.json", cancellationToken, ShrinkCardChoices).ConfigureAwait(false);
        return doc is null ? null : Read(() => ParseCardChoices(Data(doc)), "card choices", (CardChoices?)null);
    }

    /// <summary><c>[{"card_id", "dbf_id", "formats": [...], "has_hsreplay_stats"}]</c>.</summary>
    internal static List<ChoiceCard> ParseChoiceCards(JsonElement root)
    {
        var result = new List<ChoiceCard>();
        foreach (var c in Items(root))
        {
            if (Str(c, "card_id") is { Length: > 0 } id)
            {
                var formats = c.TryGetProperty("formats", out var f)
                    ? Items(f, JsonValueKind.String).Select(e => e.GetString()!).ToHashSet(StringComparer.Ordinal)
                    : [];
                var hasStats = c.TryGetProperty("has_hsreplay_stats", out var s) && s.ValueKind == JsonValueKind.True;
                result.Add(new ChoiceCard(id, (int)(Num(c, "dbf_id") ?? 0), formats, hasStats));
            }
        }

        return result;
    }

    /// <summary>
    /// <c>{"card": {"card_id"}, "format", "rank", "stats": {"state", "as_of"}, "options": [{"card_id", "dbf_id",
    /// "popularity", "win_rate", "low_sample"}]}</c>; popularity and win rate are percents.
    /// </summary>
    internal static CardChoices? ParseCardChoices(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var cardId = root.TryGetProperty("card", out var card) && card.ValueKind == JsonValueKind.Object ? Str(card, "card_id") : null;
        var options = new List<CardChoiceOption>();
        if (root.TryGetProperty("options", out var list))
        {
            foreach (var o in Items(list))
            {
                var id = Str(o, "card_id") ?? string.Empty;
                var dbf = (int)(Num(o, "dbf_id") ?? 0);
                if (id.Length == 0 && dbf <= 0)
                {
                    continue;
                }

                var low = o.TryGetProperty("low_sample", out var l) && l.ValueKind == JsonValueKind.True;
                options.Add(new CardChoiceOption(id, dbf, Share(Num(o, "popularity")), Share(Num(o, "win_rate")), low));
            }
        }

        var stats = root.TryGetProperty("stats", out var s) && s.ValueKind == JsonValueKind.Object ? s : default;
        var asOf = stats.ValueKind == JsonValueKind.Object && Str(stats, "as_of") is { } text
                   && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
            ? at
            : (DateTime?)null;
        return new CardChoices(cardId ?? string.Empty, Str(root, "format") ?? string.Empty, Str(root, "rank") ?? string.Empty, options, asOf);
    }

    /// <summary>A percent (0..100) as a share (0..1); null when missing or out of range.</summary>
    private static double? Share(double? percent) => percent is >= 0 and <= 100 ? percent / 100 : null;

    // The list is about 130 KB and an answer per card up to 40 KB (names, texts, the whole pool): the cache keeps only what is read.
    private static object ShrinkChoiceCards(JsonElement data) =>
        Items(data).Select(c => new Dictionary<string, object?>
        {
            ["card_id"] = Str(c, "card_id"),
            ["dbf_id"] = Num(c, "dbf_id"),
            ["formats"] = c.TryGetProperty("formats", out var f) ? Items(f, JsonValueKind.String).Select(e => e.GetString()).ToList() : [],
            ["has_hsreplay_stats"] = c.TryGetProperty("has_hsreplay_stats", out var s) && s.ValueKind == JsonValueKind.True,
        }).ToList();

    private static object ShrinkCardChoices(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object)
        {
            return data;
        }

        var card = data.TryGetProperty("card", out var c) && c.ValueKind == JsonValueKind.Object ? Str(c, "card_id") : null;
        var stats = data.TryGetProperty("stats", out var s) && s.ValueKind == JsonValueKind.Object ? s : default;
        return new Dictionary<string, object?>
        {
            ["card"] = new Dictionary<string, object?> { ["card_id"] = card },
            ["format"] = Str(data, "format"),
            ["rank"] = Str(data, "rank"),
            ["stats"] = new Dictionary<string, object?>
            {
                ["state"] = stats.ValueKind == JsonValueKind.Object ? Str(stats, "state") : null,
                ["as_of"] = stats.ValueKind == JsonValueKind.Object ? Str(stats, "as_of") : null,
            },
            ["options"] = data.TryGetProperty("options", out var list)
                ? Items(list).Select(o => new Dictionary<string, object?>
                {
                    ["card_id"] = Str(o, "card_id"),
                    ["dbf_id"] = Num(o, "dbf_id"),
                    ["popularity"] = Num(o, "popularity"),
                    ["win_rate"] = Num(o, "win_rate"),
                    ["low_sample"] = o.TryGetProperty("low_sample", out var l) && l.ValueKind == JsonValueKind.True,
                }).ToList()
                : [],
        };
    }
}
