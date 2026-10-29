using System.Globalization;
using Hearthpulse.Core.Hearthstone;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.Core.Logs;

/// <summary>
/// One deck block of Decks.log: the client lists every deck after logging in ("Deck Contents Received:") and names
/// the deck it queues with ("Finding Game With Deck:") a few seconds before the game starts.
/// </summary>
public sealed record DecksLogEntry(DateTime At, bool FindingGame, string Name, long DeckId, string Code);

/// <summary>Reads kept Decks.log files (history backfill, deck lookups by id).</summary>
public static class DecksLog
{
    /// <summary>
    /// Deck blocks in <paramref name="lines"/>. Lines carry only the time of day; <paramref name="date"/> is the session
    /// start, and the date moves on when the clock jumps back by more than 12 hours (a session past midnight).
    /// </summary>
    public static IReadOnlyList<DecksLogEntry> Parse(IEnumerable<string> lines, DateOnly date)
    {
        var entries = new List<DecksLogEntry>();
        var last = TimeSpan.MinValue;
        var finding = false;
        string? name = null;
        long id = 0;
        foreach (var line in lines)
        {
            if (GameSession.DecksContent(line) is not { } content || !TryTime(line, out var time))
            {
                continue;
            }

            if (last != TimeSpan.MinValue && time < last - TimeSpan.FromHours(12))
            {
                date = date.AddDays(1);
            }

            last = time;
            // Deck lines first: a deck name may itself end with ':'.
            if (content.StartsWith("### ", StringComparison.Ordinal))
            {
                name = content[4..];
                id = 0;
            }
            else if (content.StartsWith("# Deck ID: ", StringComparison.Ordinal))
            {
                _ = long.TryParse(content.AsSpan(11), NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
            }
            else if (content.EndsWith(':'))
            {
                // "Deck Contents Received:", "Finding Game With Deck:", "Starting Game With Deck:"...
                finding = content.StartsWith("Finding Game", StringComparison.Ordinal);
                name = null;
                id = 0;
            }
            else if (name is not null && !content.StartsWith('#') && !content.Contains(' ', StringComparison.Ordinal))
            {
                entries.Add(new DecksLogEntry(date.ToDateTime(TimeOnly.FromTimeSpan(time)), finding, name, id, content));
                name = null;
                id = 0;
            }
        }

        return entries;
    }

    /// <summary>Deck blocks of one log session folder (empty when its Decks.log is missing or unreadable).</summary>
    public static IReadOnlyList<DecksLogEntry> Read(string sessionDir)
    {
        var path = Path.Combine(sessionDir, "Decks.log");
        if (HearthstoneInstall.SessionStart(sessionDir) is not { } start || !File.Exists(path))
        {
            return [];
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var lines = new List<string>();
            while (reader.ReadLine() is { } line)
            {
                lines.Add(line);
            }

            return Parse(lines, DateOnly.FromDateTime(start));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diag.Error($"Decks.log unreadable ({sessionDir})", ex);
            return [];
        }
    }

    /// <summary>Deck blocks of every kept log session, oldest first.</summary>
    public static IReadOnlyList<DecksLogEntry> ReadAll(string installDir) =>
        HearthstoneInstall.AllLogSessions(installDir).SelectMany(Read).OrderBy(e => e.At).ToList();

    /// <summary>
    /// Every code a deck had in the kept logs. Editing a deck keeps its id and changes its code, so games recorded
    /// with an older list still belong to the same deck.
    /// </summary>
    public static IReadOnlyCollection<string> CodesOf(IEnumerable<DecksLogEntry> entries, long deckId) =>
        deckId == 0
            ? []
            : entries.Where(e => e.DeckId == deckId).Select(e => e.Code).ToHashSet(StringComparer.Ordinal);

    private static bool TryTime(string line, out TimeSpan time)
    {
        // "I 12:21:51.2461640 ..." - the time of day follows the one-letter level.
        var end = line.IndexOf(' ', 2);
        time = default;
        return line.Length > 2 && end > 2
            && TimeSpan.TryParseExact(line.AsSpan(2, Math.Min(end - 2, 16)), TimeFormats, CultureInfo.InvariantCulture, out time);
    }

    private static readonly string[] TimeFormats = [@"hh\:mm\:ss\.FFFFFFF", @"h\:mm\:ss\.FFFFFFF", @"hh\:mm\:ss", @"h\:mm\:ss"];
}
