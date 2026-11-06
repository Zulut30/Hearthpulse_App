using System.Globalization;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Memory;
using Hearthpulse.Core.Stats;

namespace Hearthpulse.Core.Replay;

/// <summary>
/// Everything the match page shows for one history game, built off the UI thread: header, the Constructed replay
/// (steps, turns, actions, decks) or the Battlegrounds rounds and lobby, and the analysis. Both apps bind to it.
/// </summary>
public sealed partial class MatchPage
{
    // MDI glyphs for the action list (Material Design Icons code points, as in the rest of the app).
    private const string IconPlay = "\U000F063A";
    private const string IconAttack = "\U000F0787";
    private const string IconPower = "\U000F140B";
    private const string IconChoice = "\U000F0AE2";
    private const string IconTurn = "\U000F0467";
    private const string IconEnd = "\U000F023B";

    private readonly CardDb _cards;

    private MatchPage(GameRecord game, CardDb cards, MatchHeader header)
    {
        Game = game;
        _cards = cards;
        Header = header;
    }

    public GameRecord Game { get; }

    public MatchHeader Header { get; }

    /// <summary>Why there is no replay to show, or null when there is one.</summary>
    public string? Missing { get; private init; }

    public MatchReplay? Replay { get; private init; }

    public BattlegroundsReplayData? Battlegrounds { get; private init; }

    public IReadOnlyList<MatchTurnChip> Turns { get; private init; } = [];

    /// <summary>Our deck list (the deck code of the game, else the cards we were seen with).</summary>
    public IReadOnlyList<MatchDeckRow> LocalDeck { get; private init; } = [];

    public string LocalDeckNote { get; private init; } = string.Empty;

    /// <summary>
    /// Every opponent card the log revealed by the end of the game (played, summoned, burned, shown), with counts; for a
    /// format 2 file, the cards it played face up.
    /// </summary>
    public IReadOnlyList<MatchDeckRow> OpponentCards { get; private init; } = [];

    /// <summary>The meta deck the opponent's archetype most likely played: a guess, marked as one.</summary>
    public IReadOnlyList<MatchDeckRow> OpponentPredicted { get; private init; } = [];

    /// <summary>Where the guessed list comes from ("Guess: the most played Zoolock list in the meta, 1,204 games").</summary>
    public string OpponentPredictedNote { get; private init; } = string.Empty;

    /// <summary>The opponent's archetype from the cards it showed ("Zoolock"), when the call is clear.</summary>
    public string? OpponentArchetype { get; private init; }

    public IReadOnlyList<MatchRoundView> Rounds { get; private init; } = [];

    public IReadOnlyList<MatchLobbyRow> Lobby { get; private init; } = [];

    /// <summary>The analysis tab, for Constructed replays.</summary>
    public MatchAnalysis? Analysis { get; private init; }

    public bool IsBattlegrounds => Game.IsBattlegrounds;

    public int FrameCount => Replay?.Frames.Count ?? BgReplay?.Frames.Count ?? 0;

    /// <summary>The recording stopped adding steps at its cap: the player says the replay is incomplete.</summary>
    public bool Truncated => Replay?.Header.Truncated ?? BgReplay?.Header.Truncated ?? false;

    /// <summary>Loads the page for a history game: its replay file, the card names and the meta for the archetype calls.</summary>
    public static MatchPage Load(GameRecord game, ReplayStore store, CardDb cards, ConstructedMeta? meta, ArchetypeTranslations? names = null) =>
        Load(game, store.Load(game.Replay), cards, meta, names);

    /// <summary>Loads the page for a history game from its replay text (null: no file), e.g. a starred copy (<c>MatchPage.File.cs</c>).</summary>
    /// <param name="names">The Russian archetype names fetched this run, for the archetype saved with the game.</param>
    public static MatchPage Load(GameRecord game, string? text, CardDb cards, ConstructedMeta? meta, ArchetypeTranslations? names = null)
    {
        names ??= ArchetypeTranslations.Empty;
        var file = text is null ? null : ReplayFile.Read(text);
        var header = BuildHeader(game, cards, file?.Header, names);
        if (file is null)
        {
            return new MatchPage(game, cards, header)
            {
                Missing = game.Replay is null ? Loc.T("Match.Missing.NotRecorded") : Loc.T("Match.Missing.Gone"),
            };
        }

        if (file.Value.Header.Kind == ReplayFile.Battlegrounds)
        {
            var data = ReplayFile.ReadBattlegrounds(file.Value.Body);
            if (data is null)
            {
                return new MatchPage(game, cards, header) { Missing = Loc.T("Match.Missing.Unreadable") };
            }

            return new MatchPage(game, cards, header)
            {
                Battlegrounds = data,
                BgReplay = BattlegroundsMatchReplay.From(file.Value.Header, file.Value.Body),
                Rounds = data.Rounds.Select(r => RoundView(r, cards)).ToList(),
                Lobby = data.Lobby.Select(h => new MatchLobbyRow(h.HeroCardId, cards.NameOf(h.HeroCardId), Ordinal(h.Place), h.Local)).ToList(),
            };
        }

        var replay = MatchReplay.Parse(text!);
        if (replay is null || replay.Frames.Count == 0)
        {
            return new MatchPage(game, cards, header) { Missing = Loc.T("Match.Missing.Unreadable") };
        }

        // Deck cards the opponent showed: everything the log revealed by the end (format 3), else what it played.
        var shown = replay.HasPostGame
            ? replay.Reveals.Where(r => !r.Created).Select(r => r.CardId).Concat(replay.OpponentPlayed.Select(c => c.CardId)).Distinct(StringComparer.Ordinal).ToList()
            : replay.OpponentPlayed.Select(c => c.CardId).ToList();
        var fits = ArchetypeGuess.FitsFormat(meta, game.Format);
        var guessed = fits ? ArchetypeGuess.FromSeenCards(meta, game.OpponentClass, shown) : null;
        // A game keeps the name its meta had when it was saved, maybe the English one: it is shown in the interface language.
        var archetype = (game.OpponentArchetype is { Length: > 0 } saved ? names.Display(saved) : null) ?? guessed?.Name;
        var metaArchetype = fits && archetype is not null
            ? meta!.ForClass(game.OpponentClass).FirstOrDefault(a => string.Equals(a.Name, archetype, StringComparison.OrdinalIgnoreCase)) ?? guessed
            : null;
        // Without a clear call the guessed list still comes from the closest archetype (the page marks it a guess).
        var closest = metaArchetype is null && fits ? ArchetypeGuess.Closest(meta, game.OpponentClass, shown) : null;
        var (deck, deckNote) = LocalDeckRows(game, replay, cards);
        var (predicted, predictedNote) = PredictedRows(meta, metaArchetype ?? closest?.Archetype, closest?.Hits, shown, cards);
        return new MatchPage(game, cards, header)
        {
            Replay = replay,
            Turns = TurnChips(replay),
            LocalDeck = deck,
            LocalDeckNote = deckNote,
            OpponentCards = replay.HasPostGame
                ? RevealedRows(replay, cards)
                : replay.OpponentPlayed
                    .Select(c => new MatchDeckRow(c.CardId, cards.NameOf(c.CardId), cards.Get(c.CardId)?.Cost ?? 0, c.Count > 1 ? $"×{c.Count}" : string.Empty,
                        Loc.F("Match.Deck.Turn", c.Turn), true) { Rarity = cards.Get(c.CardId)?.Rarity ?? string.Empty })
                    .ToList(),
            OpponentPredicted = predicted,
            OpponentPredictedNote = predictedNote,
            OpponentArchetype = archetype,
            Analysis = MatchAnalysis.Build(game, replay, cards, meta, archetype),
        };
    }

    /// <summary>
    /// The opponent's revealed cards by card: deck cards first (cheapest first), then the ones created during the game;
    /// each with how many copies came to light, how, and the first turn.
    /// </summary>
    private static IReadOnlyList<MatchDeckRow> RevealedRows(MatchReplay replay, CardDb cards) =>
        replay.Reveals
            .GroupBy(r => (Card: Original(r, cards), r.Created))
            .Select(g =>
            {
                var hows = g.GroupBy(r => r.How).OrderBy(h => HowOrder(h.Key))
                    .Select(h => h.Count() > 1 ? Loc.F("Match.Reveal.HowTimes", Loc.T("Match.Reveal." + h.Key), h.Count()) : Loc.T("Match.Reveal." + h.Key));
                var note = string.Join(" · ", hows.Prepend(Loc.F("Match.Deck.Turn", g.Min(r => r.Turn)))
                    .Concat(g.Key.Created ? [Loc.T("Match.Reveal.Created")] : []));
                var count = g.Count();
                return new MatchDeckRow(g.Key.Card, cards.NameOf(g.Key.Card), cards.Get(g.Key.Card)?.Cost ?? 0, count > 1 ? $"×{count}" : string.Empty, note,
                    !g.Key.Created) { Rarity = cards.Get(g.Key.Card)?.Rarity ?? string.Empty };
            })
            .OrderBy(r => r.Used ? 0 : 1).ThenBy(r => r.Cost).ThenBy(r => r.Name, StringComparer.CurrentCulture)
            .ToList();

    private static int HowOrder(string how) => how switch { "played" => 0, "summoned" => 1, "burned" => 2, _ => 3 };

    /// <summary>The card a revealed entity was in hand: the one it was transformed from when the game said so.</summary>
    private static string Original(ReplayReveal reveal, CardDb cards) =>
        reveal.TransformedFrom > 0 && cards.Get(reveal.TransformedFrom)?.Id is { } from ? from : reveal.CardId;

    /// <summary>
    /// The most played meta list of the opponent's archetype, the cards it showed marked; else the archetype's core
    /// cards. A guess: the page says so.
    /// </summary>
    /// <param name="closestHits">Set when the archetype is only the closest one, not a clear call: how many shown cards it holds.</param>
    private static (IReadOnlyList<MatchDeckRow> Rows, string Note) PredictedRows(ConstructedMeta? meta, MetaArchetype? archetype, int? closestHits,
        IReadOnlyCollection<string> shown, CardDb cards)
    {
        if (meta is null || archetype is null)
        {
            return ([], string.Empty);
        }

        var seen = new HashSet<string>(shown, StringComparer.Ordinal);
        if (LikelyDeck.ListOf(meta, archetype, cards) is not { } likely)
        {
            return ([], string.Empty);
        }

        var list = (likely.Cards, Note: likely.Games > 0
            ? Loc.F("Match.Predicted.Note", archetype.Name, likely.Games.ToString("N0", CultureInfo.CurrentCulture))
            : Loc.F("Match.Predicted.Core", archetype.Name));
        var rows = list.Cards
            .Select(c => new MatchDeckRow(c.Id, cards.NameOf(c.Id), cards.Get(c.Id)?.Cost ?? 0, c.Copies > 1 ? $"×{c.Copies}" : string.Empty,
                seen.Contains(c.Id) ? Loc.T("Match.Predicted.Seen") : string.Empty, seen.Contains(c.Id)) { Rarity = cards.Get(c.Id)?.Rarity ?? string.Empty })
            .OrderBy(r => r.Cost).ThenBy(r => r.Name, StringComparer.CurrentCulture)
            .ToList();
        return (rows, closestHits is { } hits ? $"{list.Note} {Loc.P("Match.Predicted.Closest", hits)}" : list.Note);
    }

    private static (IReadOnlyList<MatchDeckRow> Rows, string Note) LocalDeckRows(GameRecord game, MatchReplay replay, CardDb cards)
    {
        var drawn = Counts(replay.LocalDrawn.Select(c => (c.CardId, c.Count)).Concat((replay.Mulligan?.Kept ?? []).Concat(replay.Mulligan?.Received ?? []).Select(id => (id, 1))));
        var played = Counts(replay.LocalPlayed.Select(c => (c.CardId, c.Count)));
        var deck = game.DeckCode is { Length: > 0 } code ? DeckString.TryDecode(code) : null;
        IEnumerable<(string Id, int Copies)> list;
        string note;
        if (deck is not null && deck.Cards.Count > 0)
        {
            list = deck.Cards.Select(kv => (cards.Get(kv.Key)?.Id ?? kv.Key.ToString(CultureInfo.InvariantCulture), kv.Value));
            note = Loc.F("Match.Deck.Note", game.DeckName ?? Loc.T("Match.Deck.Default"), deck.CardCount);
        }
        else
        {
            // No deck code for this game: the cards it was seen to hold.
            list = drawn.Select(kv => (kv.Key, kv.Value));
            note = Loc.T("Match.Deck.NoCode");
        }

        var rows = list
            .Select(c =>
            {
                var card = cards.Get(c.Id);
                var d = drawn.GetValueOrDefault(c.Id);
                var p = played.GetValueOrDefault(c.Id);
                var marks = new List<string>();
                if (d > 0) marks.Add(d > 1 ? Loc.F("Match.Deck.DrawnTimes", d) : Loc.T("Match.Deck.Drawn"));
                if (p > 0) marks.Add(p > 1 ? Loc.F("Match.Deck.PlayedTimes", p) : Loc.T("Match.Deck.Played"));
                return new MatchDeckRow(c.Id, cards.NameOf(c.Id), card?.Cost ?? 0, c.Copies > 1 ? $"×{c.Copies}" : string.Empty, string.Join(" · ", marks), d > 0) { Rarity = card?.Rarity ?? string.Empty };
            })
            .OrderBy(r => r.Cost).ThenBy(r => r.Name, StringComparer.CurrentCulture)
            .ToList();
        return (rows, note);
    }

    private static Dictionary<string, int> Counts(IEnumerable<(string Id, int Count)> cards)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (id, count) in cards)
        {
            counts[id] = counts.GetValueOrDefault(id) + count;
        }

        return counts;
    }

    private static MatchRoundView RoundView(BattlegroundsRound round, CardDb cards)
    {
        var (result, key) = round.Result switch
        {
            "Win" => (Loc.F("Match.Round.Won", round.DamageDealt), "Win"),
            "Loss" => (Loc.F("Match.Round.Lost", round.DamageTaken), "Loss"),
            "Tie" => (Loc.T("Match.Round.Tie"), "Tie"),
            _ => (Loc.T("Match.Round.NoCombat"), "Unknown"),
        };
        var detail = string.Join(" · ", new[]
        {
            Loc.F("Match.Round.Tier", round.Tier),
            round.Health > 0 ? Loc.F("Match.Round.Health", round.Health) : null,
            round.Shop.Count > 0 ? Loc.P("Match.Round.Offers", round.Shop.Count) : null,
        }.Where(x => x is not null));
        return new MatchRoundView(
            round.Turn,
            Loc.F("Match.Round.Title", round.Turn),
            detail,
            result,
            key,
            round.OpponentHero,
            round.OpponentHero is { } h ? cards.NameOf(h) : string.Empty,
            round.Board.Select(m => Minion(m, cards)).ToList(),
            round.OpponentBoard.Select(m => Minion(m, cards)).ToList(),
            round.Shop.Select(id => new MatchCardView(id, cards.NameOf(id), false)
            {
                Attack = cards.Get(id)?.Attack ?? 0,
                Health = cards.Get(id)?.Health ?? 0,
                ShowStats = cards.Get(id)?.Type == "MINION",
                Tooltip = cards.NameOf(id),
            }).ToList());
    }

    private static MatchCardView Minion(ReplayMinion m, CardDb cards) => new(m.CardId, cards.NameOf(m.CardId), false)
    {
        Attack = m.Attack,
        Health = m.Health,
        ShowStats = true,
        Golden = m.Golden,
        Tooltip = m.Golden ? Loc.F("Match.Round.Golden", cards.NameOf(m.CardId)) : cards.NameOf(m.CardId),
    };
}
