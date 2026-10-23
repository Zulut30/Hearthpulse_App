using Hearthpulse.Core.Data;
using Hearthpulse.Core.Memory;
using Hearthpulse.Core.Tracking;
using Xunit;

namespace Hearthpulse.Tests;

/// <summary>
/// The deck tracker needs the deck Hearthstone announces in Decks.log before a game. Current clients write the lines
/// without a method name ("I 12:21:51.2461640 Finding Game With Deck:"); that format was skipped, so no deck was ever
/// captured. These lines come from a real Decks.log (October 2026).
/// </summary>
public sealed class DecksLogTests
{
    private const string RogueCode = "AAECAeCqBgiGqAeHqAeIqAflrwe2sgel2AeI2QfDiwgL958E2aIGwZcHtMEHxcEH1cUHq8kHpNgHkdkHjdoHvPIHAAA=";

    [Theory]
    [InlineData("I 12:21:51.2461640 Finding Game With Deck:", "Finding Game With Deck:")]
    [InlineData("I 12:21:51.2461640 ### Aggro - Rogue", "### Aggro - Rogue")]
    [InlineData("D 02:01:43.12 DeckManager.FindGame() - # Deck ID: 42", "# Deck ID: 42")]
    [InlineData("not a log line", null)]
    public void Line_text_is_read_from_both_formats(string line, string? expected) =>
        Assert.Equal(expected, GameSession.DecksContent(line));

    [Fact]
    public void The_deck_for_the_next_game_is_captured()
    {
        var db = Path.Combine(Path.GetTempPath(), $"hp-decks-{Guid.NewGuid():N}.db");
        using var memory = new HearthstoneMemory();
        var session = new GameSession(new HistoryStore(db), memory);

        session.OnLines("Decks.log",
        [
            "I 12:18:58.6107745 ### Разбойник",
            "I 12:18:58.6107745 # Deck ID: 2774348185",
            "I 12:18:58.6107745 " + RogueCode,
            "I 12:21:51.2461640 Finding Game With Deck:",
            "I 12:21:51.2461640 ### Разбойник",
            "I 12:21:51.2461640 # Deck ID: 2774348185",
            "I 12:21:51.2461640 " + RogueCode,
        ], catchingUp: false);

        var deck = session.QueuedDeck;
        Assert.NotNull(deck);
        Assert.Equal("Разбойник", deck.Name);
        Assert.Equal(2774348185, deck.DeckId);
        Assert.Equal(30, deck.Cards.Values.Sum());
    }
}
