using System.Globalization;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;

namespace Hearthpulse.Core.Replay;

/// <summary>A number on the analysis tab.</summary>
public sealed record AnalysisTile(string Label, string Value, string Detail);

/// <summary>The table at the end of one game turn.</summary>
public sealed record AnalysisTurnRow(
    int Turn,
    string Label,
    bool Local,
    int LocalHealth,
    int OpponentHealth,
    string LocalBoard,
    string OpponentBoard,
    string Mana,
    string Played);

/// <summary>A card that swung the game, with why.</summary>
public sealed record KeyCardRow(string CardId, string Name, bool Local, string Reason, double Score);

/// <summary>One opening card: what the player did and what the archetype's players usually do.</summary>
public sealed record MulliganCardRow(string CardId, string Name, bool Kept, string Choice, string MetaKeep, string MetaWinRate, int Agreement)
{
    /// <summary>The choice matches what most of the archetype's players do.</summary>
    public bool Agrees => Agreement > 0;

    /// <summary>The choice goes against a clear majority of the archetype's players.</summary>
    public bool Differs => Agreement < 0;
}

/// <summary>
/// The analysis tab of a Constructed replay: health and board per turn, mana use, the cards that swung the game
/// (simple heuristics on what the replay shows), the mulligan against the archetype's community numbers, and the meta
/// standing of both decks. Built only from the replay file (what the player saw) and the public meta.
/// </summary>
public sealed class MatchAnalysis
{
    private MatchAnalysis()
    {
    }

    public IReadOnlyList<AnalysisTile> Tiles { get; private init; } = [];

    public IReadOnlyList<AnalysisTurnRow> TurnRows { get; private init; } = [];

    /// <summary>Hero health plus armor at the end of each game turn, yours and the opponent's (chart series).</summary>
    public IReadOnlyList<double?> LocalHealth { get; private init; } = [];

    public IReadOnlyList<double?> OpponentHealth { get; private init; } = [];

    /// <summary>Total attack plus health of the minions in play at the end of each game turn.</summary>
    public IReadOnlyList<double?> LocalBoard { get; private init; } = [];

    public IReadOnlyList<double?> OpponentBoard { get; private init; } = [];

    /// <summary>Share of the mana you spent on each of your turns (0..1), and the opponent's on theirs.</summary>
    public IReadOnlyList<double> LocalMana { get; private init; } = [];

    public IReadOnlyList<double> OpponentMana { get; private init; } = [];

    /// <summary>A full-mana tick (1.0) per bar of <see cref="LocalMana"/>, for the chart's comparison marks.</summary>
    public IReadOnlyList<double> ManaFull => LocalMana.Select(_ => 1.0).ToList();

    public double HealthMaximum { get; private init; } = 30;

    public double BoardMaximum { get; private init; } = 10;

    public IReadOnlyList<KeyCardRow> KeyCards { get; private init; } = [];

    public IReadOnlyList<MulliganCardRow> Mulligan { get; private init; } = [];

    public IReadOnlyList<string> MulliganReceived { get; private init; } = [];

    public string MulliganReceivedText => string.Join(", ", MulliganReceived);

    public string MulliganNote { get; private init; } = string.Empty;

    /// <summary>Lines comparing the game with the meta: archetypes' win rates, going first or on the coin.</summary>
    public IReadOnlyList<MatchFact> Meta { get; private init; } = [];

    public string MetaNote { get; private init; } = string.Empty;

    /// <param name="opponentArchetype">The opponent's archetype as the page names it (saved with the game, or guessed from its cards).</param>
    public static MatchAnalysis Build(GameRecord game, MatchReplay replay, CardDb cards, ConstructedMeta? meta, string? opponentArchetype = null)
    {
        var frames = replay.Frames;
        var turns = replay.Turns.Where(t => t.Turn > 0).ToList();
        var ends = turns.Select(t => frames[Math.Min(t.LastFrame, frames.Count - 1)]).ToList();
        var rows = turns.Zip(ends, (turn, end) => TurnRow(turn, end, frames, cards)).ToList();

        var localMana = new List<double>();
        var opponentMana = new List<double>();
        foreach (var (turn, end) in turns.Zip(ends))
        {
            // The turn the game ended in was cut short: its mana says nothing about how it was played.
            if (end.Action.Kind == ReplayActionKind.End)
            {
                continue;
            }

            if (turn.Player is { } player && ManaShare(end.Of(player)) is { } share)
            {
                (player == ReplayPlayer.Local ? localMana : opponentMana).Add(share);
            }
        }

        var localHealth = ends.Select(e => (double?)Durability(e.Local)).ToList();
        var opponentHealth = ends.Select(e => (double?)Durability(e.Opponent)).ToList();
        var localBoard = ends.Select(e => (double?)Power(e.Local)).ToList();
        var opponentBoard = ends.Select(e => (double?)Power(e.Opponent)).ToList();
        var (keyCards, dealt, taken, kills) = Swings(frames, cards);
        var archetype = OurArchetype(game, replay, cards, meta);
        var (mulligan, mulliganNote) = MulliganRows(replay, archetype, cards, meta);

        var tiles = new List<AnalysisTile>
        {
            new(Loc.T("Match.Analysis.Damage"), dealt.ToString(CultureInfo.CurrentCulture), Loc.F("Match.Analysis.DamageTaken", taken)),
            new(Loc.T("Match.Analysis.Deaths"), kills.Local.ToString(CultureInfo.CurrentCulture), Loc.F("Match.Analysis.DeathsOwn", kills.Opponent)),
            new(Loc.T("Match.Analysis.Mana"), localMana.Count > 0 ? localMana.Average().ToString("P0", CultureInfo.CurrentCulture) : "—",
                opponentMana.Count > 0
                    ? Loc.F("Match.Analysis.ManaOpponent", opponentMana.Average().ToString("P0", CultureInfo.CurrentCulture))
                    : Loc.T("Match.Analysis.ManaOwnTurns")),
            new(Loc.T("Match.Analysis.Played"), replay.LocalPlayed.Sum(c => c.Count).ToString(CultureInfo.CurrentCulture),
                Loc.F("Match.Analysis.PlayedOpponent", replay.OpponentPlayed.Sum(c => c.Count))),
        };

        return new MatchAnalysis
        {
            Tiles = tiles,
            TurnRows = rows,
            LocalHealth = localHealth,
            OpponentHealth = opponentHealth,
            LocalBoard = localBoard,
            OpponentBoard = opponentBoard,
            LocalMana = localMana,
            OpponentMana = opponentMana,
            HealthMaximum = Math.Max(30, localHealth.Concat(opponentHealth).Max() ?? 30),
            BoardMaximum = Math.Max(10, localBoard.Concat(opponentBoard).Max() ?? 10),
            KeyCards = keyCards,
            Mulligan = mulligan,
            MulliganReceived = replay.Mulligan?.Received.Select(cards.NameOf).ToList() ?? [],
            MulliganNote = mulliganNote,
            Meta = MetaFacts(game, archetype, opponentArchetype ?? game.OpponentArchetype, meta),
            MetaNote = meta is null
                ? Loc.T("Match.Meta.NotLoaded")
                : string.Join(" · ", meta.Source, ConstructedMeta.Label(ConstructedMeta.Ranks, meta.Rank), ConstructedMeta.Label(ConstructedMeta.Periods, meta.Period)),
        };
    }

    private static AnalysisTurnRow TurnRow(ReplayTurn turn, ReplayFrame end, IReadOnlyList<ReplayFrame> frames, CardDb cards)
    {
        var local = turn.Player == ReplayPlayer.Local;
        var played = frames.Skip(turn.FirstFrame).Take(turn.LastFrame - turn.FirstFrame + 1)
            .Where(f => f.Action.Kind == ReplayActionKind.Play && f.Action.CardId is not null)
            .Select(f => cards.NameOf(f.Action.CardId))
            .ToList();
        var acting = turn.Player is { } p ? end.Of(p) : null;
        return new AnalysisTurnRow(
            turn.Turn,
            Loc.F("Match.Analysis.TurnLabel", turn.PlayerTurn, Loc.T(local ? "Match.You" : "Match.Opponent")),
            local,
            Durability(end.Local),
            Durability(end.Opponent),
            $"{end.Local.BoardAttack}/{end.Local.BoardHealth}",
            $"{end.Opponent.BoardAttack}/{end.Opponent.BoardHealth}",
            acting is { Mana: > 0 } ? $"{Math.Min(acting.ManaUsed, acting.Mana)}/{acting.Mana}" : "—",
            string.Join(", ", played));
    }

    /// <summary>The share of this turn's mana the player spent; locked overload counts as neither.</summary>
    private static double? ManaShare(ReplayBoard board)
    {
        var available = board.Mana - board.OverloadLocked;
        if (available <= 0)
        {
            return null;
        }

        return Math.Clamp((board.ManaUsed - board.OverloadLocked) / (double)available, 0, 1);
    }

    private static int Durability(ReplayBoard board) => board.Health + board.Armor;

    private static int Power(ReplayBoard board) => board.BoardAttack + board.BoardHealth;

    /// <summary>
    /// The actions that moved the game most: for each play, hero power or attack, the board it gained against the
    /// opponent's (attack plus health of minions), plus face damage. The best action of each card counts; the top three
    /// of each player are kept. A heuristic on what the replay shows, not a simulation.
    /// </summary>
    private static (IReadOnlyList<KeyCardRow> Rows, int Dealt, int Taken, (int Local, int Opponent) Kills) Swings(IReadOnlyList<ReplayFrame> frames, CardDb cards)
    {
        var best = new Dictionary<(string Card, bool Local), (double Score, string Reason)>();
        int dealt = 0, taken = 0, killed = 0, lost = 0;
        for (var i = 1; i < frames.Count; i++)
        {
            var (before, after) = (frames[i - 1], frames[i]);
            var action = after.Action;
            dealt += Math.Max(0, Durability(before.Opponent) - Durability(after.Opponent));
            taken += Math.Max(0, Durability(before.Local) - Durability(after.Local));
            killed += action.Deaths.Count(d => d.Player == ReplayPlayer.Opponent && d.Type == "MINION");
            lost += action.Deaths.Count(d => d.Player == ReplayPlayer.Local && d.Type == "MINION");
            if (action.Kind is not (ReplayActionKind.Play or ReplayActionKind.HeroPower or ReplayActionKind.Attack or ReplayActionKind.Use)
                || action.Player is not { } player || action.CardId is not { } card)
            {
                continue;
            }

            var other = player == ReplayPlayer.Local ? ReplayPlayer.Opponent : ReplayPlayer.Local;
            var face = Math.Max(0, Durability(before.Of(other)) - Durability(after.Of(other)));
            var swing = Power(after.Of(player)) - Power(before.Of(player)) - (Power(after.Of(other)) - Power(before.Of(other)));
            var kills = action.Deaths.Count(d => d.Player == other && d.Type == "MINION");
            var lethal = Durability(after.Of(other)) <= 0 && Durability(before.Of(other)) > 0;
            var score = swing + face + (lethal ? 100 : 0);
            if (score <= 2)
            {
                continue;
            }

            var reason = new List<string> { Loc.F("Match.Key.Turn", after.PlayerTurn) };
            if (lethal) reason.Add(Loc.T("Match.Key.Lethal"));
            if (kills > 0) reason.Add(Loc.P("Match.Key.Killed", kills));
            if (face > 0) reason.Add(Loc.F("Match.Key.Face", face));
            if (swing > 0 && kills == 0) reason.Add(Loc.F("Match.Key.Board", swing));
            var key = (card, player == ReplayPlayer.Local);
            if (!best.TryGetValue(key, out var known) || score > known.Score)
            {
                best[key] = (score, string.Join(" · ", reason));
            }
        }

        var rows = best
            .GroupBy(b => b.Key.Local)
            .SelectMany(g => g.OrderByDescending(b => b.Value.Score).Take(3))
            .Select(b => new KeyCardRow(b.Key.Card, cards.NameOf(b.Key.Card), b.Key.Local, b.Value.Reason, b.Value.Score))
            .OrderByDescending(r => r.Local)
            .ThenByDescending(r => r.Score)
            .ToList();
        return (rows, dealt, taken, (killed, lost));
    }

    /// <summary>Our deck's archetype in the meta, from its deck code (else the cards we were seen with).</summary>
    private static MetaArchetype? OurArchetype(GameRecord game, MatchReplay replay, CardDb cards, ConstructedMeta? meta)
    {
        if (meta is null || ConstructedMeta.FormatKeyOf(game.Format) != meta.Format)
        {
            return null;
        }

        var deck = game.DeckCode is { Length: > 0 } code ? DeckString.TryDecode(code) : null;
        var ids = deck is not null
            ? deck.Cards.Keys.Select(cards.Get).OfType<Card>().Select(c => c.Id).ToList()
            : replay.LocalPlayed.Select(c => c.CardId).Concat(replay.LocalDrawn.Select(c => c.CardId)).Distinct().ToList();
        var heroClass = string.IsNullOrEmpty(game.PlayerClass) || game.PlayerClass == "NEUTRAL"
            ? deck?.HeroDbfIds.Select(cards.Get).FirstOrDefault(c => c is not null)?.CardClass
            : game.PlayerClass;
        return meta.Match(heroClass, ids);
    }

    private static (IReadOnlyList<MulliganCardRow> Rows, string Note) MulliganRows(MatchReplay replay, MetaArchetype? archetype, CardDb cards, ConstructedMeta? meta)
    {
        if (replay.Mulligan is not { } mulligan)
        {
            return ([], Loc.T("Match.Mulligan.NotRecorded"));
        }

        var kept = mulligan.Kept.ToList();
        var stats = archetype?.Mulligan.ToDictionary(m => m.CardId, StringComparer.Ordinal) ?? [];
        var rows = new List<MulliganCardRow>();
        foreach (var card in mulligan.Offered)
        {
            var wasKept = kept.Remove(card);
            var stat = stats.GetValueOrDefault(card);
            // Agreement: +1 when the choice matches what most of the archetype's players do, -1 when it goes against a
            // clear majority (keep rate above 60% or below 40%), 0 when the meta is split or has no number.
            var agreement = stat is null ? 0
                : stat.KeepRate >= 0.6 ? (wasKept ? 1 : -1)
                : stat.KeepRate <= 0.4 ? (wasKept ? -1 : 1)
                : 0;
            rows.Add(new MulliganCardRow(
                card,
                cards.NameOf(card),
                wasKept,
                Loc.T(wasKept ? "Match.Mulligan.Kept" : "Match.Mulligan.Replaced"),
                stat is null ? "—" : stat.KeepRate.ToString("P0", CultureInfo.CurrentCulture),
                stat is null || stat.InHand == 0 ? "—" : stat.InHandWinRate.ToString("P1", CultureInfo.CurrentCulture),
                agreement));
        }

        var note = archetype is null
            ? Loc.T(meta is null ? "Match.Mulligan.NeedsMeta" : "Match.Mulligan.NoArchetype")
            : Loc.F("Match.Mulligan.Note", archetype.Name, rows.Count(r => r.Agreement > 0), rows.Count(r => r.Agreement != 0));
        return (rows, note);
    }

    private static IReadOnlyList<MatchFact> MetaFacts(GameRecord game, MetaArchetype? ours, string? opponentArchetype, ConstructedMeta? meta)
    {
        var facts = new List<MatchFact>();
        if (meta is null)
        {
            return facts;
        }

        if (ours is not null)
        {
            facts.Add(new MatchFact(Loc.T("Match.Meta.Yours"), Loc.F("Match.Meta.YoursValue", ours.Name, Percent(ours.WinRate), ours.Games.ToString("N0", CultureInfo.CurrentCulture))));
            if (game.WentFirst is { } first && (first ? ours.FirstGames : ours.CoinGames) > 0)
            {
                var rate = first ? ours.FirstWinRate : ours.CoinWinRate;
                facts.Add(new MatchFact(Loc.T(first ? "Match.Meta.First" : "Match.Meta.Coin"), Loc.F("Match.Meta.Wins", ours.Name, Percent(rate))));
            }
        }
        else
        {
            facts.Add(new MatchFact(Loc.T("Match.Meta.Yours"), Loc.T("Match.Meta.NoMatch")));
        }

        var theirs = opponentArchetype is { Length: > 0 } name
            ? meta.ForClass(game.OpponentClass).FirstOrDefault(a => a.Name == name)
            : null;
        if (theirs is not null)
        {
            var value = Loc.F("Match.Meta.TheirsValue", theirs.Name, Percent(theirs.WinRate));
            facts.Add(new MatchFact(Loc.T("Match.Meta.Theirs"), theirs.Share > 0 ? Loc.F("Match.Meta.Share", value, Percent(theirs.Share)) : value));
        }
        else if (opponentArchetype is { Length: > 0 } guessed)
        {
            facts.Add(new MatchFact(Loc.T("Match.Meta.Theirs"), guessed));
        }

        if (ours is not null && theirs is not null)
        {
            var edge = ours.WinRate - theirs.WinRate;
            facts.Add(new MatchFact(Loc.T("Match.Meta.OnPaper"), Math.Abs(edge) < 0.01 ? Loc.T("Match.Meta.Even")
                : edge > 0 ? Loc.F("Match.Meta.OursStronger", Percent(edge)) : Loc.F("Match.Meta.TheirsStronger", Percent(-edge))));
        }

        return facts;
    }

    private static string Percent(double share) => share.ToString("P1", CultureInfo.CurrentCulture);
}
