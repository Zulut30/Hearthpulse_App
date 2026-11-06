using System.Globalization;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Memory;
using Hearthpulse.Core.Stats;

namespace Hearthpulse.Core.Replay;

public sealed partial class MatchPage
{
    /// <summary>The step at <paramref name="frame"/> with <paramref name="bottom"/>'s side at the bottom.</summary>
    public MatchStepView? Step(int frame, ReplayPlayer bottom)
    {
        if (Replay is not { Frames.Count: > 0 } replay)
        {
            return null;
        }

        var f = replay.Frames[Math.Clamp(frame, 0, replay.Frames.Count - 1)];
        var before = f.Index > 0 ? replay.Frames[f.Index - 1] : null;
        var marks = new StepMarks(ActorOf(f), f.Action.TargetRef ?? 0, HealthBefore(before));
        var died = f.Action.Deaths.Select(d => d.Ref).ToHashSet();
        var local = Side(f.Local, true, f.PlayerTurn, marks, before?.Local, died);
        var opponent = Side(f.Opponent, false, f.PlayerTurn, marks, before?.Opponent, died);
        var turn = f.Mulligan ? Loc.T("Match.Step.Mulligan") : f.Turn == 0 ? Loc.T("Match.Step.Start") : Loc.F("Match.Step.Turn", f.PlayerTurn, Whose(f.Active));
        return new MatchStepView(f.Index, replay.Frames.Count, turn, Describe(f.Action), Details(f.Action), f.Action.Player == ReplayPlayer.Local,
            bottom == ReplayPlayer.Local ? opponent : local,
            bottom == ReplayPlayer.Local ? local : opponent)
        {
            Choices = ChoicesOf(replay, f),
            ActorRef = marks.Actor,
            TargetRef = marks.Target,
            IsAttack = f.Action.Kind == ReplayActionKind.Attack,
        };
    }

    /// <summary>
    /// The game's event feed up to its end (plan R2): each turn's header and its actions, the step on screen marked.
    /// The same texts as the turn's actions.
    /// </summary>
    public IReadOnlyList<MatchFeedRow> Feed(int frame)
    {
        if (Replay is not { } replay)
        {
            return [];
        }

        var keys = Analysis?.KeyCards ?? [];
        var rows = new List<MatchFeedRow>();
        foreach (var chip in Turns)
        {
            var inTurn = frame >= chip.FirstFrame && frame <= chip.LastFrame;
            rows.Add(new MatchFeedRow(chip.FirstFrame, string.Empty, chip.Tooltip.Split('\n')[0], chip.Local, null, inTurn, Header: true) { Mulligan = chip.Mulligan });
            foreach (var f in replay.Frames.Skip(chip.FirstFrame).Take(chip.LastFrame - chip.FirstFrame + 1))
            {
                var details = Details(f.Action);
                var local = f.Action.Player == ReplayPlayer.Local;
                var key = keys.FirstOrDefault(k => k.Frame == f.Index && k.CardId == f.Action.CardId && k.Local == local);
                rows.Add(new MatchFeedRow(f.Index, IconOf(f.Action.Kind), Describe(f.Action) + (details.Length > 0 ? " · " + details : string.Empty),
                    local, f.Action.CardId, f.Index == frame, Header: false) { Key = key is not null, KeyReason = key?.Reason });
            }
        }

        return rows;
    }

    /// <summary>
    /// The step's actor on the table: the attacker or the card played; a spell (gone from the table) starts its arrow at
    /// its side's hero. 0 without an actor.
    /// </summary>
    private static int ActorOf(ReplayFrame frame)
    {
        if (frame.Action.ActorRef is not { } actor)
        {
            return 0;
        }

        var boards = new[] { frame.Local, frame.Opponent };
        if (boards.Any(b => b.Hero?.Id == actor || b.Weapon?.Id == actor || b.HeroPower?.Id == actor || b.Minions.Any(m => m.Id == actor) || b.Secrets.Any(s => s.Id == actor)))
        {
            return actor;
        }

        return frame.Action.Player is { } player ? frame.Of(player).Hero?.Id ?? 0 : 0;
    }

    /// <summary>What the table marks on a step: the actor, its target, and each card's health just before.</summary>
    private sealed record StepMarks(int Actor, int Target, IReadOnlyDictionary<int, int> HealthBefore);

    /// <summary>Health (and a hero's armor) of each card on the table at a step, by replay id.</summary>
    private static Dictionary<int, int> HealthBefore(ReplayFrame? frame)
    {
        var health = new Dictionary<int, int>();
        if (frame is null)
        {
            return health;
        }

        foreach (var board in new[] { frame.Local, frame.Opponent })
        {
            if (board.Hero is { } hero)
            {
                health[hero.Id] = hero.Health + hero.Armor;
            }

            foreach (var minion in board.Minions)
            {
                health[minion.Id] = minion.Health;
            }
        }

        return health;
    }

    /// <summary>A table card with the step's marks: actor, target, health change.</summary>
    private static MatchCardView Marked(MatchCardView view, ReplayCard card, StepMarks marks, int health) => view with
    {
        Ref = card.Id,
        Acting = card.Id == marks.Actor && marks.Actor != 0,
        Targeted = card.Id == marks.Target && marks.Target != 0,
        Change = marks.HealthBefore.TryGetValue(card.Id, out var before) ? health - before : 0,
    };

    /// <summary>The choice rows of a step: a Discover's options, or both mulligans at the mulligan step.</summary>
    private IReadOnlyList<MatchChoiceView> ChoicesOf(MatchReplay replay, ReplayFrame frame)
    {
        if (frame.Action is { Kind: ReplayActionKind.Choice, Options.Count: > 0 } choice)
        {
            var left = choice.Picked.ToList();
            var cards = choice.Options.Select(id =>
            {
                var picked = left.Remove(id);
                return Offered(id, picked, !picked && choice.Picked.Count > 0, string.Empty);
            }).ToList();
            var title = choice.CardId is { } source ? Loc.F("Match.Choice.From", _cards.NameOf(source)) : Loc.T("Match.Choice.Title");
            return [new MatchChoiceView(title, cards)];
        }

        if (frame.Action.Kind != ReplayActionKind.Mulligan)
        {
            return [];
        }

        var rows = new List<MatchChoiceView>();
        if (replay.Mulligan is { } mine)
        {
            var replaced = mine.Replaced.ToList();
            rows.Add(new MatchChoiceView(Loc.T("Match.Choice.YourMulligan"), mine.Offered.Select(id =>
            {
                var back = replaced.Remove(id);
                return Offered(id, !back, back, Loc.T(back ? "Match.Choice.Replaced" : "Match.Choice.Kept"));
            }).ToList()));
        }

        if (replay.OpponentMulligan is { } theirs)
        {
            rows.Add(new MatchChoiceView(Loc.T("Match.Choice.TheirMulligan"),
                theirs.Kept.Select(id => TheirMulliganCard(id, kept: true)).Concat(theirs.Replaced.Select(id => TheirMulliganCard(id, kept: false))).ToList()));
        }

        return rows;
    }

    private MatchCardView Offered(string cardId, bool picked, bool dimmed, string note) => new(cardId, _cards.NameOf(cardId), false)
    {
        Cost = _cards.Get(cardId)?.Cost ?? 0,
        Picked = picked,
        Dimmed = dimmed,
        Note = note,
        Tooltip = _cards.NameOf(cardId),
    };

    /// <summary>A card of the opponent's mulligan: face up when the log revealed it later, else a card back.</summary>
    private MatchCardView TheirMulliganCard(string? cardId, bool kept)
    {
        var note = Loc.T(kept ? "Match.Choice.Kept" : "Match.Choice.Replaced");
        return cardId is null
            ? new MatchCardView(null, string.Empty, true) { Dimmed = !kept, Note = note, Tooltip = Loc.T("Match.Reveal.Never") }
            : Offered(cardId, kept, !kept, note) with { Revealed = true, Tooltip = $"{_cards.NameOf(cardId)} · {Loc.T("Match.Reveal.Later")}" };
    }

    /// <summary>The actions of the turn holding <paramref name="frame"/>, the current one marked.</summary>
    public IReadOnlyList<MatchActionRow> Actions(int frame)
    {
        if (Replay is not { } replay || TurnOf(frame) is not { } chip)
        {
            return [];
        }

        var keys = Analysis?.KeyCards ?? [];
        return replay.Frames.Skip(chip.FirstFrame).Take(chip.LastFrame - chip.FirstFrame + 1)
            .Select(f =>
            {
                var local = f.Action.Player == ReplayPlayer.Local;
                var key = keys.FirstOrDefault(k => k.Frame == f.Index && k.CardId == f.Action.CardId && k.Local == local);
                return new MatchActionRow(f.Index, IconOf(f.Action.Kind), Describe(f.Action) + Details(f.Action) switch { "" => "", var d => " · " + d },
                    local, f.Action.CardId, f.Index == frame) { Key = key is not null, KeyReason = key?.Reason };
            })
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

    private MatchSideView Side(ReplayBoard board, bool local, int turn, StepMarks marks, ReplayBoard? before, IReadOnlySet<int> died)
    {
        var hero = board.Hero?.CardId ?? (local ? Game.HeroCardId : Game.OpponentHeroCardId);
        var heroRef = board.Hero?.Id ?? 0;
        return new MatchSideView(
            local ? Loc.T("Match.You") : Loc.T("Match.Opponent"),
            local,
            hero,
            _cards.NameOf(hero),
            board.Health,
            board.Armor,
            board.Weapon is { } weapon ? Marked(Card(weapon, turn), weapon, marks, weapon.Health) with { Change = 0 } : null,
            board.HeroPower is { } power ? Marked(Card(power, turn), power, marks, 0) with { Change = 0 } : null,
            WithDying(board.Minions.Select(m => Marked(Card(m, turn), m, marks, m.Health)).ToList(), board, before, marks, turn, died),
            board.Hand.Select(c => Card(c, turn)).ToList(),
            board.Secrets.Select(c => Marked(Card(c, turn), c, marks, 0) with { Change = 0 }).ToList(),
            board.Mana > 0 ? $"{Math.Max(0, board.Mana - board.ManaUsed)}/{board.Mana}" : "0/0",
            Loc.F("Match.Side.Deck", board.DeckCount, board.Hand.Count))
        {
            HeroRef = heroRef,
            HeroActing = heroRef != 0 && heroRef == marks.Actor,
            HeroTargeted = heroRef != 0 && heroRef == marks.Target,
            HealthChange = heroRef != 0 && marks.HealthBefore.TryGetValue(heroRef, out var heroBefore) ? board.Health + board.Armor - heroBefore : 0,
            HandCount = board.Hand.Count,
            SecretCount = board.Secrets.Count,
            DeckCount = board.DeckCount,
        };
    }

    /// <summary>
    /// The minions that died on this step stay where they stood, as dying (an exchange keeps its arrow): the step's
    /// deaths that stood on this side the step before. A minion that left otherwise (back to the hand, shuffled into the
    /// deck, transformed, taken by the other side) is no ghost. Their badge is the health they had.
    /// </summary>
    private List<MatchCardView> WithDying(List<MatchCardView> minions, ReplayBoard board, ReplayBoard? before, StepMarks marks, int turn, IReadOnlySet<int> died)
    {
        if (before is null)
        {
            return minions;
        }

        for (var i = 0; i < before.Minions.Count; i++)
        {
            var gone = before.Minions[i];
            if (!died.Contains(gone.Id) || board.Minions.Any(m => m.Id == gone.Id))
            {
                continue;
            }

            var ghost = Marked(Card(gone, turn), gone, marks, 0) with { Dying = true };
            minions.Insert(Math.Min(i, minions.Count), ghost);
        }

        return minions;
    }

    private MatchCardView Card(ReplayCard card, int turn)
    {
        if (card.IsCardBack && card.Reveal is { } reveal)
        {
            // Hidden at this step; the log revealed it later in the game (shown only on the page of a finished game).
            var id = Original(reveal, _cards);
            var revealedName = _cards.NameOf(id);
            var revealedNote = Loc.F("Match.Reveal.OnTurn", reveal.Turn);
            return new MatchCardView(id, revealedName, false)
            {
                Revealed = true,
                Cost = _cards.Get(id)?.Cost ?? 0,
                Note = revealedNote,
                Tooltip = $"{revealedName} · {revealedNote}",
            };
        }

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
}
