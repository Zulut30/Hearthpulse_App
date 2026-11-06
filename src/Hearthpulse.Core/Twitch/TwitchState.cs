using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.Core.Twitch;

/// <summary>One row of the deck as the extension shows it: the card, its cost and name, copies left and in the list.</summary>
public sealed record TwitchCard(string Id, int Dbf, int Cost, string Name, int Left, int Total);

/// <summary>The deck being played: its name and hero (null when unknown), format, cards left, and the rows.</summary>
public sealed record TwitchDeck(string? Name, string? Hero, string Format, int Left, IReadOnlyList<TwitchCard> Cards);

/// <summary>
/// What goes to the Twitch extension (<c>POST twitch/state</c>, docs/streaming.md): the own deck's remaining cards in a
/// Constructed-like game, or <see cref="None"/>. Only what the deck panel shows the player: no deck order, no hand,
/// nothing about the opponent. <see cref="Key"/> is the body without its number, to tell a change from a repeat.
/// </summary>
public sealed class TwitchState
{
    /// <summary>The body's version.</summary>
    public const int Version = 1;

    /// <summary>The largest body the site takes, in bytes of UTF-8.</summary>
    public const int MaxBodyBytes = 16 * 1024;

    private static readonly JsonWriterOptions Writer = new() { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };

    private TwitchState(TwitchDeck? deck, int turn)
    {
        Deck = deck;
        Turn = turn;
        Key = Write(0, keyed: false);
    }

    /// <summary>No deck to show: no game, Battlegrounds, the Arena draft, a game whose deck is not known.</summary>
    public static TwitchState None { get; } = new(null, 0);

    /// <summary>"constructed" with a deck, else "none".</summary>
    public string Mode => Deck is null ? "none" : "constructed";

    public TwitchDeck? Deck { get; }

    /// <summary>The game's turn (0 before the first).</summary>
    public int Turn { get; }

    /// <summary>The body without <c>v</c> and <c>seq</c>: equal keys are the same state.</summary>
    public string Key { get; }

    /// <summary>A Constructed state (tests and <see cref="From"/>).</summary>
    public static TwitchState Of(TwitchDeck deck, int turn) => new(deck, Math.Max(0, turn));

    /// <summary>
    /// The state of <paramref name="snapshot"/>, by the rule of the OBS deck window (docs/streaming.md): a game other
    /// than Battlegrounds with its deck known; anything else is <see cref="None"/>. Rows keep the tracker's order.
    /// </summary>
    public static TwitchState From(LiveSnapshot snapshot, CardDb? cards)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.InGame || snapshot.Battlegrounds is not null || snapshot.Constructed is not { DeckKnown: true } view)
        {
            return None;
        }

        var rows = view.Deck
            .Select(r => new TwitchCard(r.CardId, cards?.Get(r.CardId)?.DbfId ?? 0, r.Cost, r.Name, r.Remaining, r.Total))
            .ToList();
        var deck = new TwitchDeck(
            string.IsNullOrWhiteSpace(view.DeckName) ? null : view.DeckName,
            string.IsNullOrEmpty(view.PlayerHeroCardId) ? null : view.PlayerHeroCardId,
            FormatOf(snapshot.GameType, view.FormatType),
            Math.Max(0, view.DeckCount),
            rows);
        return Of(deck, view.Turn);
    }

    /// <summary>
    /// The contract's format: Casual games are "casual" whatever their format; Ranked, Friendly and Practice by the
    /// log's format; Arena, Brawl and the rest "other".
    /// </summary>
    internal static string FormatOf(string? gameType, string? formatType)
    {
        if (gameType == "GT_CASUAL")
        {
            return "casual";
        }

        if (gameType is not ("GT_RANKED" or "GT_VS_FRIEND" or "GT_VS_AI"))
        {
            return "other";
        }

        return formatType switch
        {
            "FT_STANDARD" => "standard",
            "FT_WILD" => "wild",
            "FT_TWIST" => "twist",
            "FT_CLASSIC" => "classic",
            _ => "other",
        };
    }

    /// <summary>The body to send, numbered <paramref name="seq"/>.</summary>
    public string ToJson(long seq) => Write(seq, keyed: true);

    private string Write(long seq, bool keyed)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, Writer))
        {
            json.WriteStartObject();
            if (keyed)
            {
                json.WriteNumber("v", Version);
                json.WriteNumber("seq", seq);
            }

            json.WriteString("mode", Mode);
            if (Deck is { } deck)
            {
                json.WriteStartObject("deck");
                WriteNullable(json, "name", deck.Name);
                WriteNullable(json, "hero", deck.Hero);
                json.WriteString("format", deck.Format);
                json.WriteNumber("left", deck.Left);
                json.WriteStartArray("cards");
                foreach (var card in deck.Cards)
                {
                    json.WriteStartObject();
                    json.WriteString("id", card.Id);
                    json.WriteNumber("dbf", card.Dbf);
                    json.WriteNumber("cost", card.Cost);
                    json.WriteString("name", card.Name);
                    json.WriteNumber("left", card.Left);
                    json.WriteNumber("total", card.Total);
                    json.WriteEndObject();
                }

                json.WriteEndArray();
                json.WriteEndObject();
                json.WriteStartObject("game");
                json.WriteNumber("turn", Turn);
                json.WriteEndObject();
            }

            json.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static void WriteNullable(Utf8JsonWriter json, string name, string? value)
    {
        if (value is null)
        {
            json.WriteNull(name);
        }
        else
        {
            json.WriteString(name, value);
        }
    }
}
