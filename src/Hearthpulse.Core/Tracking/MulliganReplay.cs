using System.Globalization;
using Hearthpulse.Core.Api;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Hearthstone;
using Hearthpulse.Core.Power;

namespace Hearthpulse.Core.Tracking;

/// <summary>
/// Replays the newest kept Standard log up to the local player's mulligan and builds the panel for it, for the
/// <c>--render-ui</c> checks of both apps. Reads the logs only; the guide comes from the API (or its disk cache).
/// </summary>
public static class MulliganReplay
{
    /// <summary>The Constructed view at the first mulligan of the newest Standard game whose deck Decks.log names, or null.</summary>
    public static async Task<ConstructedView?> LatestAsync(string installDirectory, CardDb cards, HearthpulseData? data, ConstructedMeta? meta, CancellationToken cancellationToken = default)
    {
        foreach (var dir in HearthstoneInstall.AllLogSessions(installDirectory).Reverse())
        {
            var power = Path.Combine(dir, "Power.log");
            var decks = Path.Combine(dir, "Decks.log");
            if (!File.Exists(power) || !File.Exists(decks))
            {
                continue;
            }

            var found = Replay(power, ReadDecks(decks), cards);
            if (found is null)
            {
                continue;
            }

            var (game, deck, tracker) = found.Value;
            var playerClass = cards.Get(game.LocalHero?.CardId)?.CardClass ?? string.Empty;
            var guide = data is null ? null : await MulliganGuideSource.LoadGuideAsync(data, playerClass, deck.Cards, cancellationToken).ConfigureAwait(false);
            var view = tracker.BuildView(game, cards);
            return view with { Mulligan = Mulligan.Build(game, cards, deck, meta, playerClass, guide) };
        }

        return null;
    }

    private static (GameState Game, DeckDefinition Deck, ConstructedTracker Tracker)? Replay(string log, List<(TimeSpan Time, DeckDefinition Deck)> decks, CardDb cards)
    {
        if (decks.Count == 0)
        {
            return null;
        }

        var parser = new PowerParser(DateOnly.FromDateTime(File.GetLastWriteTime(log)));
        var tracker = new ConstructedTracker();
        parser.AddListener(tracker);
        var started = TimeSpan.Zero;
        foreach (var line in ReadShared(log))
        {
            if (line.Contains("CREATE_GAME", StringComparison.Ordinal) && Time(line) is { } at)
            {
                started = at;
                tracker.Deck = null;
            }

            parser.Feed(line);
            if (!line.Contains("MULLIGAN_STATE value=INPUT", StringComparison.Ordinal))
            {
                continue;
            }

            parser.Flush();
            if (parser.Game is not { FormatType: "FT_STANDARD", LocalPlayerId: > 0 } game || !game.IsConstructedLike || !Mulligan.InProgress(game)
                || !game.Controlled(game.LocalPlayerId, "HAND").Any(e => e.CardId.Length > 0))
            {
                continue;
            }

            // The deck queued last before this game started is the one being played.
            var deck = decks.LastOrDefault(d => d.Time <= started).Deck;
            if (deck is not null)
            {
                tracker.Deck = deck;
                return (game, deck, tracker);
            }
        }

        return null;
    }

    /// <summary>"Finding Game With Deck:" blocks of Decks.log with their time; the deck code is the block's last line.</summary>
    private static List<(TimeSpan Time, DeckDefinition Deck)> ReadDecks(string path)
    {
        var result = new List<(TimeSpan, DeckDefinition)>();
        TimeSpan? blockTime = null;
        string name = string.Empty;
        foreach (var line in ReadShared(path))
        {
            var content = GameSession.DecksContent(line);
            if (content is null)
            {
                continue;
            }

            if (content.EndsWith("With Deck:", StringComparison.Ordinal))
            {
                blockTime = Time(line);
                name = string.Empty;
            }
            else if (blockTime is not null && content.StartsWith("### ", StringComparison.Ordinal))
            {
                name = content[4..];
            }
            else if (blockTime is { } at && !content.StartsWith('#') && DeckString.TryDecode(content, name) is { } deck)
            {
                result.Add((at, deck));
                blockTime = null;
            }
        }

        return result;
    }

    private static TimeSpan? Time(string line)
    {
        var parts = line.Split(' ', 3);
        return parts.Length > 1 && TimeSpan.TryParse(parts[1], CultureInfo.InvariantCulture, out var time) ? time : null;
    }

    private static IEnumerable<string> ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }
}
