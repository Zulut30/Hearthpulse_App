using System.Text.Json;

namespace Hearthpulse.Core.Data;

/// <summary>A card and the player's turn it was drawn or played on (0 = the opening hand).</summary>
public sealed record CardTurn(string CardId, int Turn);

/// <summary>
/// The local player's mulligan: the deck cards offered (the coin is not), the ones sent back and the ones dealt in
/// their place. A card offered twice (two copies) is listed twice.
/// </summary>
public sealed record MulliganFacts(IReadOnlyList<string> Offered, IReadOnlyList<string> Replaced, IReadOnlyList<string> Received)
{
    /// <summary>Offered cards that stayed in hand (copies counted: one of two copies may be kept).</summary>
    public IReadOnlyList<string> Kept
    {
        get
        {
            var sentBack = Replaced.ToList();
            var kept = new List<string>();
            foreach (var card in Offered)
            {
                if (!sentBack.Remove(card))
                {
                    kept.Add(card);
                }
            }

            return kept;
        }
    }
}

/// <summary>JSON of the card columns of a history row (<c>mulligan</c>, <c>cards_drawn</c>, <c>cards_played</c>, <c>opponent_cards</c>).</summary>
public static class GameCards
{
    /// <summary><c>[{"id":"CORE_CS2_029","turn":3}]</c>, or null for no cards.</summary>
    public static string? TurnsJson(IReadOnlyList<CardTurn>? cards) =>
        cards is not { Count: > 0 } ? null : JsonSerializer.Serialize(cards.Select(c => new { id = c.CardId, turn = c.Turn }));

    /// <summary><c>{"offered":[..],"replaced":[..],"received":[..]}</c>, or null without a mulligan.</summary>
    public static string? MulliganJson(MulliganFacts? mulligan) =>
        mulligan is null ? null : JsonSerializer.Serialize(new { offered = mulligan.Offered, replaced = mulligan.Replaced, received = mulligan.Received });

    /// <summary>The cards of a <see cref="TurnsJson"/> column; empty for null or unreadable text.</summary>
    public static IReadOnlyList<CardTurn> Turns(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var list = new List<CardTurn>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() is { Length: > 0 } cardId)
                {
                    var turn = item.TryGetProperty("turn", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetInt32(out var n) ? n : 0;
                    list.Add(new CardTurn(cardId, turn));
                }
            }

            return list;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The mulligan of a <see cref="MulliganJson"/> column; null for null or unreadable text.</summary>
    public static MulliganFacts? Mulligan(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return root.ValueKind != JsonValueKind.Object
                ? null
                : new MulliganFacts(Ids(root, "offered"), Ids(root, "replaced"), Ids(root, "received"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<string> Ids(JsonElement root, string name) =>
        root.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).Where(s => s.Length > 0).ToList()
            : [];
}
