using System.Globalization;
using Hearthpulse.Core.Localization;

namespace Hearthpulse.Core.Replay;

/// <summary>
/// The Battlegrounds step-by-step replay of the match page (format 4 files): each step as shown, the marks of the player
/// bar and the spans of the timeline. Only what the file holds, which is what the player saw: Bob's offer in the tavern,
/// the combat opponent in combat, heroes by card.
/// </summary>
public sealed partial class MatchPage
{
    private IReadOnlyList<MatchStepMark>? _battlegroundsMarks;
    private IReadOnlyList<MatchTimelineSpan>? _battlegroundsSpans;

    /// <summary>True when the game's file holds the Battlegrounds step-by-step replay (format 4).</summary>
    public bool HasBattlegroundsSteps => BgReplay is not null;

    /// <summary>The steps of a format 4 Battlegrounds file; null for other files.</summary>
    private BattlegroundsMatchReplay? BgReplay { get; init; }

    /// <summary>The Battlegrounds step at <paramref name="frame"/>, or null without one.</summary>
    public MatchBgStepView? BattlegroundsStep(int frame)
    {
        if (BgReplay is not { Frames.Count: > 0 } replay)
        {
            return null;
        }

        var f = replay.Frames[Math.Clamp(frame, 0, replay.Frames.Count - 1)];
        var combat = f.Phase == MatchPhase.Combat;
        var opponent = Of(f, 'O', "PLAY", "HERO").LastOrDefault();
        var board = Of(f, 'L', "PLAY", "MINION").Select(c => CardView(f, c)).ToList();
        if (f is { Kind: MatchStepKind.Sell, Actor: { } sold } && f.Cards.All(c => c.Id != sold.Id))
        {
            // The minion sold is shown where it stood, marked as the step's card.
            board.Insert(Math.Clamp(sold.Position - 1, 0, board.Count), CardView(f, sold));
        }

        var shop = Of(f, 'B').Select(c => CardView(f, c)).ToList();
        return new MatchBgStepView(f.Index, replay.Frames.Count, f.Turn, f.Phase, f.Kind, BgTitle(f), BgAction(f), BgDetail(f))
        {
            Hero = Of(f, 'L', "PLAY", "HERO").LastOrDefault() is { } hero ? HeroView(f, hero, f.Tier) : null,
            Opponent = combat && opponent is not null ? HeroView(f, opponent, f.Leaderboard.FirstOrDefault(l => l.HeroCardId == opponent.CardId)?.Tier ?? 0) : null,
            Board = board,
            OpponentBoard = combat ? Of(f, 'O', "PLAY", "MINION").Select(c => CardView(f, c)).ToList() : [],
            Shop = shop,
            Hand = Of(f, 'L', "HAND").Select(c => CardView(f, c)).ToList(),
            Choices = f.Choice is { } choice ? [ChoiceView(choice)] : [],
            Leaderboard = f.Leaderboard
                .OrderBy(l => l.Place <= 0 ? 99 : l.Place)
                .Select(l => new MatchBgLeaderRow(l.HeroCardId, _cards.NameOf(l.HeroCardId), l.Place, l.Health, l.Armor, l.Tier, l.Local,
                    combat && !l.Local && l.HeroCardId == opponent?.CardId, l.Dead))
                .ToList(),
            Gold = !combat && f.MaxGold > 0 ? string.Create(CultureInfo.CurrentCulture, $"{f.Gold} / {f.MaxGold}") : string.Empty,
            ShopFrozen = shop.Any(c => c.Frozen),
            ResultKey = f.Kind == MatchStepKind.CombatEnd ? ResultKeyOf(f.Result) : null,
        };
    }

    private IReadOnlyList<MatchStepMark> BattlegroundsMarks() =>
        _battlegroundsMarks ??= BgReplay is not { } replay
            ? []
            : replay.Frames.Select(f => new MatchStepMark(f.Index, f.Turn, f.Phase, f.Kind, BgLocal(f), BgEvent(f.Kind), BgAction(f))).ToList();

    private IReadOnlyList<MatchTimelineSpan> BattlegroundsSpans() => _battlegroundsSpans ??= BuildBattlegroundsSpans();

    /// <summary>The timeline: the hero choice, each tavern (by turn), each combat (its result in the tooltip), the end.</summary>
    private List<MatchTimelineSpan> BuildBattlegroundsSpans()
    {
        var spans = new List<MatchTimelineSpan>();
        if (BgReplay is not { } replay)
        {
            return spans;
        }

        var frames = replay.Frames;
        var first = 0;
        for (var i = 1; i <= frames.Count; i++)
        {
            if (i < frames.Count && frames[i].Phase == frames[first].Phase && frames[i].Turn == frames[first].Turn)
            {
                continue;
            }

            var start = frames[first];
            var last = frames[i - 1];
            var hero = frames.Skip(first).Take(i - first).SelectMany(f => Of(f, 'O', "PLAY", "HERO")).Select(c => _cards.NameOf(c.CardId)).FirstOrDefault()
                       ?? Loc.T("Match.Opponent");
            var tooltip = start.Phase switch
            {
                MatchPhase.Shop => Loc.F("Match.Bg.Span.Shop", start.Turn, last.Tier),
                MatchPhase.Combat => last.Kind != MatchStepKind.CombatEnd ? Loc.F("Match.Bg.Span.Combat", start.Turn, hero)
                    : last.Result switch
                    {
                        "WIN" => Loc.F("Match.Bg.Span.CombatWon", start.Turn, hero, last.Dealt),
                        "LOSS" => Loc.F("Match.Bg.Span.CombatLost", start.Turn, hero, last.Taken),
                        _ => Loc.F("Match.Bg.Span.CombatTie", start.Turn, hero),
                    },
                MatchPhase.End => Loc.F("Match.Bg.Action.End", Ordinal(last.Place)),
                _ => Loc.T("Match.Bg.Title.Start"),
            };
            var label = start.Phase == MatchPhase.Shop ? start.Turn.ToString(CultureInfo.InvariantCulture) : string.Empty;
            spans.Add(new MatchTimelineSpan(start.Index, last.Index, start.Turn, start.Phase, true, label, tooltip));
            first = i;
        }

        return spans;
    }

    /// <summary>Left/right stop at tavern actions, the start and end of each combat, attacks and the end of the game.</summary>
    private static bool BgEvent(MatchStepKind kind) => kind is not (MatchStepKind.Start or MatchStepKind.TurnStart or MatchStepKind.Deaths or MatchStepKind.Trigger);

    /// <summary>The step is the player's: everything in the tavern, our attacks and triggers, deaths of our minions.</summary>
    private static bool BgLocal(BgReplayFrame f) => f.Kind switch
    {
        MatchStepKind.Attack or MatchStepKind.Trigger => f.Actor?.Side != 'O',
        MatchStepKind.Deaths => f.Cards.FirstOrDefault(c => c.Dying)?.Side != 'O',
        _ => true,
    };

    private static string BgTitle(BgReplayFrame f) => f.Phase switch
    {
        MatchPhase.Shop => Loc.F("Match.Bg.Title.Shop", f.Turn),
        MatchPhase.Combat => Loc.F("Match.Bg.Title.Combat", f.Turn),
        MatchPhase.End => Loc.T("Match.Bg.Title.End"),
        _ => Loc.T("Match.Bg.Title.Start"),
    };

    /// <summary>One line for a step: "You bought Alleycat", "Turn 5 · combat against Reno".</summary>
    private string BgAction(BgReplayFrame f)
    {
        var actor = f.Actor is { } a ? _cards.NameOf(a.CardId) : string.Empty;
        var aimed = f.Target is { } t ? Loc.F("Match.Action.Target", actor, _cards.NameOf(t.CardId)) : actor;
        return f.Kind switch
        {
            MatchStepKind.Choice => f.Choice is { Picked.Count: > 0 } choice
                ? Loc.F("Match.Action.Picked", string.Join(", ", choice.Picked.Select(_cards.NameOf)))
                : Loc.T("Match.Action.Offered"),
            MatchStepKind.TurnStart => Loc.T("Match.Bg.Action.TavernOpens"),
            MatchStepKind.Buy => Loc.F("Match.Bg.Action.Buy", actor),
            MatchStepKind.Sell => Loc.F("Match.Bg.Action.Sell", actor),
            MatchStepKind.Play => Loc.F("Match.Action.YouPlayed", aimed),
            MatchStepKind.HeroPower => Loc.F("Match.Action.YouUsed", aimed),
            MatchStepKind.Refresh => Loc.T("Match.Bg.Action.Refresh"),
            MatchStepKind.Upgrade => Loc.F("Match.Bg.Action.Upgrade", f.Tier),
            MatchStepKind.Freeze => Loc.T(Of(f, 'B').Any(c => c.Frozen) ? "Match.Bg.Action.Freeze" : "Match.Bg.Action.Unfreeze"),
            MatchStepKind.Move => Loc.F("Match.Bg.Action.Move", actor),
            MatchStepKind.Trigger => Loc.F("Match.Bg.Action.Trigger", actor),
            MatchStepKind.CombatStart => Loc.F("Match.Bg.Action.CombatStart",
                Of(f, 'O', "PLAY", "HERO").LastOrDefault() is { } hero ? _cards.NameOf(hero.CardId) : Loc.T("Match.Opponent")),
            MatchStepKind.Attack => f.Target is { } d
                ? Loc.F("Match.Action.Attacked", actor, _cards.NameOf(d.CardId))
                : Loc.F("Match.Action.AttackedNoTarget", actor),
            MatchStepKind.Deaths => Loc.F("Match.Bg.Action.Deaths", string.Join(", ", f.Cards.Where(c => c.Dying).Select(c => _cards.NameOf(c.CardId)))),
            MatchStepKind.CombatEnd => f.Result switch
            {
                "WIN" => Loc.T("Match.Action.Won"),
                "LOSS" => Loc.T("Match.Action.Lost"),
                _ => Loc.T("Match.Action.Tied"),
            },
            MatchStepKind.End => Loc.F("Match.Bg.Action.End", Ordinal(f.Place)),
            _ => string.Empty,
        };
    }

    /// <summary>What else the step tells: the combat's damage, a move's slots, the options not picked.</summary>
    private string BgDetail(BgReplayFrame f) => f.Kind switch
    {
        MatchStepKind.CombatEnd when f is { Result: "WIN", Dealt: > 0 } => Loc.F("Match.Bg.Detail.Dealt", f.Dealt),
        MatchStepKind.CombatEnd when f is { Result: "LOSS", Taken: > 0 } => Loc.F("Match.Bg.Detail.Taken", f.Taken),
        MatchStepKind.Move when f is { MovedFrom: > 0, Actor: { } moved } && moved.Position != f.MovedFrom =>
            Loc.F("Match.Bg.Detail.Position", f.MovedFrom, moved.Position),
        MatchStepKind.Choice when f.Choice is { Picked.Count: > 0 } choice && Others(choice) is { Count: > 0 } others =>
            Loc.F("Match.Detail.Over", string.Join(", ", others.Select(_cards.NameOf))),
        _ => string.Empty,
    };

    private static List<string> Others(BgReplayChoice choice)
    {
        var others = choice.Options.ToList();
        foreach (var picked in choice.Picked)
        {
            others.Remove(picked);
        }

        return others;
    }

    private MatchChoiceView ChoiceView(BgReplayChoice choice)
    {
        var left = choice.Picked.ToList();
        var title = Loc.T(choice.Kind switch
        {
            "HERO" => "Match.Bg.Choice.Hero",
            "TRINKET" => "Match.Bg.Choice.Trinket",
            "QUEST" => "Match.Bg.Choice.Quest",
            _ => "Match.Bg.Choice.Discover",
        });
        return new MatchChoiceView(title, choice.Options.Select(id =>
        {
            var picked = left.Remove(id);
            return Offered(id, picked, !picked && choice.Picked.Count > 0, string.Empty);
        }).ToList());
    }

    private MatchBgHeroView HeroView(BgReplayFrame f, BgReplayCard hero, int tier) =>
        new(hero.CardId, _cards.NameOf(hero.CardId), hero.Health, hero.Armor, tier)
        {
            HeroPowers = Of(f, hero.Side, "PLAY", "HERO_POWER").Select(c => CardView(f, c)).ToList(),
            Trinkets = Of(f, hero.Side, "PLAY", "BATTLEGROUND_TRINKET", "BATTLEGROUND_QUEST_REWARD").Concat(Of(f, hero.Side, "SECRET"))
                .Select(c => CardView(f, c)).ToList(),
        };

    private MatchBgCardView CardView(BgReplayFrame f, BgReplayCard c)
    {
        var name = _cards.NameOf(c.CardId);
        var actor = f.Actor?.Id == c.Id;
        var attacking = f.Kind == MatchStepKind.Attack && actor;
        var defending = f.Kind == MatchStepKind.Attack && f.Target?.Id == c.Id;
        var acting = actor && f.Kind is MatchStepKind.Buy or MatchStepKind.Sell or MatchStepKind.Play or MatchStepKind.HeroPower
            or MatchStepKind.Move or MatchStepKind.Trigger;
        var minion = c.Type == "MINION";
        var tier = c.Side == 'B' ? c.Tier : 0;
        var parts = new List<string> { minion ? Loc.F("Match.Bg.Card.Stats", name, c.Attack, c.Health) : name };
        if (c.Golden) parts.Add(Loc.T("Match.Bg.Card.Golden"));
        if (c.Taunt) parts.Add(Loc.T("Match.Keyword.TAUNT"));
        if (c.DivineShield) parts.Add(Loc.T("Match.Keyword.DIVINE_SHIELD"));
        if (c.Reborn) parts.Add(Loc.T("Match.Keyword.REBORN"));
        if (c.Venomous) parts.Add(Loc.T("Match.Bg.Keyword.Venomous"));
        if (c.Windfury) parts.Add(Loc.T("Match.Keyword.WINDFURY"));
        if (c.Stealth) parts.Add(Loc.T("Match.Keyword.STEALTH"));
        if (tier > 0) parts.Add(Loc.F("Match.Bg.Card.Tier", tier));
        if (c.Frozen) parts.Add(Loc.T("Match.Keyword.FROZEN"));
        if (attacking) parts.Add(Loc.T("Match.Bg.Card.Attacking"));
        if (defending) parts.Add(Loc.T("Match.Bg.Card.Defending"));
        if (c.Dying) parts.Add(Loc.T("Match.Bg.Card.Dying"));
        return new MatchBgCardView(c.CardId, name)
        {
            Attack = c.Attack,
            Health = c.Health,
            ShowStats = minion,
            Golden = c.Golden,
            Damaged = c.Damaged,
            Taunt = c.Taunt,
            DivineShield = c.DivineShield,
            Reborn = c.Reborn,
            Venomous = c.Venomous,
            Windfury = c.Windfury,
            Stealth = c.Stealth,
            Frozen = c.Frozen,
            Tier = tier,
            Attacking = attacking,
            Defending = defending,
            Dying = c.Dying,
            Acting = acting,
            Tooltip = string.Join(" · ", parts),
        };
    }

    /// <summary>The step's cards of one side and zone (and card types), left to right.</summary>
    private static IEnumerable<BgReplayCard> Of(BgReplayFrame f, char side, string? zone = null, params string[] types) =>
        f.Cards.Where(c => c.Side == side && (zone is null || c.Zone == zone) && (types.Length == 0 || types.Contains(c.Type)))
            .OrderBy(c => c.Position).ThenBy(c => c.Id);

    private static string? ResultKeyOf(string? result) => result switch
    {
        "WIN" => "Win",
        "LOSS" => "Loss",
        "TIE" => "Tie",
        _ => null,
    };
}
