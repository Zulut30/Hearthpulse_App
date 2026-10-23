using System.Globalization;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Memory;
using Hearthpulse.Core.Stats;

namespace Hearthpulse.Core.Replay;

/// <summary>The match page's header: both players, the mode, the result.</summary>
public sealed record MatchHeader(
    string Title,
    string Subtitle,
    string Result,
    string ResultKey,
    MatchPlayerView Local,
    MatchPlayerView Opponent)
{
    /// <summary>Header facts as label/value pairs: duration, turns, coin, deck...</summary>
    public IReadOnlyList<MatchFact> Facts { get; init; } = [];
}

public sealed record MatchPlayerView(string Name, string HeroCardId, string Hero, string Class, string Rank);

public sealed record MatchFact(string Label, string Value);

/// <summary>A card at one step, ready to show. <see cref="CardBack"/> cards have no id and no name.</summary>
public sealed record MatchCardView(string? CardId, string Name, bool CardBack)
{
    public int Attack { get; init; }

    public int Health { get; init; }

    public int Cost { get; init; }

    /// <summary>Attack and health are worth showing (minions, weapons, heroes).</summary>
    public bool ShowStats { get; init; }

    public bool Damaged { get; init; }

    public bool Golden { get; init; }

    public bool Taunt { get; init; }

    public bool DivineShield { get; init; }

    /// <summary>A short note under the card: "Turn 3", "Created", "Frozen".</summary>
    public string Note { get; init; } = string.Empty;

    public string Tooltip { get; init; } = string.Empty;
}

/// <summary>One side of the table at a step.</summary>
public sealed record MatchSideView(
    string Label,
    bool IsLocal,
    string HeroCardId,
    string Hero,
    int Health,
    int Armor,
    MatchCardView? Weapon,
    MatchCardView? HeroPower,
    IReadOnlyList<MatchCardView> Board,
    IReadOnlyList<MatchCardView> Hand,
    IReadOnlyList<MatchCardView> Secrets,
    string Mana,
    string Deck)
{
    public string HealthText => Armor > 0 ? $"{Health} + {Armor}" : Health.ToString(CultureInfo.CurrentCulture);
}

/// <summary>A step of the replay as shown: the table from the chosen side and what happened.</summary>
public sealed record MatchStepView(int Index, int Count, string Turn, string Action, string Detail, bool LocalActs, MatchSideView Top, MatchSideView Bottom);

/// <summary>A turn chip in the turn strip.</summary>
public sealed record MatchTurnChip(int Number, string Label, string Tooltip, bool Local, bool Mulligan, int FirstFrame, int LastFrame);

/// <summary>An action in the turn's list (clicking it goes to its step).</summary>
public sealed record MatchActionRow(int Frame, string Icon, string Text, bool Local, string? CardId, bool Current);

/// <summary>A card of a deck list: ours with draw/play marks, or one the opponent showed with its turn.</summary>
public sealed record MatchDeckRow(string CardId, string Name, int Cost, string Count, string Note, bool Used);

/// <summary>A Battlegrounds round as shown.</summary>
public sealed record MatchRoundView(
    int Turn,
    string Title,
    string Detail,
    string Result,
    string ResultKey,
    string? OpponentHeroCardId,
    string OpponentHero,
    IReadOnlyList<MatchCardView> Board,
    IReadOnlyList<MatchCardView> OpponentBoard,
    IReadOnlyList<MatchCardView> Shop);

public sealed record MatchLobbyRow(string HeroCardId, string Hero, string Place, bool Local);

/// <summary>
/// Everything the match page shows for one history game, built off the UI thread: header, the Constructed replay
/// (steps, turns, actions, decks) or the Battlegrounds rounds and lobby, and the analysis. Both apps bind to it.
/// </summary>
public sealed class MatchPage
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

    /// <summary>The cards the opponent played from hand, with the turn each was first played.</summary>
    public IReadOnlyList<MatchDeckRow> OpponentCards { get; private init; } = [];

    /// <summary>The opponent's archetype from the cards it showed ("Zoolock"), when the call is clear.</summary>
    public string? OpponentArchetype { get; private init; }

    public IReadOnlyList<MatchRoundView> Rounds { get; private init; } = [];

    public IReadOnlyList<MatchLobbyRow> Lobby { get; private init; } = [];

    /// <summary>The analysis tab, for Constructed replays.</summary>
    public MatchAnalysis? Analysis { get; private init; }

    public bool IsBattlegrounds => Game.IsBattlegrounds;

    public int FrameCount => Replay?.Frames.Count ?? 0;

    /// <summary>Loads the page for a history game: its replay file, the card names and the meta for the archetype calls.</summary>
    public static MatchPage Load(GameRecord game, ReplayStore store, CardDb cards, ConstructedMeta? meta)
    {
        var text = store.Load(game.Replay);
        var file = text is null ? null : ReplayFile.Read(text);
        var header = BuildHeader(game, cards, file?.Header);
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
                Rounds = data.Rounds.Select(r => RoundView(r, cards)).ToList(),
                Lobby = data.Lobby.Select(h => new MatchLobbyRow(h.HeroCardId, cards.NameOf(h.HeroCardId), Ordinal(h.Place), h.Local)).ToList(),
            };
        }

        var replay = MatchReplay.Parse(text!);
        if (replay is null || replay.Frames.Count == 0)
        {
            return new MatchPage(game, cards, header) { Missing = Loc.T("Match.Missing.Unreadable") };
        }

        var shown = replay.OpponentPlayed.Select(c => c.CardId).ToList();
        var archetype = game.OpponentArchetype
                        ?? (ArchetypeGuess.FitsFormat(meta, game.Format) ? ArchetypeGuess.FromSeenCards(meta, game.OpponentClass, shown)?.Name : null);
        var (deck, deckNote) = LocalDeckRows(game, replay, cards);
        return new MatchPage(game, cards, header)
        {
            Replay = replay,
            Turns = TurnChips(replay),
            LocalDeck = deck,
            LocalDeckNote = deckNote,
            OpponentCards = replay.OpponentPlayed
                .Select(c => new MatchDeckRow(c.CardId, cards.NameOf(c.CardId), cards.Get(c.CardId)?.Cost ?? 0, c.Count > 1 ? $"×{c.Count}" : string.Empty,
                    Loc.F("Match.Deck.Turn", c.Turn), true))
                .ToList(),
            OpponentArchetype = archetype,
            Analysis = MatchAnalysis.Build(game, replay, cards, meta, archetype),
        };
    }

    /// <summary>The step at <paramref name="frame"/> with <paramref name="bottom"/>'s side at the bottom.</summary>
    public MatchStepView? Step(int frame, ReplayPlayer bottom)
    {
        if (Replay is not { Frames.Count: > 0 } replay)
        {
            return null;
        }

        var f = replay.Frames[Math.Clamp(frame, 0, replay.Frames.Count - 1)];
        var local = Side(f.Local, true, f.PlayerTurn);
        var opponent = Side(f.Opponent, false, f.PlayerTurn);
        var turn = f.Mulligan ? Loc.T("Match.Step.Mulligan") : f.Turn == 0 ? Loc.T("Match.Step.Start") : Loc.F("Match.Step.Turn", f.PlayerTurn, Whose(f.Active));
        return new MatchStepView(f.Index, replay.Frames.Count, turn, Describe(f.Action), Details(f.Action), f.Action.Player == ReplayPlayer.Local,
            bottom == ReplayPlayer.Local ? opponent : local,
            bottom == ReplayPlayer.Local ? local : opponent);
    }

    /// <summary>The actions of the turn holding <paramref name="frame"/>, the current one marked.</summary>
    public IReadOnlyList<MatchActionRow> Actions(int frame)
    {
        if (Replay is not { } replay || TurnOf(frame) is not { } chip)
        {
            return [];
        }

        return replay.Frames.Skip(chip.FirstFrame).Take(chip.LastFrame - chip.FirstFrame + 1)
            .Select(f => new MatchActionRow(f.Index, IconOf(f.Action.Kind), Describe(f.Action) + Details(f.Action) switch { "" => "", var d => " · " + d },
                f.Action.Player == ReplayPlayer.Local, f.Action.CardId, f.Index == frame))
            .ToList();
    }

    /// <summary>The turn chip a frame belongs to.</summary>
    public MatchTurnChip? TurnOf(int frame) =>
        Turns.FirstOrDefault(t => frame >= t.FirstFrame && frame <= t.LastFrame) ?? (Turns.Count > 0 && frame > Turns[^1].LastFrame ? Turns[^1] : Turns.FirstOrDefault());

    private static IReadOnlyList<MatchTurnChip> TurnChips(MatchReplay replay)
    {
        var chips = new List<MatchTurnChip>();
        foreach (var turn in replay.Turns)
        {
            var mulligan = turn.Turn == 0;
            var local = turn.Player == ReplayPlayer.Local;
            var label = mulligan ? Loc.T("Match.Chip.Mulligan") : turn.PlayerTurn.ToString(CultureInfo.InvariantCulture);
            var tooltip = mulligan ? Loc.T("Match.Step.Mulligan")
                : Loc.F("Match.Step.Turn", turn.PlayerTurn, Whose(turn.Player)) + (turn.Seconds is { } s ? $" · {s / 60}:{s % 60:00}" : string.Empty);
            chips.Add(new MatchTurnChip(turn.Turn, label, tooltip, local, mulligan, turn.FirstFrame, turn.LastFrame));
        }

        // Frames before the first turn (game start) belong to the first chip; the last chip runs to the end.
        if (chips.Count > 0)
        {
            chips[0] = chips[0] with { FirstFrame = 0 };
            chips[^1] = chips[^1] with { LastFrame = replay.Frames.Count - 1 };
        }

        return chips;
    }

    private MatchSideView Side(ReplayBoard board, bool local, int turn)
    {
        var hero = board.Hero?.CardId ?? (local ? Game.HeroCardId : Game.OpponentHeroCardId);
        return new MatchSideView(
            local ? Loc.T("Match.You") : Loc.T("Match.Opponent"),
            local,
            hero,
            _cards.NameOf(hero),
            board.Health,
            board.Armor,
            board.Weapon is { } weapon ? Card(weapon, turn) : null,
            board.HeroPower is { } power ? Card(power, turn) : null,
            board.Minions.Select(m => Card(m, turn)).ToList(),
            board.Hand.Select(c => Card(c, turn)).ToList(),
            board.Secrets.Select(c => Card(c, turn)).ToList(),
            board.Mana > 0 ? $"{Math.Max(0, board.Mana - board.ManaUsed)}/{board.Mana}" : "0/0",
            Loc.F("Match.Side.Deck", board.DeckCount, board.Hand.Count));
    }

    private MatchCardView Card(ReplayCard card, int turn)
    {
        if (card.IsCardBack)
        {
            var origin = card.Origin switch
            {
                "start" => Loc.T("Match.Origin.Start"),
                "drawn" => Loc.F("Match.Origin.Drawn", card.EnteredTurn),
                "created" => Loc.F("Match.Origin.Created", card.EnteredTurn),
                "returned" => Loc.F("Match.Origin.Returned", card.EnteredTurn),
                "stolen" => Loc.F("Match.Origin.Stolen", card.EnteredTurn),
                "unknown" => Loc.T("Match.Origin.Unknown"),
                "coin" => Loc.T("Match.Origin.Coin"),
                _ => card.Zone == "SECRET" ? Loc.T("Match.Origin.Secret") : string.Empty,
            };
            return new MatchCardView(null, string.Empty, true)
            {
                Note = card.Origin switch
                {
                    "start" => Loc.T("Match.Chip.Mulligan"),
                    "coin" => Loc.T("Match.Note.Coin"),
                    null or "unknown" => string.Empty,
                    _ => card.EnteredTurn.ToString(CultureInfo.InvariantCulture),
                },
                Tooltip = origin,
            };
        }

        var name = _cards.NameOf(card.CardId);
        var stats = card.Type is "MINION" or "WEAPON" or "HERO" or "LOCATION";
        var keywords = card.Keywords.Select(Keyword).Where(k => k.Length > 0).ToList();
        var note = card.Zone == "HAND" && card.EnteredTurn > 0 ? Loc.F("Match.Note.Turn", card.EnteredTurn) : keywords.FirstOrDefault() ?? string.Empty;
        return new MatchCardView(card.CardId, name, false)
        {
            Attack = card.Attack,
            Health = card.Health,
            Cost = card.Cost > 0 ? card.Cost : _cards.Get(card.CardId)?.Cost ?? 0,
            ShowStats = stats && card.Zone == "PLAY" || card.Type == "MINION",
            Damaged = card.Damaged,
            Golden = card.Golden,
            Taunt = card.Keywords.Contains("TAUNT"),
            DivineShield = card.Keywords.Contains("DIVINE_SHIELD"),
            Note = note,
            Tooltip = keywords.Count > 0 ? $"{name} · {string.Join(", ", keywords)}" : name,
        };
    }

    private static string Keyword(string tag) => tag switch
    {
        "TAUNT" or "DIVINE_SHIELD" or "STEALTH" or "FROZEN" or "SILENCED" or "POISONOUS" or "WINDFURY" or "REBORN" or "LIFESTEAL" or "DORMANT"
            or "IMMUNE" => Loc.T("Match.Keyword." + tag),
        _ => string.Empty,
    };

    /// <summary>One line for an action: "You played Fireball → Ragnaros".</summary>
    public string Describe(ReplayAction action)
    {
        var you = action.Player == ReplayPlayer.Local;
        // "Fireball → Ragnaros" when the card had a face-up target.
        string Aimed(string? card) => action.TargetId is { } t ? Loc.F("Match.Action.Target", _cards.NameOf(card), _cards.NameOf(t)) : _cards.NameOf(card);
        return action.Kind switch
        {
            ReplayActionKind.Start => Loc.T("Match.Step.Start"),
            ReplayActionKind.Mulligan => Loc.T("Match.Step.Mulligan"),
            ReplayActionKind.TurnStart => Loc.T(you ? "Match.Action.YourTurn" : "Match.Action.TheirTurn"),
            ReplayActionKind.Play => action.CardId is { } id
                ? Loc.F(you ? "Match.Action.YouPlayed" : "Match.Action.TheyPlayed", Aimed(id))
                : Loc.T(you ? "Match.Action.YouPlayedCard" : "Match.Action.TheyPlayedCard"),
            ReplayActionKind.HeroPower or ReplayActionKind.Use => Loc.F(you ? "Match.Action.YouUsed" : "Match.Action.TheyUsed", Aimed(action.CardId)),
            ReplayActionKind.Attack => action.TargetId is { } d
                ? Loc.F("Match.Action.Attacked", _cards.NameOf(action.CardId), _cards.NameOf(d))
                : Loc.F("Match.Action.AttackedNoTarget", _cards.NameOf(action.CardId)),
            ReplayActionKind.Choice => action.Picked.Count == 0 ? Loc.T("Match.Action.Offered")
                : action.CardId is { } source
                    ? Loc.F("Match.Action.PickedFrom", string.Join(", ", action.Picked.Select(_cards.NameOf)), _cards.NameOf(source))
                    : Loc.F("Match.Action.Picked", string.Join(", ", action.Picked.Select(_cards.NameOf))),
            ReplayActionKind.End => action.Result switch
            {
                "WON" => Loc.T("Match.Action.Won"),
                "LOST" => Loc.T("Match.Action.Lost"),
                "TIED" => Loc.T("Match.Action.Tied"),
                "CONCEDED" => Loc.T("Match.Action.Conceded"),
                _ => Loc.T("Match.Action.Over"),
            },
            _ => string.Empty,
        };
    }

    /// <summary>What else the action did: deaths, the other Discover options.</summary>
    public string Details(ReplayAction action)
    {
        var parts = new List<string>();
        if (action.Kind == ReplayActionKind.Choice && action.Options.Count > 0)
        {
            var others = action.Options.ToList();
            foreach (var picked in action.Picked)
            {
                others.Remove(picked);
            }

            if (others.Count > 0)
            {
                parts.Add(Loc.F("Match.Detail.Over", string.Join(", ", others.Select(_cards.NameOf))));
            }
        }

        if (action.Deaths.Count > 0)
        {
            parts.Add(Loc.F("Match.Detail.Died", string.Join(", ", action.Deaths.Select(d => _cards.NameOf(d.CardId)))));
        }

        return string.Join(" · ", parts);
    }

    private static string IconOf(ReplayActionKind kind) => kind switch
    {
        ReplayActionKind.Play => IconPlay,
        ReplayActionKind.HeroPower or ReplayActionKind.Use => IconPower,
        ReplayActionKind.Attack => IconAttack,
        ReplayActionKind.Choice => IconChoice,
        ReplayActionKind.End => IconEnd,
        _ => IconTurn,
    };

    private static string Whose(ReplayPlayer? player) => player switch
    {
        ReplayPlayer.Local => Loc.T("Match.You"),
        ReplayPlayer.Opponent => Loc.T("Match.Opponent"),
        _ => string.Empty,
    };

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
                return new MatchDeckRow(c.Id, cards.NameOf(c.Id), card?.Cost ?? 0, c.Copies > 1 ? $"×{c.Copies}" : string.Empty, string.Join(" · ", marks), d > 0);
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

    private static MatchHeader BuildHeader(GameRecord game, CardDb cards, ReplayHeader? replay)
    {
        var bg = game.IsBattlegrounds;
        var local = new MatchPlayerView(
            StripTag(game.PlayerName),
            game.HeroCardId,
            cards.NameOf(game.HeroCardId),
            bg ? ModeText(game.Mode) : ConstructedMeta.ClassName(ClassOf(game, cards)),
            bg ? Rating(game) : RankInfo.Localize(replay?.LocalRank ?? game.Rank) ?? string.Empty);
        var opponent = new MatchPlayerView(
            bg ? Loc.T("Match.Lobby") : StripTag(game.OpponentName) is { Length: > 0 } name && !name.StartsWith("UNKNOWN", StringComparison.Ordinal) ? name : Loc.T("Match.Opponent"),
            game.OpponentHeroCardId,
            cards.NameOf(game.OpponentHeroCardId),
            ConstructedMeta.ClassName(game.OpponentClass),
            RankInfo.Localize(replay?.OpponentRank) ?? string.Empty);
        var top = game.Mode == "Duos" ? 2 : 4;
        var (result, key) = game.IsAbandoned ? (Loc.T("Main.Result.Abandoned"), game.Result)
            : bg ? (Loc.F("Match.Header.Place", Ordinal(game.Placement)), game.Placement is > 0 && game.Placement <= top ? "Win" : "Loss")
            : (ResultText(game.Result), game.Result);
        var format = game.Format is { Length: > 0 } f ? Loc.Or("Match.Format." + f, string.Empty) : string.Empty;
        var title = bg ? Loc.F("Match.Header.Title", cards.NameOf(game.HeroCardId), ModeText(game.Mode))
            : Loc.F("Match.Header.Versus", local.Class.Length > 0 ? local.Class : game.DeckName ?? Loc.T("Match.You"),
                opponent.Class.Length > 0 ? opponent.Class : Loc.T("Match.Opponent"));
        var subtitle = string.Join(" · ", new[]
        {
            ModeText(game.Mode),
            bg ? null : format,
            game.StartedAt.ToString("d MMM yyyy, HH:mm", Loc.DateCulture),
        }.Where(x => !string.IsNullOrEmpty(x)));
        var duration = game.Duration > TimeSpan.Zero ? $"{(int)game.Duration.TotalMinutes}:{game.Duration.Seconds:00}" : "—";
        var facts = new List<MatchFact>
        {
            new(Loc.T("Match.Fact.Duration"), duration),
            new(Loc.T(bg ? "Match.Fact.Rounds" : "Match.Fact.Turns"), game.Turns.ToString(CultureInfo.CurrentCulture)),
        };
        if (!bg)
        {
            facts.Add(new MatchFact(Loc.T("Match.Fact.Coin"), game.WentFirst switch
            {
                true => Loc.T("Match.Fact.WentFirst"),
                false => Loc.T("Match.Fact.HadCoin"),
                _ => "—",
            }));
            if (game.DeckName is { Length: > 0 } deck)
            {
                facts.Add(new MatchFact(Loc.T("Match.Fact.Deck"), deck));
            }

            if (game.OpponentArchetype is { Length: > 0 } archetype)
            {
                facts.Add(new MatchFact(Loc.T("Match.Fact.OpponentDeck"), archetype));
            }
        }
        else
        {
            facts.Add(new MatchFact(Loc.T("Match.Fact.TavernTier"), game.TavernTier.ToString(CultureInfo.CurrentCulture)));
            if (game.RatingDelta is { } delta)
            {
                facts.Add(new MatchFact(Loc.T("Match.Fact.Rating"), delta >= 0 ? $"+{delta}" : $"−{-delta}"));
            }
        }

        return new MatchHeader(title, subtitle, result, key, local, opponent) { Facts = facts };
    }

    /// <summary>
    /// The player's class: the row's, else the hero's, else the deck's (a hero card played mid-game, like a neutral
    /// Deathwing, replaces the class hero the row names).
    /// </summary>
    private static string ClassOf(GameRecord game, CardDb cards)
    {
        static bool Known(string? c) => !string.IsNullOrEmpty(c) && c != "NEUTRAL";
        if (Known(game.PlayerClass))
        {
            return game.PlayerClass;
        }

        if (cards.Get(game.HeroCardId)?.CardClass is { } heroClass && Known(heroClass))
        {
            return heroClass;
        }

        var deck = game.DeckCode is { Length: > 0 } code ? DeckString.TryDecode(code) : null;
        return deck?.HeroDbfIds.Select(cards.Get).FirstOrDefault(c => Known(c?.CardClass))?.CardClass ?? string.Empty;
    }

    private static string Rating(GameRecord game) =>
        game.RatingBefore is { } before
            ? game.RatingAfter is { } after ? $"{before:N0} → {after:N0}" : $"{before:N0}"
            : string.Empty;

    private static string StripTag(string name) => name.Split('#')[0];

    private static string Ordinal(int n) => n switch
    {
        <= 0 => "—",
        1 => Loc.T("Main.Place.1"),
        2 => Loc.T("Main.Place.2"),
        3 => Loc.T("Main.Place.3"),
        _ => Loc.F("Main.Place.N", n),
    };

    /// <summary>A history result ("Win", "Loss", "Tie", "Unknown") as the player reads it.</summary>
    private static string ResultText(string result) => result switch
    {
        "Win" => Loc.T("Main.Result.Win"),
        "Loss" => Loc.T("Main.Result.Loss"),
        "Tie" => Loc.T("Main.Result.Tie"),
        "Unknown" => Loc.T("Main.Result.Unknown"),
        _ => result,
    };

    /// <summary>A history mode ("Ranked", "Battlegrounds"...) as the player reads it.</summary>
    private static string ModeText(string mode) => Loc.Or("Main.Mode." + mode, mode);
}
