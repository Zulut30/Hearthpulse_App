using System.Globalization;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;

namespace Hearthpulse.Core.Replay;

/// <summary>The match page of a replay file without a History row: a starred copy or an imported game (<c>docs/replay-library.md</c>).</summary>
public sealed partial class MatchPage
{
    /// <summary>The key of a game that is only a file: never a History key (those start with the start time).</summary>
    public const string FileKeyPrefix = "file:";

    /// <summary>
    /// The game a replay file is of, as a History row would hold it, from the file's summary (format 4) or, for an older
    /// file, its kind and the start time in its name. No names ("You" and "Opponent" stand in for them), no rating.
    /// </summary>
    public static GameRecord RecordOf(ReplayHeader? header, string fileName)
    {
        var summary = header?.Summary;
        var bg = header?.Kind == ReplayFile.Battlegrounds;
        var started = summary is { StartedAt.Year: > 2000 } s ? s.StartedAt.ToLocalTime()
            : fileName.Length > 14 && DateTime.TryParseExact(fileName[..14], "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var named) ? named
            : DateTime.MinValue;
        return new GameRecord
        {
            Key = FileKeyPrefix + fileName,
            StartedAt = started,
            EndedAt = started.AddSeconds(summary?.Seconds ?? 0),
            Mode = summary?.Mode ?? (bg ? "Battlegrounds" : "Other"),
            GameType = summary?.GameType ?? string.Empty,
            Format = summary?.Format ?? string.Empty,
            PlayerName = Loc.T("Match.You"),
            OpponentName = Loc.T("Match.Opponent"),
            HeroCardId = summary?.HeroCardId ?? string.Empty,
            OpponentHeroCardId = summary?.OpponentHeroCardId ?? string.Empty,
            PlayerClass = summary?.PlayerClass ?? string.Empty,
            OpponentClass = summary?.OpponentClass ?? string.Empty,
            Result = summary?.Result ?? "Unknown",
            Placement = summary?.Placement ?? 0,
            Turns = summary?.Turns ?? 0,
            DeckCode = summary?.DeckCode,
            TavernTier = summary?.TavernTier ?? 0,
            Races = summary is { Tribes.Count: > 0 } ? string.Join(',', summary.Tribes) : null,
            Source = "file",
            AnomalyCardId = summary?.AnomalyCardId,
            WentFirst = summary?.WentFirst,
            OpponentArchetype = summary?.OpponentArchetype,
            Replay = fileName,
        };
    }

    /// <summary>
    /// Loads the page of a replay file without a History row (its text; null when it is gone or unreadable). The file
    /// may have been put in the folder by hand, so it goes through the import filter first: a file the filter refuses
    /// shows as unreadable, and what is shown is the filtered copy (no ranks, ratings, hidden cards).
    /// </summary>
    public static MatchPage LoadFile(string? text, string fileName, CardDb cards, Func<GameRecord, ConstructedMeta?> meta)
    {
        if (text is null)
        {
            var gone = RecordOf(null, fileName);
            return Load(gone, (string?)null, cards, meta(gone));
        }

        if (ReplayImport.Sanitize(text, cards, out _) is not { } clean)
        {
            var refused = RecordOf(null, fileName);
            return new MatchPage(refused, cards, BuildHeader(refused, cards, null)) { Missing = Loc.T("Match.Missing.Unreadable") };
        }

        var game = RecordOf(ReplayFile.Read(clean)?.Header, fileName);
        return Load(game, clean, cards, meta(game));
    }
}
