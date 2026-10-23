using System.Globalization;

namespace Hearthpulse.Core.Replay;

public enum ReplayPlayer
{
    Local,
    Opponent,
}

/// <summary>A card on the table at one step. <see cref="CardId"/> is null for a card back.</summary>
public sealed record ReplayCard(int Id, ReplayPlayer Player, string Zone, int Position, string? CardId, string Type)
{
    public int Attack { get; init; }

    /// <summary>Health left (HEALTH minus DAMAGE; durability for a weapon).</summary>
    public int Health { get; init; }

    public bool Damaged { get; init; }

    public int Armor { get; init; }

    public int Cost { get; init; }

    public bool Golden { get; init; }

    /// <summary>Keyword tags it has now (TAUNT, DIVINE_SHIELD, FROZEN...).</summary>
    public IReadOnlyList<string> Keywords { get; init; } = [];

    /// <summary>How a card back got into the opponent's hand: start, drawn, created, returned, stolen, coin.</summary>
    public string? Origin { get; init; }

    /// <summary>The player turn it got into its zone (0 = opening hand / mulligan).</summary>
    public int EnteredTurn { get; init; }

    public bool IsCardBack => CardId is null;
}

/// <summary>One side of the table at one step.</summary>
public sealed record ReplayBoard
{
    public ReplayCard? Hero { get; init; }

    public ReplayCard? HeroPower { get; init; }

    public ReplayCard? Weapon { get; init; }

    /// <summary>Minions and locations in play, left to right.</summary>
    public IReadOnlyList<ReplayCard> Minions { get; init; } = [];

    /// <summary>The hand, left to right; the opponent's cards are card backs.</summary>
    public IReadOnlyList<ReplayCard> Hand { get; init; } = [];

    /// <summary>Secrets and quests; an opponent's secret is a card back.</summary>
    public IReadOnlyList<ReplayCard> Secrets { get; init; } = [];

    public int DeckCount { get; init; }

    /// <summary>Mana crystals this turn (temporary ones included).</summary>
    public int Mana { get; init; }

    public int ManaUsed { get; init; }

    public int OverloadLocked { get; init; }

    public int Health => Hero?.Health ?? 0;

    public int Armor => Hero?.Armor ?? 0;

    /// <summary>Total attack and health of the minions in play.</summary>
    public int BoardAttack => Minions.Where(m => m.Type == "MINION").Sum(m => m.Attack);

    public int BoardHealth => Minions.Where(m => m.Type == "MINION").Sum(m => m.Health);
}

public enum ReplayActionKind
{
    Start,
    Mulligan,
    TurnStart,
    Play,
    HeroPower,
    Attack,
    Choice,
    End,
}

/// <summary>A card that left the board during an action (died, destroyed, weapon broke).</summary>
public sealed record ReplayDeath(ReplayPlayer Player, string CardId, string Type);

/// <summary>What happened in one step.</summary>
public sealed record ReplayAction(ReplayActionKind Kind, ReplayPlayer? Player)
{
    /// <summary>The card played, the attacker, or the card that offered the choice.</summary>
    public string? CardId { get; init; }

    public string? TargetId { get; init; }

    public IReadOnlyList<string> Options { get; init; } = [];

    public IReadOnlyList<string> Picked { get; init; } = [];

    public IReadOnlyList<ReplayDeath> Deaths { get; init; } = [];

    /// <summary>The local result for <see cref="ReplayActionKind.End"/>: WON, LOST, TIED, CONCEDED.</summary>
    public string? Result { get; init; }
}

/// <summary>The table after one action resolved.</summary>
public sealed record ReplayFrame(int Index, int Turn, ReplayPlayer? Active, bool Mulligan, ReplayAction Action, ReplayBoard Local, ReplayBoard Opponent)
{
    /// <summary>The acting player's own turn number (game turns 2N-1 and 2N are turn N); 0 during the mulligan.</summary>
    public int PlayerTurn => Mulligan ? 0 : (Turn + 1) / 2;

    public ReplayBoard Of(ReplayPlayer player) => player == ReplayPlayer.Local ? Local : Opponent;
}

/// <summary>One game turn (or the mulligan, <see cref="Turn"/> 0) and its frames.</summary>
public sealed record ReplayTurn(int Turn, ReplayPlayer? Player, int FirstFrame, int LastFrame, int StartSeconds)
{
    public int PlayerTurn => (Turn + 1) / 2;

    /// <summary>Seconds the turn took, when the next turn's start is known.</summary>
    public int? Seconds { get; init; }
}

/// <summary>The local mulligan: the cards offered, kept and replaced, and what came instead.</summary>
public sealed record ReplayMulligan(IReadOnlyList<string> Offered, IReadOnlyList<string> Kept, IReadOnlyList<string> Replaced, IReadOnlyList<string> Received);

/// <summary>A card a player showed: played from hand, first on <see cref="Turn"/> (player turn), <see cref="Count"/> times.</summary>
public sealed record ShownCard(string CardId, int Turn, int Count);

/// <summary>
/// A Constructed game's replay, folded from its event lines into frames: the table after each action (game start,
/// mulligan, turn starts, plays, attacks, choices, the end). Holds only what the file holds - what the local player saw.
/// </summary>
public sealed class MatchReplay
{
    private static readonly string[] KeywordTags =
        ["TAUNT", "DIVINE_SHIELD", "STEALTH", "FROZEN", "SILENCED", "POISONOUS", "WINDFURY", "REBORN", "LIFESTEAL", "DORMANT", "IMMUNE", "DEATHRATTLE"];

    private MatchReplay(ReplayHeader header) => Header = header;

    public ReplayHeader Header { get; }

    public IReadOnlyList<ReplayFrame> Frames { get; private set; } = [];

    public IReadOnlyList<ReplayTurn> Turns { get; private set; } = [];

    public ReplayMulligan? Mulligan { get; private set; }

    /// <summary>Cards the opponent played from hand (hero powers left out), in order of first play.</summary>
    public IReadOnlyList<ShownCard> OpponentPlayed { get; private set; } = [];

    /// <summary>Cards the local player played from hand (hero powers left out).</summary>
    public IReadOnlyList<ShownCard> LocalPlayed { get; private set; } = [];

    /// <summary>Cards the local player drew after the mulligan, with the turn.</summary>
    public IReadOnlyList<ShownCard> LocalDrawn { get; private set; } = [];

    /// <summary>Every non-mulligan choice of the local player (Discover and the like).</summary>
    public IReadOnlyList<ReplayAction> Choices => Frames.Select(f => f.Action).Where(a => a.Kind == ReplayActionKind.Choice).ToList();

    /// <summary>The local result at the end (WON, LOST, TIED, CONCEDED), when the game ended.</summary>
    public string? Result { get; private set; }

    /// <summary>Reads a Constructed replay file's text; null for another kind or an unreadable file.</summary>
    public static MatchReplay? Parse(string text)
    {
        if (ReplayFile.Read(text) is not { } file || file.Header.Kind != ReplayFile.Constructed)
        {
            return null;
        }

        var replay = new MatchReplay(file.Header);
        new Folder(replay).Run(file.Body);
        return replay;
    }

    /// <summary>Folds the event lines into frames.</summary>
    private sealed class Folder(MatchReplay replay)
    {
        private readonly Dictionary<int, CardState> _cards = new();
        private readonly Dictionary<string, string>[] _players = [new(StringComparer.Ordinal), new(StringComparer.Ordinal)];
        private readonly List<ReplayFrame> _frames = new();
        private readonly List<ReplayTurn> _turns = new();
        private readonly List<(string Card, int Turn)> _opponentPlayed = new();
        private readonly List<(string Card, int Turn)> _localPlayed = new();
        private readonly List<(string Card, int Turn)> _localDrawn = new();
        private readonly List<string> _offered = new();
        private readonly List<string> _replaced = new();
        private readonly List<string> _received = new();
        private ReplayAction _action = new(ReplayActionKind.Start, null);
        private List<ReplayDeath> _deaths = new();
        private List<string> _picked = new();
        private int _turn;
        private int _turnSeconds;
        private ReplayPlayer? _active;
        private bool _mulliganDone;
        private bool _mulliganChoice;
        private bool _pendingPlay;

        public void Run(IReadOnlyList<string> lines)
        {
            foreach (var line in lines)
            {
                var t = line.Split(' ');
                try
                {
                    Apply(t);
                }
                catch (Exception ex) when (ex is FormatException or IndexOutOfRangeException or OverflowException)
                {
                    // A damaged line is skipped; the rest of the replay still reads.
                }
            }

            Step(null);
            CloseTurn(null);
            replay.Frames = _frames;
            replay.Turns = _turns;
            replay.OpponentPlayed = Shown(_opponentPlayed);
            replay.LocalPlayed = Shown(_localPlayed);
            replay.LocalDrawn = Shown(_localDrawn);
            if (_offered.Count > 0)
            {
                // What went back is what left the hand while the new cards were dealt (the Coin cannot go back).
                var kept = new List<string>(_offered);
                foreach (var card in _replaced)
                {
                    kept.Remove(card);
                }

                replay.Mulligan = new ReplayMulligan(_offered.ToList(), kept, _replaced.ToList(), _received.ToList());
            }
        }

        private void Apply(string[] t)
        {
            switch (t[0])
            {
                case "T":
                    var turn = Int(t[1]);
                    var side = Side(t[2]);
                    if (!_mulliganDone && turn <= 1)
                    {
                        // TURN is 1 from before the mulligan: the turn really starts at MAIN_READY.
                        _turn = turn;
                        _active = side;
                        _turnSeconds = Int(t[3]);
                        break;
                    }

                    if (!_mulliganDone)
                    {
                        // Recording picked the game up after the mulligan (a reconnect).
                        _mulliganDone = true;
                        _turnStartFrame = _frames.Count;
                    }

                    Step(new ReplayAction(ReplayActionKind.TurnStart, side));
                    CloseTurn(Int(t[3]));
                    _turn = turn;
                    _active = side;
                    _turnSeconds = Int(t[3]);
                    break;
                case "S" when t[1] == "BEGIN_MULLIGAN" && !_mulliganDone:
                    Step(new ReplayAction(ReplayActionKind.Mulligan, null));
                    break;
                case "S" when t[1] == "MAIN_READY" && !_mulliganDone:
                    Step(new ReplayAction(ReplayActionKind.TurnStart, _active));
                    // The mulligan is turn 0 of the turn list.
                    _turns.Add(new ReplayTurn(0, null, 0, Math.Max(0, _frames.Count - 1), 0) { Seconds = _turnSeconds });
                    _mulliganDone = true;
                    _turnStartFrame = _frames.Count;
                    break;
                case "E":
                    ApplyEntity(t);
                    break;
                case "X":
                    if (_cards.Remove(Int(t[1]), out var gone) && Dealing && gone is { Player: ReplayPlayer.Local, Zone: "HAND", CardId: { } back })
                    {
                        _replaced.Add(back);
                    }

                    break;
                case "Z" when _cards.TryGetValue(Int(t[1]), out var moved):
                    moved.Position = Int(t[2]);
                    break;
                case "V" when t[1] is "L" or "O":
                    _players[t[1] == "L" ? 0 : 1][t[2]] = t[3];
                    break;
                case "V" when _cards.TryGetValue(Int(t[1]), out var tagged):
                    tagged.Tags[t[2]] = t[3];
                    break;
                case "B":
                    Step(new ReplayAction(ReplayActionKind.Play, _active));
                    _pendingPlay = true;
                    break;
                case "P":
                    Play(t);
                    break;
                case "A":
                    var attacker = Card(t[1]);
                    Step(new ReplayAction(ReplayActionKind.Attack, attacker?.Player ?? _active)
                    {
                        CardId = attacker?.CardId,
                        TargetId = Card(t[2])?.CardId,
                    });
                    break;
                case "C" when t[1] == "MULLIGAN":
                    _mulliganChoice = true;
                    _offered.Clear();
                    _offered.AddRange(Ids(t[3]));
                    break;
                case "C":
                    _mulliganChoice = false;
                    Step(new ReplayAction(ReplayActionKind.Choice, ReplayPlayer.Local)
                    {
                        CardId = t[2] == "-" ? null : t[2],
                        Options = Ids(t[3]),
                    });
                    break;
                case "c" when _mulliganChoice && !_mulliganDone:
                    // The kept cards; what was replaced is read from the hand itself.
                    break;
                case "c":
                    _picked.Add(t[1]);
                    break;
                case "R":
                    Step(new ReplayAction(ReplayActionKind.End, ReplayPlayer.Local) { Result = t[1] });
                    replay.Result = t[1];
                    break;
            }
        }

        private int _turnStartFrame;

        private void Play(string[] t)
        {
            var card = Card(t[1]);
            var kind = card?.Type == "HERO_POWER" ? ReplayActionKind.HeroPower : ReplayActionKind.Play;
            var action = new ReplayAction(kind, card?.Player ?? _active) { CardId = card?.CardId, TargetId = Card(t[2])?.CardId };
            if (_pendingPlay)
            {
                // The step began at the play's B line: fill in what was played.
                _action = action;
                _pendingPlay = false;
            }
            else
            {
                Step(action);
            }

            if (kind == ReplayActionKind.Play && card?.CardId is { } id)
            {
                (card.Player == ReplayPlayer.Local ? _localPlayed : _opponentPlayed).Add((id, PlayerTurn));
            }
        }

        private void ApplyEntity(string[] t)
        {
            var id = Int(t[1]);
            var player = Side(t[2]) ?? ReplayPlayer.Opponent;
            var zone = t[3];
            var card = t[5] == "-" ? null : t[5];
            _cards.TryGetValue(id, out var state);
            if (state is not null && state.Zone == "PLAY" && zone == "GRAVEYARD" && state.CardId is { } dead
                && state.Type is "MINION" or "WEAPON" or "LOCATION" or "HERO")
            {
                _deaths.Add(new ReplayDeath(state.Player, dead, state.Type));
            }

            var enteredHand = zone == "HAND" && state?.Zone != "HAND";
            state ??= new CardState(id);
            state.Player = player;
            state.Zone = zone;
            state.Position = Int(t[4]);
            state.CardId = card;
            state.Type = t[6];
            state.Tags.Clear();
            foreach (var pair in t.Skip(7))
            {
                var eq = pair.IndexOf('=');
                if (eq > 0)
                {
                    state.Tags[pair[..eq]] = pair[(eq + 1)..];
                }
            }

            if (enteredHand || state.EnteredTurn < 0)
            {
                state.EnteredTurn = PlayerTurn;
            }

            _cards[id] = state;
            if (enteredHand && player == ReplayPlayer.Local && card is not null)
            {
                if (Dealing)
                {
                    _received.Add(card);
                }
                else if (_mulliganDone)
                {
                    _localDrawn.Add((card, PlayerTurn));
                }
            }
        }

        private int PlayerTurn => _mulliganDone ? (_turn + 1) / 2 : 0;

        /// <summary>The local mulligan is dealing the replacements.</summary>
        private bool Dealing => !_mulliganDone && _players[0].GetValueOrDefault("MULLIGAN_STATE") == "DEALING";

        /// <summary>Closes the current step with a frame of the table as it is now, and starts <paramref name="next"/>.</summary>
        private void Step(ReplayAction? next)
        {
            var action = _action with
            {
                Deaths = _deaths.Count > 0 ? _deaths : [],
                Picked = _picked.Count > 0 ? _picked : _action.Picked,
            };
            _frames.Add(new ReplayFrame(_frames.Count, _turn, _active, !_mulliganDone, action, Board(ReplayPlayer.Local), Board(ReplayPlayer.Opponent)));
            _deaths = new List<ReplayDeath>();
            _picked = new List<string>();
            _pendingPlay = false;
            if (next is not null)
            {
                _action = next;
            }
        }

        private void CloseTurn(int? nextStart)
        {
            if (!_mulliganDone || _turnStartFrame >= _frames.Count)
            {
                return;
            }

            _turns.Add(new ReplayTurn(_turn, _active, _turnStartFrame, _frames.Count - 1, _turnSeconds)
            {
                Seconds = nextStart is { } next ? Math.Max(0, next - _turnSeconds) : null,
            });
            _turnStartFrame = _frames.Count;
        }

        private ReplayBoard Board(ReplayPlayer player)
        {
            var mine = _cards.Values.Where(c => c.Player == player).ToList();
            var tags = _players[player == ReplayPlayer.Local ? 0 : 1];
            return new ReplayBoard
            {
                Hero = mine.Where(c => c is { Zone: "PLAY", Type: "HERO" }).Select(ToCard).LastOrDefault(),
                HeroPower = mine.Where(c => c is { Zone: "PLAY", Type: "HERO_POWER" }).Select(ToCard).LastOrDefault(),
                Weapon = mine.Where(c => c is { Zone: "PLAY", Type: "WEAPON" }).Select(ToCard).LastOrDefault(),
                Minions = mine.Where(c => c is { Zone: "PLAY", Type: "MINION" or "LOCATION" }).OrderBy(c => c.Position).ThenBy(c => c.Id).Select(ToCard).ToList(),
                Hand = mine.Where(c => c.Zone == "HAND").OrderBy(c => c.Position).ThenBy(c => c.Id).Select(ToCard).ToList(),
                Secrets = mine.Where(c => c.Zone == "SECRET").OrderBy(c => c.Id).Select(ToCard).ToList(),
                DeckCount = mine.Count(c => c.Zone == "DECK"),
                Mana = Tag(tags, "RESOURCES") + Tag(tags, "TEMP_RESOURCES"),
                ManaUsed = Tag(tags, "RESOURCES_USED"),
                OverloadLocked = Tag(tags, "OVERLOAD_LOCKED"),
            };
        }

        private static ReplayCard ToCard(CardState c)
        {
            var health = Tag(c.Tags, "HEALTH");
            if (c.Type == "WEAPON" && health == 0)
            {
                health = Tag(c.Tags, "DURABILITY");
            }

            var damage = Tag(c.Tags, "DAMAGE");
            return new ReplayCard(c.Id, c.Player, c.Zone, c.Position, c.CardId, c.Type)
            {
                Attack = Tag(c.Tags, "ATK"),
                Health = health - damage,
                Damaged = damage > 0,
                Armor = Tag(c.Tags, "ARMOR"),
                Cost = Tag(c.Tags, "COST"),
                Golden = Tag(c.Tags, "PREMIUM") > 0,
                Keywords = KeywordTags.Where(k => Tag(c.Tags, k) > 0).ToList(),
                Origin = c.Tags.GetValueOrDefault("ORIGIN"),
                EnteredTurn = Math.Max(0, c.EnteredTurn),
            };
        }

        private CardState? Card(string token) =>
            token != "-" && int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? _cards.GetValueOrDefault(id) : null;

        private static IReadOnlyList<ShownCard> Shown(List<(string Card, int Turn)> cards) =>
            cards.GroupBy(c => c.Card, StringComparer.Ordinal)
                .Select(g => new ShownCard(g.Key, g.Min(c => c.Turn), g.Count()))
                .OrderBy(c => c.Turn).ThenBy(c => c.CardId, StringComparer.Ordinal)
                .ToList();

        private static IReadOnlyList<string> Ids(string token) =>
            token == "-" ? [] : token.Split(',', StringSplitOptions.RemoveEmptyEntries);

        private static ReplayPlayer? Side(string token) => token switch
        {
            "L" => ReplayPlayer.Local,
            "O" => ReplayPlayer.Opponent,
            _ => null,
        };

        private static int Int(string token) => int.Parse(token, NumberStyles.Integer, CultureInfo.InvariantCulture);

        private static int Tag(Dictionary<string, string> tags, string tag) =>
            tags.TryGetValue(tag, out var value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : 0;
    }

    private sealed class CardState(int id)
    {
        public int Id { get; } = id;

        public ReplayPlayer Player { get; set; }

        public string Zone { get; set; } = string.Empty;

        public int Position { get; set; }

        public string? CardId { get; set; }

        public string Type { get; set; } = "-";

        public int EnteredTurn { get; set; } = -1;

        public Dictionary<string, string> Tags { get; } = new(StringComparer.Ordinal);
    }
}
