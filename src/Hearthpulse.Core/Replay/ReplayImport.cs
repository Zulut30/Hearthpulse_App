using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;

namespace Hearthpulse.Core.Replay;

/// <summary>Why an import was refused (the library shows <c>Library.ImportError.*</c>).</summary>
public enum ReplayImportError
{
    None,

    /// <summary>Neither a Hearthpulse replay nor a Power.log.</summary>
    NotReplay,

    /// <summary>Over <see cref="ReplayImport.MaxFileBytes"/> packed or <see cref="ReplayImport.MaxTextChars"/> unpacked.</summary>
    TooLarge,

    /// <summary>A replay whose header or a line breaks the format: the whole file is refused.</summary>
    Damaged,

    /// <summary>A replay format this version does not read (the first one, or a newer one).</summary>
    Version,

    /// <summary>The file could not be read (gone, locked, no access).</summary>
    Unreadable,
}

/// <summary>What an import did: the files it added to Imported (newest game first) and how many were there already, or why it refused.</summary>
public sealed record ReplayImportResult(ReplayImportError Error, IReadOnlyList<string> Added, int Already = 0, bool FromLog = false)
{
    public bool Ok => Error == ReplayImportError.None;

    public static ReplayImportResult Refused(ReplayImportError error) => new(error, []);
}

/// <summary>
/// Import into the library's Imported folder (<c>docs/replay-library.md</c>): a <c>.hpr</c> file of anybody, or a
/// Power.log whose finished games are recorded as a log import does (never into History or the stats). A foreign file
/// is untrusted: it is read with size limits, every line must keep the grammar of its format (else the whole file is
/// refused), and the hidden-information rule of <c>docs/match-replay.md</c> is applied again (a card id in the opponent's
/// hand or secret, the combat opponent outside a combat, Bob's offer in a combat are cleaned away). What is stored is
/// the copy rebuilt from the known fields only, as format 4, without ranks, ratings or the legend number. The same
/// filter makes the copy for "Share" (<see cref="ReplayShare"/>).
/// </summary>
public static partial class ReplayImport
{
    /// <summary>The largest packed replay file accepted.</summary>
    public const long MaxFileBytes = 16L * 1024 * 1024;

    /// <summary>The longest replay text accepted (the gzip-bomb guard: unpacking stops here).</summary>
    public const int MaxTextChars = 32 * 1024 * 1024;

    /// <summary>The most post-game lines (<c>N</c>, <c>K</c>) a Constructed file may hold.</summary>
    private const int MaxPostGameLines = 20_000;

    private const int MaxListItems = 16;

    /// <summary>
    /// Imports one file into <paramref name="imported"/>: a replay (packed or not) or a Power.log. A log's games are
    /// recorded into <paramref name="workDir"/> first, then filtered like any file; the folder is deleted afterwards.
    /// </summary>
    public static ReplayImportResult ImportFile(string path, ReplayStore imported, CardDb cards, string workDir, CancellationToken cancellationToken = default)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return ReplayImportResult.Refused(ReplayImportError.Unreadable);
            }

            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> head = stackalloc byte[2];
            var gzip = file.ReadAtLeast(head, 2, throwOnEndOfStream: false) == 2 && head[0] == 0x1F && head[1] == 0x8B;
            file.Position = 0;
            if (gzip)
            {
                if (info.Length > MaxFileBytes)
                {
                    return ReplayImportResult.Refused(ReplayImportError.TooLarge);
                }

                using var unpacked = new GZipStream(file, CompressionMode.Decompress);
                return ReadLimited(unpacked, MaxTextChars) is { } text ? Add(text, imported, cards) : ReplayImportResult.Refused(ReplayImportError.TooLarge);
            }

            var start = ReadLimited(file, 64 * 1024, cut: true) ?? string.Empty;
            if (start.StartsWith("HPR", StringComparison.Ordinal))
            {
                file.Position = 0;
                return info.Length > MaxTextChars || ReadLimited(file, MaxTextChars) is not { } text
                    ? ReplayImportResult.Refused(ReplayImportError.TooLarge)
                    : Add(text, imported, cards);
            }

            return IsPowerLog(start) ? ImportLog(path, info, imported, cards, workDir, cancellationToken) : ReplayImportResult.Refused(ReplayImportError.NotReplay);
        }
        catch (InvalidDataException)
        {
            return ReplayImportResult.Refused(ReplayImportError.Damaged);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            Diag.Error($"Importing {Path.GetFileName(path)} failed", ex);
            return ReplayImportResult.Refused(ReplayImportError.Unreadable);
        }
    }

    /// <summary>
    /// The replay rebuilt by the import filter as format 4, or null with the reason. <paramref name="dateOnly"/> (the copy
    /// to share) keeps only the date of the game's start, not its time.
    /// </summary>
    public static string? Sanitize(string text, CardDb cards, out ReplayImportError error, bool dateOnly = false)
    {
        var magic = text.AsSpan(0, Math.Min(text.Length, 16));
        var end = magic.IndexOf('\n');
        var first = (end < 0 ? magic : magic[..end]).TrimEnd('\r').ToString();
        if (!MagicLine().IsMatch(first))
        {
            error = ReplayImportError.NotReplay;
            return null;
        }

        if (first is not (ReplayFile.Magic or ReplayFile.Magic3 or ReplayFile.PreviousMagic))
        {
            error = ReplayImportError.Version;
            return null;
        }

        error = ReplayImportError.Damaged;
        if (ReplayFile.Read(text) is not { } file || file.Header.Kind is not (ReplayFile.Constructed or ReplayFile.Battlegrounds))
        {
            return null;
        }

        try
        {
            var clean = file.Header.Kind == ReplayFile.Battlegrounds
                ? CleanBattlegrounds(file.Header, file.Body, dateOnly)
                : CleanConstructed(file.Header, file.Body, cards, dateOnly);
            if (clean is not null)
            {
                error = ReplayImportError.None;
            }

            return clean;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or OverflowException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The name an imported replay is stored under: its game's start time and a hash of the rebuilt text (the same file imported twice is found).</summary>
    public static string FileNameFor(string cleanText, ReplaySummary? summary)
    {
        var started = summary is { StartedAt.Year: > 2000 } s ? s.StartedAt.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) : "game";
        return $"{started}-{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(cleanText)))[..12]}.hpr";
    }

    private static ReplayImportResult Add(string text, ReplayStore imported, CardDb cards)
    {
        if (Sanitize(text, cards, out var error) is not { } clean)
        {
            return ReplayImportResult.Refused(error);
        }

        var name = FileNameFor(clean, ReplayFile.Read(clean)?.Header.Summary);
        if (imported.Contains(name))
        {
            return new ReplayImportResult(ReplayImportError.None, [], Already: 1);
        }

        return imported.SaveAs(name, clean) ? new ReplayImportResult(ReplayImportError.None, [name]) : ReplayImportResult.Refused(ReplayImportError.Unreadable);
    }

    /// <summary>The finished games of a Power.log, recorded as a log import does, then filtered into Imported.</summary>
    private static ReplayImportResult ImportLog(string path, FileInfo info, ReplayStore imported, CardDb cards, string workDir, CancellationToken cancellationToken)
    {
        // The log has times of day only: a session folder's name gives the day, else the file's own dates.
        var start = info.Directory is { } folder && Hearthstone.HearthstoneInstall.SessionStart(folder.FullName) is { } session
            ? session
            : info.CreationTime < info.LastWriteTime ? info.CreationTime : info.LastWriteTime;
        var work = new ReplayStore(Path.Combine(workDir, Guid.NewGuid().ToString("N"))) { AutoPrune = false };
        try
        {
            var added = new List<string>();
            var already = 0;
            foreach (var record in LogBackfill.Parse(path, start, cards, cancellationToken, work).Where(r => !r.IsAbandoned && r.Replay is not null))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (work.Load(record.Replay) is not { } text || Sanitize(text, cards, out _) is not { } clean)
                {
                    continue;
                }

                var name = FileNameFor(clean, ReplayFile.Read(clean)?.Header.Summary);
                if (imported.Contains(name))
                {
                    already++;
                }
                else if (imported.SaveAs(name, clean))
                {
                    added.Add(name);
                }
            }

            added.Reverse();
            return new ReplayImportResult(ReplayImportError.None, added, already, FromLog: true);
        }
        finally
        {
            try
            {
                if (Directory.Exists(work.Directory))
                {
                    Directory.Delete(work.Directory, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Diag.Error("Deleting the log import's work folder failed", ex);
            }
        }
    }

    private static bool IsPowerLog(string start) =>
        start.Contains("GameState.DebugPrintPower", StringComparison.Ordinal) || start.Contains("PowerTaskList.DebugPrintPower", StringComparison.Ordinal)
        || start.Contains("GameState.DebugPrintGame", StringComparison.Ordinal);

    /// <summary>The text of <paramref name="stream"/>, or null past <paramref name="max"/> characters (with <paramref name="cut"/>: its first <paramref name="max"/>).</summary>
    internal static string? ReadLimited(Stream stream, int max, bool cut = false)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 81920, leaveOpen: true);
        var text = new StringBuilder();
        var buffer = new char[81920];
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (text.Length + read > max)
            {
                return cut ? text.Append(buffer, 0, max - text.Length).ToString() : null;
            }

            text.Append(buffer, 0, read);
        }

        return text.ToString();
    }

    // ---- Constructed -----------------------------------------------------------------------------------------------

    private static string? CleanConstructed(ReplayHeader header, IReadOnlyList<string> body, CardDb cards, bool dateOnly)
    {
        // Events first, then the post-game part: once an N or K line comes, only N and K lines follow.
        var split = 0;
        while (split < body.Count && !PostGameLine().IsMatch(body[split]))
        {
            split++;
        }

        if (split > ReplayRecorder.MaxEvents || body.Count - split > MaxPostGameLines)
        {
            return null;
        }

        var events = new List<string>(split);
        var hidden = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < split; i++)
        {
            var line = body[i];
            if (!ConstructedLine().IsMatch(line))
            {
                return null;
            }

            var t = line.Split(' ');
            switch (t[0])
            {
                case "E":
                    // The opponent's hand and secrets (but a quest) are card backs: an id there is cleaned away.
                    var back = t[2] == "O" && (t[3] == "HAND" || (t[3] == "SECRET" && !IsQuest(cards, t[5])));
                    if (back)
                    {
                        hidden.Add(t[1]);
                        var origin = t.Length > 7 && t[7].StartsWith("ORIGIN=", StringComparison.Ordinal) ? " " + t[7] : string.Empty;
                        line = $"E {t[1]} O {t[3]} {t[4]} - -{origin}";
                    }
                    else
                    {
                        hidden.Remove(t[1]);
                    }

                    break;
                case "V" when hidden.Contains(t[1]):
                    continue;
                case "X":
                    hidden.Remove(t[1]);
                    break;
            }

            events.Add(line);
        }

        for (var i = split; i < body.Count; i++)
        {
            if (!PostGameLine().IsMatch(body[i]))
            {
                return null;
            }
        }

        var summary = header.Summary is { } given ? CleanSummary(given, dateOnly) : RebuildConstructedSummary(events, cards);
        if (summary is null)
        {
            return null;
        }

        var clean = new ReplayHeader { Kind = ReplayFile.Constructed, Summary = summary, Truncated = header.Truncated, Events = events.Count };
        return ReplayFile.Write(clean, events.Concat(body.Skip(split)));
    }

    private static bool IsQuest(CardDb cards, string cardId) =>
        cards.Get(cardId)?.Mechanics is { } mechanics && (mechanics.Contains("QUEST") || mechanics.Contains("QUESTLINE"));

    /// <summary>A format 2 or 3 game has no summary: the heroes from the events, the result from the <c>R</c> line.</summary>
    private static ReplaySummary RebuildConstructedSummary(IReadOnlyList<string> events, CardDb cards)
    {
        string? Hero(string side) => events.Select(e => e.Split(' '))
            .FirstOrDefault(t => t is ["E", _, var s, "PLAY", _, var card, "HERO", ..] && s == side && card != "-")?[5];

        var hero = Hero("L") ?? string.Empty;
        var opponent = Hero("O");
        var result = events.LastOrDefault(e => e.StartsWith("R ", StringComparison.Ordinal))?[2..] switch
        {
            "WON" => "Win",
            "LOST" or "CONCEDED" => "Loss",
            "TIED" => "Tie",
            _ => "Unknown",
        };
        var turns = events.Where(e => e.StartsWith("T ", StringComparison.Ordinal))
            .Select(e => int.Parse(e.Split(' ')[1], CultureInfo.InvariantCulture)).DefaultIfEmpty(0).Max();
        return new ReplaySummary
        {
            // An old file does not say when it was played: no date (the stored name starts with "game-").
            StartedAt = DateTime.MinValue,
            HeroCardId = hero,
            PlayerClass = cards.Get(hero)?.CardClass ?? string.Empty,
            OpponentHeroCardId = opponent,
            OpponentClass = opponent is null ? null : cards.Get(opponent)?.CardClass,
            Result = result,
            Turns = (turns + 1) / 2,
        };
    }

    // ---- Battlegrounds ---------------------------------------------------------------------------------------------

    private static string? CleanBattlegrounds(ReplayHeader header, IReadOnlyList<string> body, bool dateOnly)
    {
        // Format 4: the event lines, then the G line; format 3: only the rounds and lobby, as bare JSON.
        var v4 = header.Version >= ReplayFile.Version;
        var count = v4 ? body.Count - 1 : 0;
        if (body.Count == 0 || count > BattlegroundsReplayRecorder.MaxEvents || (!v4 && body.Count != 1)
            || ReplayFile.ReadBattlegrounds(body) is not { } read || CleanData(read) is not { } data)
        {
            return null;
        }

        var events = new List<string>(count);
        var shown = new Dictionary<string, char>(StringComparer.Ordinal);
        bool fight = false, opponentInView = false;
        for (var i = 0; i < count; i++)
        {
            var line = body[i];
            if (!BattlegroundsReplayGrammar.IsLine(line))
            {
                return null;
            }

            var t = line.Split(' ');
            switch (t[0])
            {
                case "F":
                    events.Add(line);
                    // Bob's offer leaves view when the combat starts.
                    LeaveView(events, shown, 'B');
                    fight = opponentInView = true;
                    continue;
                case "f":
                    fight = false;
                    break;
                case "T":
                    // The combat opponent leaves view by the next tavern.
                    LeaveView(events, shown, 'O');
                    fight = opponentInView = false;
                    break;
                case "E" when (t[2] == "B" && fight) || (t[2] == "O" && !opponentInView):
                    continue;
                case "E":
                    shown[t[1]] = t[2][0];
                    break;
                case "X" or "Y" when !shown.Remove(t[1]):
                    continue;
                case "V" or "Z" when t[1] != "L" && !shown.ContainsKey(t[1]):
                    continue;
            }

            events.Add(line);
        }

        var summary = header.Summary is { } given ? CleanSummary(given, dateOnly) : RebuildBattlegroundsSummary(data);
        if (summary is null)
        {
            return null;
        }

        var clean = new ReplayHeader { Kind = ReplayFile.Battlegrounds, Summary = summary, Truncated = header.Truncated };
        return ReplayFile.WriteBattlegrounds(clean, data, events.Count > 0 ? events : null);
    }

    /// <summary>Every card of <paramref name="side"/> still shown leaves view (<c>X</c>).</summary>
    private static void LeaveView(List<string> events, Dictionary<string, char> shown, char side)
    {
        foreach (var id in shown.Where(s => s.Value == side).Select(s => s.Key).ToList())
        {
            shown.Remove(id);
            events.Add("X " + id);
        }
    }

    private static ReplaySummary RebuildBattlegroundsSummary(BattlegroundsReplayData data)
    {
        var local = data.Lobby.FirstOrDefault(h => h.Local);
        var last = data.Rounds.Count > 0 ? data.Rounds[^1] : null;
        return new ReplaySummary
        {
            Mode = "Battlegrounds",
            // An old file does not say when it was played: no date (the stored name starts with "game-").
            StartedAt = DateTime.MinValue,
            HeroCardId = local?.HeroCardId ?? string.Empty,
            Placement = local?.Place ?? 0,
            Turns = last?.Turn ?? 0,
            TavernTier = last?.Tier ?? 0,
        };
    }

    /// <summary>The rounds and lobby from their known fields, checked; null when a card id or a list breaks the limits.</summary>
    private static BattlegroundsReplayData? CleanData(BattlegroundsReplayData data)
    {
        var rounds = Safe(data.Rounds);
        var lobby = Safe(data.Lobby);
        if (rounds.Count > 200 || lobby.Count > 8)
        {
            return null;
        }

        var clean = new List<BattlegroundsRound>(rounds.Count);
        foreach (var round in rounds)
        {
            if (round is null)
            {
                return null;
            }

            var board = Minions(round.Board);
            var opponentBoard = Minions(round.OpponentBoard);
            var shop = Safe(round.Shop);
            if (board is null || opponentBoard is null || shop.Count > 1000 || !shop.All(IsCard)
                || (round.OpponentHero is { } hero && !IsCard(hero)) || round.Result is not (null or "Win" or "Loss" or "Tie"))
            {
                return null;
            }

            clean.Add(new BattlegroundsRound
            {
                Turn = Math.Clamp(round.Turn, 0, 999),
                Tier = Math.Clamp(round.Tier, 0, 7),
                Health = Math.Clamp(round.Health, 0, 9999),
                Board = board,
                OpponentHero = round.OpponentHero,
                OpponentBoard = opponentBoard,
                Shop = [.. shop],
                Result = round.Result,
                DamageTaken = Math.Clamp(round.DamageTaken, 0, 9999),
                DamageDealt = Math.Clamp(round.DamageDealt, 0, 9999),
            });
        }

        if (lobby.Any(h => h is null || !IsCard(h.HeroCardId)))
        {
            return null;
        }

        return new BattlegroundsReplayData
        {
            Rounds = clean,
            Lobby = lobby.Select(h => new BattlegroundsLobbyHero(h.HeroCardId, Math.Clamp(h.Place, 0, 8), h.Local)).ToList(),
        };
    }

    private static List<ReplayMinion>? Minions(IReadOnlyList<ReplayMinion>? minions)
    {
        var list = Safe(minions);
        return list.Count > 14 || list.Any(m => m is null || !IsCard(m.CardId))
            ? null
            : list.Select(m => new ReplayMinion(m.CardId, Stat(m.Attack), Stat(m.Health), m.Golden)).ToList();
    }

    // ---- Summary ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The summary from its known fields, checked: no rating, the deck code only when it decodes, the archetype only as
    /// a plain name. Null when a card id or a list breaks the limits.
    /// </summary>
    private static ReplaySummary? CleanSummary(ReplaySummary s, bool dateOnly)
    {
        var tribes = Safe(s.Tribes);
        var powers = Safe(s.HeroPowers);
        var trinkets = Safe(s.Trinkets);
        var hand = Safe(s.FinalHand);
        var board = Safe(s.FinalBoard);
        if (!IsCardOrEmpty(s.HeroCardId) || (s.OpponentHeroCardId is { } opponent && !IsCard(opponent)) || (s.AnomalyCardId is { } anomaly && !IsCard(anomaly))
            || tribes.Count > 8 || !tribes.All(IsWord) || powers.Count > MaxListItems || !powers.All(IsCard)
            || trinkets.Count > MaxListItems || !trinkets.All(IsCard) || hand.Count > MaxListItems || !hand.All(IsCard)
            || board.Count > 14 || board.Any(m => m is null || !IsCard(m.CardId) || Safe(m.Keywords).Count > MaxListItems || !Safe(m.Keywords).All(IsWord)))
        {
            return null;
        }

        var started = s.StartedAt.Kind == DateTimeKind.Local ? s.StartedAt.ToUniversalTime() : DateTime.SpecifyKind(s.StartedAt, DateTimeKind.Utc);
        return new ReplaySummary
        {
            Mode = s.Mode is { } mode && ModeName().IsMatch(mode) ? mode : "Other",
            GameType = IsWordOrEmpty(s.GameType) ? s.GameType : string.Empty,
            Format = IsWordOrEmpty(s.Format) ? s.Format : string.Empty,
            // The date of play as the player saw it, without the time (shared copies).
            StartedAt = dateOnly ? DateTime.SpecifyKind(started.ToLocalTime().Date, DateTimeKind.Utc) : started,
            Seconds = Math.Clamp(s.Seconds, 0, 24 * 3600),
            HeroCardId = s.HeroCardId ?? string.Empty,
            PlayerClass = IsWordOrEmpty(s.PlayerClass) ? s.PlayerClass : string.Empty,
            OpponentHeroCardId = s.OpponentHeroCardId,
            OpponentClass = s.OpponentClass is { } opponentClass && IsWord(opponentClass) ? opponentClass : null,
            Result = s.Result is "Win" or "Loss" or "Tie" or "Abandoned" ? s.Result : "Unknown",
            Turns = Math.Clamp(s.Turns, 0, 999),
            WentFirst = s.WentFirst,
            DeckCode = s.DeckCode is { Length: <= 400 } code && DeckString.TryDecode(code) is { Cards.Count: > 0 } ? code : null,
            OpponentArchetype = s.OpponentArchetype is { } archetype && ArchetypeName().IsMatch(archetype) ? archetype : null,
            Placement = Math.Clamp(s.Placement, 0, 8),
            TavernTier = Math.Clamp(s.TavernTier, 0, 7),
            AnomalyCardId = s.AnomalyCardId,
            Tribes = [.. tribes],
            HeroPowers = [.. powers],
            Trinkets = [.. trinkets],
            FinalBoard = board.Select(m => new ReplayBgMinion(m.CardId, Stat(m.Attack), Stat(m.Health), m.Golden, [.. Safe(m.Keywords)])).ToList(),
            FinalHand = [.. hand],
        };
    }

    private static IReadOnlyList<T> Safe<T>(IReadOnlyList<T>? list) => list ?? [];

    private static int Stat(int value) => Math.Clamp(value, 0, 1_000_000);

    private static bool IsCard(string? id) => id is not null && CardId().IsMatch(id);

    private static bool IsCardOrEmpty(string? id) => id is null || id.Length == 0 || CardId().IsMatch(id);

    private static bool IsWord(string? value) => value is not null && Word().IsMatch(value);

    private static bool IsWordOrEmpty(string? value) => value is null || value.Length == 0 || Word().IsMatch(value);

    // ---- Grammar ---------------------------------------------------------------------------------------------------

    private const string Id = "[1-9][0-9]{0,6}";
    private const string IdOrNone = "(?:[1-9][0-9]{0,6}|-)";
    private const string Number = "(?:0|[1-9][0-9]{0,8})";
    private const string Value = "(?:-?" + Number + "|-)";
    private const string Small = "(?:[0-9]|[1-9][0-9])";
    private const string Card = "[A-Za-z0-9_]{1,64}";
    private const string Token = "[A-Z][A-Z0-9_]{0,31}";
    private const string CardTag = "(?:ATK|HEALTH|DAMAGE|ARMOR|COST|DURABILITY|TAUNT|DIVINE_SHIELD|STEALTH|FROZEN|SILENCED|POISONOUS|WINDFURY|REBORN|LIFESTEAL|DORMANT|IMMUNE|PREMIUM|DEATHRATTLE)";
    private const string CardType = "(?:MINION|SPELL|WEAPON|HERO|HERO_POWER|LOCATION)";

    [GeneratedRegex("^HPR[0-9]{1,3}$", RegexOptions.CultureInvariant)]
    private static partial Regex MagicLine();

    [GeneratedRegex("^" + Card + "$", RegexOptions.CultureInvariant)]
    private static partial Regex CardId();

    [GeneratedRegex("^" + Token + "$", RegexOptions.CultureInvariant)]
    private static partial Regex Word();

    [GeneratedRegex("^[A-Z][A-Za-z]{0,23}$", RegexOptions.CultureInvariant)]
    private static partial Regex ModeName();

    /// <summary>An archetype as the meta names it ("Zoo Warlock", "Зоолок"): letters, digits, spaces and a few marks, nothing like a link or a path.</summary>
    [GeneratedRegex(@"^[\p{L}\p{N}][\p{L}\p{N} '&()+\-]{0,59}$", RegexOptions.CultureInvariant)]
    private static partial Regex ArchetypeName();

    /// <summary>A Constructed event line of formats 2-4 (<c>docs/match-replay.md</c>, "File format 3"), as <see cref="ReplayRecorder"/> writes it.</summary>
    [GeneratedRegex(
        "^(?:"
        + "T (?:0|[1-9][0-9]{0,2}) [LO-] " + Number
        + "|D [LO] " + Number
        + "|E " + Id + " [LO] (?:PLAY|GRAVEYARD|HAND|SECRET) " + Small + " (?:" + Card + "|-) (?:" + CardType + "(?: " + CardTag + "=" + Value + ")*"
        + "|-(?: ORIGIN=(?:start|drawn|created|returned|stolen|coin|unknown))?)"
        + "|X " + Id
        + "|V " + Id + " " + CardTag + " " + Value
        + "|V [LO] (?:RESOURCES|RESOURCES_USED|TEMP_RESOURCES|OVERLOAD_LOCKED|OVERLOAD_OWED) " + Value
        + "|V L (?:MULLIGAN_STATE|PLAYSTATE) " + Token
        + "|B"
        + "|[PUA] " + Id + " " + IdOrNone
        + "|C " + Token + " (?:" + Card + "|-) " + Card + "(?:," + Card + "){0,63}"
        + "|c " + Card
        + "|Z " + Id + " " + Small
        + "|S (?:BEGIN_MULLIGAN|MAIN_READY|FINAL_GAMEOVER)"
        + "|R " + Token
        + ")$", RegexOptions.CultureInvariant)]
    private static partial Regex ConstructedLine();

    /// <summary>A post-game line: <c>N n entity</c>, <c>K entity card turn how created [from]</c>.</summary>
    [GeneratedRegex(
        "^(?:N " + Id + " " + Id
        + "|K " + Id + " " + Card + " (?:0|[1-9][0-9]{0,2}) (?:played|summoned|burned|shown) [01](?: [1-9][0-9]{0,8})?"
        + ")$", RegexOptions.CultureInvariant)]
    private static partial Regex PostGameLine();
}
