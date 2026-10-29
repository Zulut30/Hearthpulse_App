using System.Globalization;

namespace Hearthpulse.Core.Replay;

/// <summary>A card on a Battlegrounds step: ours (<c>L</c>), Bob's offer (<c>B</c>) or the combat opponent's (<c>O</c>).</summary>
public sealed record BgReplayCard(int Id, char Side, string Zone, int Position, string CardId, string Type)
{
    public int Attack { get; init; }

    /// <summary>Health left (HEALTH minus DAMAGE).</summary>
    public int Health { get; init; }

    public bool Damaged { get; init; }

    public int Armor { get; init; }

    public bool Golden { get; init; }

    public bool Taunt { get; init; }

    public bool DivineShield { get; init; }

    public bool Reborn { get; init; }

    /// <summary>POISONOUS or VENOMOUS.</summary>
    public bool Venomous { get; init; }

    /// <summary>WINDFURY or MEGA_WINDFURY.</summary>
    public bool Windfury { get; init; }

    public bool Stealth { get; init; }

    public bool Frozen { get; init; }

    /// <summary>TECH_LEVEL: the tavern tier of the card.</summary>
    public int Tier { get; init; }

    /// <summary>Died in this step.</summary>
    public bool Dying { get; init; }
}

/// <summary>A leaderboard row at a step: the hero shown, its place, health, armor and tavern tier.</summary>
public sealed record BgReplayLeader(int Slot, string HeroCardId, int Place, int Health, int Armor, int Tier, bool Local)
{
    public bool Dead => Health <= 0;
}

/// <summary>Our choice at a step: HERO, DISCOVER, TRINKET or QUEST; the options and what was picked.</summary>
public sealed record BgReplayChoice(string Kind, string? SourceCardId, IReadOnlyList<string> Options, IReadOnlyList<string> Picked);

/// <summary>One Battlegrounds step: what happened and every card shown after it.</summary>
public sealed record BgReplayFrame(int Index, int Turn, MatchPhase Phase, MatchStepKind Kind, int Seconds)
{
    /// <summary>The card the step is about (bought, sold, played, moved, the attacker, a trigger's source), as it is after the step or as it was last shown.</summary>
    public BgReplayCard? Actor { get; init; }

    /// <summary>The attack's defender or the play's target.</summary>
    public BgReplayCard? Target { get; init; }

    /// <summary>Every card shown after the step; those that died in it are still there, <see cref="BgReplayCard.Dying"/>.</summary>
    public IReadOnlyList<BgReplayCard> Cards { get; init; } = [];

    public IReadOnlyList<BgReplayLeader> Leaderboard { get; init; } = [];

    public BgReplayChoice? Choice { get; init; }

    /// <summary>Gold left (RESOURCES + TEMP_RESOURCES - RESOURCES_USED) and the turn's gold (RESOURCES).</summary>
    public int Gold { get; init; }

    public int MaxGold { get; init; }

    /// <summary>Our tavern tier.</summary>
    public int Tier { get; init; }

    /// <summary>The end of a combat: WIN, LOSS or TIE; null otherwise.</summary>
    public string? Result { get; init; }

    public int Taken { get; init; }

    public int Dealt { get; init; }

    /// <summary>The final place (the last step of a game that ended).</summary>
    public int Place { get; init; }

    /// <summary>A move: the minion's slot before it.</summary>
    public int MovedFrom { get; init; }
}

/// <summary>
/// A Battlegrounds game's step-by-step replay (format 4), folded from its event lines into <see cref="BgReplayFrame"/>s:
/// the hero choice, each tavern action, each combat step, the end of each combat and of the game.
/// </summary>
public sealed class BattlegroundsMatchReplay
{
    private BattlegroundsMatchReplay(ReplayHeader header) => Header = header;

    public ReplayHeader Header { get; }

    public IReadOnlyList<BgReplayFrame> Frames { get; private set; } = [];

    /// <summary>Reads the steps of a Battlegrounds file; null for another kind, an older format or a file without steps.</summary>
    public static BattlegroundsMatchReplay? Parse(string text) =>
        ReplayFile.Read(text) is { } file ? From(file.Header, file.Body) : null;

    /// <summary>The steps of an already split file; null when it has none.</summary>
    public static BattlegroundsMatchReplay? From(ReplayHeader header, IReadOnlyList<string> body)
    {
        if (header.Kind != ReplayFile.Battlegrounds || header.Version < ReplayFile.Version || header.Events <= 0)
        {
            return null;
        }

        var replay = new BattlegroundsMatchReplay(header);
        new Folder(replay).Run(body.Take(header.Events));
        return replay.Frames.Count > 0 ? replay : null;
    }

    /// <summary>Folds the event lines into frames: a step line closes the step before with the table as it is then.</summary>
    private sealed class Folder(BattlegroundsMatchReplay replay)
    {
        private readonly Dictionary<int, CardState> _cards = new();
        private readonly Dictionary<int, BgReplayCard> _last = new();
        private readonly Dictionary<string, string> _player = new(StringComparer.Ordinal);
        private readonly SortedDictionary<int, BgReplayLeader> _leaders = new();
        private readonly List<BgReplayFrame> _frames = new();
        private StepInfo? _action = new(MatchPhase.Start, MatchStepKind.Start, 0, 0);
        private bool _started;
        private MatchPhase _phase = MatchPhase.Start;
        private int _turn;

        public void Run(IEnumerable<string> lines)
        {
            foreach (var line in lines)
            {
                try
                {
                    Apply(line.Split(' '));
                }
                catch (Exception ex) when (ex is FormatException or IndexOutOfRangeException or OverflowException)
                {
                    // A damaged line is skipped; the rest of the replay still reads.
                }
            }

            Step(null);
            replay.Frames = _frames;
        }

        private void Apply(string[] t)
        {
            switch (t[0])
            {
                case "T":
                    _phase = MatchPhase.Shop;
                    _turn = Int(t[1]);
                    Step(new StepInfo(_phase, MatchStepKind.TurnStart, _turn, Int(t[2])));
                    break;
                case "F":
                    _phase = MatchPhase.Combat;
                    _turn = Int(t[1]);
                    Step(new StepInfo(_phase, MatchStepKind.CombatStart, _turn, Int(t[2])));
                    break;
                case "f":
                    // The combat's end shows the boards as the fight left them; what the game clears up after it shows
                    // with the next step.
                    Step(new StepInfo(MatchPhase.Combat, MatchStepKind.CombatEnd, _turn, Seconds)
                    {
                        Result = t[1], Taken = Int(t[2]), Dealt = Int(t[3]),
                    });
                    Step(null);
                    _action = null;
                    break;
                case "M":
                    var kind = KindOf(t[1]);
                    var actor = Id(t, 2);
                    Step(new StepInfo(_phase, kind, _turn, Seconds)
                    {
                        ActorId = actor,
                        TargetId = Id(t, 3),
                        MovedFrom = kind == MatchStepKind.Move && _cards.TryGetValue(actor, out var moved) ? moved.Position : 0,
                    });
                    break;
                case "C":
                    var choice = new BgReplayChoice(t[1], t[2] == "-" ? null : t[2], t[3].Split(',', StringSplitOptions.RemoveEmptyEntries), []);
                    if (!_started && _action is { Kind: MatchStepKind.Start })
                    {
                        // The hero choice opens the game: it is the first step itself.
                        _action = _action with { Kind = MatchStepKind.Choice, Choice = choice };
                        _started = true;
                        break;
                    }

                    Step(new StepInfo(_phase, MatchStepKind.Choice, _turn, Seconds) { Choice = choice });
                    break;
                case "c":
                    Pick(t[1]);
                    break;
                case "R":
                    _phase = MatchPhase.End;
                    Step(new StepInfo(MatchPhase.End, MatchStepKind.End, _turn, Seconds) { Place = Int(t[1]) });
                    break;
                case "E":
                    var id = Int(t[1]);
                    var card = _cards.TryGetValue(id, out var known) ? known : new CardState(id);
                    card.Side = t[2][0];
                    card.Zone = t[3];
                    card.Position = Int(t[4]);
                    card.CardId = t[5];
                    card.Type = t[6];
                    card.Tags.Clear();
                    foreach (var tag in t.Skip(7))
                    {
                        var eq = tag.IndexOf('=', StringComparison.Ordinal);
                        if (eq > 0)
                        {
                            card.Tags[tag[..eq]] = tag[(eq + 1)..];
                        }
                    }

                    _cards[id] = card;
                    break;
                case "X":
                    if (_cards.Remove(Int(t[1]), out var gone))
                    {
                        _last[gone.Id] = gone.ToCard();
                    }

                    break;
                case "Y" when _cards.TryGetValue(Int(t[1]), out var dead):
                    dead.Dying = true;
                    break;
                case "Z" when _cards.TryGetValue(Int(t[1]), out var placed):
                    placed.Position = Int(t[2]);
                    break;
                case "V" when t[1] == "L":
                    _player[t[2]] = t[3];
                    break;
                case "V" when _cards.TryGetValue(Int(t[1]), out var tagged):
                    tagged.Tags[t[2]] = t[3];
                    break;
                case "L":
                    var slot = Int(t[1]);
                    _leaders[slot] = new BgReplayLeader(slot, t[3], Int(t[4]), Int(t[5]), Int(t[6]), Int(t[7]), t[2] == "L");
                    break;
            }
        }

        private int Seconds => _frames.Count > 0 ? _frames[^1].Seconds : 0;

        /// <summary>A pick goes to the open choice, else to the last one (picked after other lines).</summary>
        private void Pick(string cardId)
        {
            if (_action is { Choice: { } open })
            {
                _action = _action with { Choice = open with { Picked = open.Picked.Append(cardId).ToList() } };
                return;
            }

            for (var i = _frames.Count - 1; i >= 0; i--)
            {
                if (_frames[i].Choice is { } done)
                {
                    _frames[i] = _frames[i] with { Choice = done with { Picked = done.Picked.Append(cardId).ToList() } };
                    return;
                }
            }
        }

        /// <summary>Closes the open step with a frame of the table as it is now, and opens <paramref name="next"/>.</summary>
        private void Step(StepInfo? next)
        {
            _started = true;
            if (_action is { } action && (action.Kind != MatchStepKind.Start || _cards.Count > 0))
            {
                var cards = _cards.Values.Select(c => c.ToCard()).OrderBy(c => c.Side).ThenBy(c => c.Zone, StringComparer.Ordinal)
                    .ThenBy(c => c.Position).ThenBy(c => c.Id).ToList();
                var resources = Tag("RESOURCES");
                _frames.Add(new BgReplayFrame(_frames.Count, action.Turn, action.Phase, action.Kind, action.Seconds)
                {
                    Actor = Find(action.ActorId),
                    Target = Find(action.TargetId),
                    Cards = cards,
                    Leaderboard = _leaders.Values.ToList(),
                    Choice = action.Choice,
                    Gold = Math.Max(0, resources + Tag("TEMP_RESOURCES") - Tag("RESOURCES_USED")),
                    MaxGold = resources,
                    Tier = Tag("PLAYER_TECH_LEVEL"),
                    Result = action.Result,
                    Taken = action.Taken,
                    Dealt = action.Dealt,
                    Place = action.Place,
                    MovedFrom = action.MovedFrom,
                });
            }

            // What died in the step is gone from the next one.
            foreach (var dead in _cards.Values.Where(c => c.Dying).ToList())
            {
                _cards.Remove(dead.Id);
                _last[dead.Id] = dead.ToCard() with { Dying = false };
            }

            _action = next;
        }

        private BgReplayCard? Find(int id) =>
            id <= 0 ? null : _cards.TryGetValue(id, out var card) ? card.ToCard() : _last.GetValueOrDefault(id);

        private int Tag(string tag) => int.TryParse(_player.GetValueOrDefault(tag), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

        private static int Id(string[] t, int index) => t.Length > index && t[index] != "-" ? Int(t[index]) : 0;

        private static MatchStepKind KindOf(string kind) => kind switch
        {
            "BUY" => MatchStepKind.Buy,
            "SELL" => MatchStepKind.Sell,
            "PLAY" => MatchStepKind.Play,
            "POWER" => MatchStepKind.HeroPower,
            "USE" => MatchStepKind.Use,
            "REFRESH" => MatchStepKind.Refresh,
            "UPGRADE" => MatchStepKind.Upgrade,
            "FREEZE" => MatchStepKind.Freeze,
            "MOVE" => MatchStepKind.Move,
            "ATTACK" => MatchStepKind.Attack,
            "DEATHS" => MatchStepKind.Deaths,
            _ => MatchStepKind.Trigger,
        };

        private static int Int(string s) => int.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }

    private sealed record StepInfo(MatchPhase Phase, MatchStepKind Kind, int Turn, int Seconds)
    {
        public int ActorId { get; init; }

        public int TargetId { get; init; }

        public int MovedFrom { get; init; }

        public BgReplayChoice? Choice { get; init; }

        public string? Result { get; init; }

        public int Taken { get; init; }

        public int Dealt { get; init; }

        public int Place { get; init; }
    }

    private sealed class CardState(int id)
    {
        public int Id { get; } = id;

        public char Side { get; set; }

        public string Zone { get; set; } = string.Empty;

        public int Position { get; set; }

        public string CardId { get; set; } = string.Empty;

        public string Type { get; set; } = string.Empty;

        public bool Dying { get; set; }

        public Dictionary<string, string> Tags { get; } = new(StringComparer.Ordinal);

        public BgReplayCard ToCard()
        {
            var damage = Int("DAMAGE");
            return new BgReplayCard(Id, Side, Zone, Position, CardId, Type)
            {
                Attack = Int("ATK"),
                Health = Int("HEALTH") - damage,
                Damaged = damage > 0,
                Armor = Int("ARMOR"),
                Golden = Int("PREMIUM") > 0,
                Taunt = Int("TAUNT") > 0,
                DivineShield = Int("DIVINE_SHIELD") > 0,
                Reborn = Int("REBORN") > 0,
                Venomous = Int("POISONOUS") > 0 || Int("VENOMOUS") > 0,
                Windfury = Int("WINDFURY") > 0 || Int("MEGA_WINDFURY") > 0,
                Stealth = Int("STEALTH") > 0,
                Frozen = Int("FROZEN") > 0,
                Tier = Int("TECH_LEVEL"),
                Dying = Dying,
            };
        }

        private int Int(string tag) => Tags.TryGetValue(tag, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
    }
}
