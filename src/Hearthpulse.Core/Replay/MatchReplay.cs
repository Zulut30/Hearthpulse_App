using System.Globalization;

namespace Hearthpulse.Core.Replay;

/// <summary>
/// A Constructed game's replay, folded from its event lines into frames: the table after each action (game start,
/// mulligan, turn starts, plays, attacks, choices, the end). The table is what the local player saw; a file of a
/// finished game (format 3) adds what the log revealed by the end about the opponent's card backs
/// (<see cref="ReplayCard.Reveal"/>, <see cref="Reveals"/>, <see cref="OpponentMulligan"/>), shown only after the game.
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

    /// <summary>
    /// Every card that came into the local hand after the mulligan (drawn or created), with its player turn, in order.
    /// </summary>
    public IReadOnlyList<(string CardId, int Turn)> LocalHandEntries { get; private set; } = [];

    /// <summary>Every non-mulligan choice of the local player (Discover and the like).</summary>
    public IReadOnlyList<ReplayAction> Choices { get; private set; } = [];

    /// <summary>The local result at the end (WON, LOST, TIED, CONCEDED), when the game ended.</summary>
    public string? Result { get; private set; }

    /// <summary>Every opponent card the log revealed by the end of the game, by entity (empty for a format 2 file).</summary>
    public IReadOnlyList<ReplayReveal> Reveals { get; private set; } = [];

    /// <summary>The opponent's mulligan, when the file has the post-game part and the mulligan was recorded.</summary>
    public ReplayOpponentMulligan? OpponentMulligan { get; private set; }

    /// <summary>True when the file has the post-game part (format 3), so card backs can show what they were.</summary>
    public bool HasPostGame { get; private set; }

    /// <summary>Reads a Constructed replay file's text; null for another kind or an unreadable file.</summary>
    public static MatchReplay? Parse(string text) => Parse(text, reference: false);

    /// <summary>
    /// Reads a Constructed replay as the local player saw it during the game: the post-game part (what the log revealed
    /// by the end about the opponent's card backs) is left out, so no card back has a name (ADR 0005). Game records
    /// (docs/game-stats.md) are built from this.
    /// </summary>
    public static MatchReplay? ParseSeen(string text)
    {
        if (ReplayFile.Read(text) is not { } file || file.Header.Kind != ReplayFile.Constructed)
        {
            return null;
        }

        var replay = new MatchReplay(file.Header);
        new Folder(replay, reference: false).Run(file.Body.Where(line => !IsPostGame(line)).ToList());
        return replay;
    }

    private static bool IsPostGame(string line) => line.Length > 2 && line[0] is 'N' or 'K' && line[1] == ' ';

    /// <summary>
    /// <paramref name="reference"/>: every frame walks all the game's cards and builds each card anew, as before the
    /// index and the shared cards (plan R5); tests check the frames are the same both ways.
    /// </summary>
    internal static MatchReplay? Parse(string text, bool reference)
    {
        if (ReplayFile.Read(text) is not { } file || file.Header.Kind != ReplayFile.Constructed)
        {
            return null;
        }

        var replay = new MatchReplay(file.Header);
        new Folder(replay, reference).Run(file.Body);
        return replay;
    }

    /// <summary>Folds the event lines into frames.</summary>
    private sealed class Folder(MatchReplay replay, bool reference)
    {
        private readonly Dictionary<int, CardState> _cards = new();

        // The cards on the table, in hand or secret (what a frame shows): a long game's graveyard is not walked at every
        // step (plan R5: a file at the event cap opened in seconds, not ten).
        private readonly Dictionary<int, CardState> _shown = new();
        private int _order;
        private readonly Dictionary<string, string>[] _players = [new(StringComparer.Ordinal), new(StringComparer.Ordinal)];
        private readonly List<ReplayFrame> _frames = new();
        private readonly int[] _decks = new int[2];
        private readonly List<ReplayTurn> _turns = new();
        private readonly List<(string Card, int Turn)> _opponentPlayed = new();
        private readonly List<(string Card, int Turn)> _localPlayed = new();
        private readonly List<(string Card, int Turn)> _localDrawn = new();
        private readonly List<string> _offered = new();
        private readonly List<string> _replaced = new();
        private readonly List<string> _received = new();

        // Post-game part: replay id -> game entity of an opponent card back, entity -> what the log revealed.
        private readonly Dictionary<int, int> _entities = new();
        private readonly Dictionary<int, ReplayReveal> _reveals = new();
        private List<int>? _opponentOffered;
        private List<int>? _opponentFinal;
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
            // The post-game part follows the events; read it first so card backs know what they were from the start.
            foreach (var line in lines)
            {
                if (line.Length > 2 && line[0] is 'N' or 'K' && line[1] == ' ')
                {
                    try
                    {
                        PostGame(line.Split(' '));
                    }
                    catch (Exception ex) when (ex is FormatException or IndexOutOfRangeException or OverflowException)
                    {
                        // A damaged line is skipped.
                    }
                }
            }

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
            if (!_mulliganDone && _frames.Count > 0)
            {
                // Over before MAIN_READY (conceded in the mulligan): the mulligan is still the game's turn 0.
                _turns.Add(new ReplayTurn(0, null, 0, _frames.Count - 1) { Seconds = _turnSeconds });
            }

            CloseTurn(null);
            replay.Frames = _frames;
            replay.Choices = _frames.Select(f => f.Action).Where(a => a.Kind == ReplayActionKind.Choice).ToList();
            replay.Turns = _turns;
            replay.OpponentPlayed = Shown(_opponentPlayed);
            replay.LocalPlayed = Shown(_localPlayed);
            replay.LocalDrawn = Shown(_localDrawn);
            replay.LocalHandEntries = _localDrawn.ToList();
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

            replay.HasPostGame = _entities.Count > 0 || _reveals.Count > 0;
            replay.Reveals = _reveals.Values.OrderBy(r => r.Turn).ThenBy(r => r.EntityId).ToList();
            if (replay.HasPostGame && _opponentOffered is { Count: > 0 } && _opponentFinal is not null)
            {
                // The Coin is not a mulligan card. It can be in hand before the mulligan starts, as the fifth card and
                // hidden (the log names it only when it is played): at most four cards are offered. The cards are told
                // by the hand itself, not by what the log called them later: a card dealt from the deck counts even
                // when its reveal says "created" (put into the deck by a start-of-game effect, or transformed in hand
                // before it was first named).
                var offered = _opponentOffered.Where(e => !Coin(e)).Take(4).ToList();
                var coin = _opponentOffered.Where(e => !Coin(e)).Skip(4).ToHashSet();
                var final = _opponentFinal.Where(e => !Coin(e) && !coin.Contains(e)).ToList();
                string? Of(int entity) => _reveals.TryGetValue(entity, out var r) ? r.CardId : null;
                replay.OpponentMulligan = new ReplayOpponentMulligan(
                    offered.Where(final.Contains).Select(Of).ToList(),
                    offered.Where(e => !final.Contains(e)).Select(Of).ToList(),
                    final.Where(e => !offered.Contains(e)).Select(Of).ToList());
            }
        }

        private void PostGame(string[] t)
        {
            if (t[0] == "N")
            {
                _entities[Int(t[1])] = Int(t[2]);
            }
            else if (t.Length >= 6 && t[2] != "-")
            {
                var entity = Int(t[1]);
                _reveals[entity] = new ReplayReveal(entity, t[2], GameTurnToPlayer(Int(t[3])), t[4], t[5] == "1", t.Length > 6 ? Int(t[6]) : 0);
            }
        }

        /// <summary>The log named the card as the Coin (GAME_005 or a skin: EDR_COIN1, JAIL_COIN3...).</summary>
        private bool Coin(int entity) =>
            _reveals.TryGetValue(entity, out var r) && (r.CardId == "GAME_005" || r.CardId.Contains("_COIN", StringComparison.Ordinal));

        private static int GameTurnToPlayer(int turn) => turn <= 0 ? 0 : (turn + 1) / 2;

        /// <summary>The game entities of the opponent's card backs in hand now (the Coin left out), left to right.</summary>
        private List<int> OpponentHandEntities() =>
            _shown.Values.Where(c => c is { Player: ReplayPlayer.Opponent, Zone: "HAND" } && c.Tags.GetValueOrDefault("ORIGIN") != "coin")
                .OrderBy(c => c.Position).ThenBy(c => c.Id)
                .Select(c => _entities.GetValueOrDefault(c.Id)).Where(e => e > 0).ToList();

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
                    _opponentOffered = OpponentHandEntities();
                    break;
                case "S" when t[1] == "MAIN_READY" && !_mulliganDone:
                    _opponentFinal = OpponentHandEntities();
                    Step(new ReplayAction(ReplayActionKind.TurnStart, _active));
                    // The mulligan is turn 0 of the turn list.
                    _turns.Add(new ReplayTurn(0, null, 0, Math.Max(0, _frames.Count - 1)) { Seconds = _turnSeconds });
                    _mulliganDone = true;
                    _turnStartFrame = _frames.Count;
                    break;
                case "E":
                    ApplyEntity(t);
                    break;
                case "X":
                    _shown.Remove(Int(t[1]));
                    if (_cards.Remove(Int(t[1]), out var gone) && Dealing && gone is { Player: ReplayPlayer.Local, Zone: "HAND", CardId: { } back })
                    {
                        _replaced.Add(back);
                    }

                    break;
                case "D":
                    _decks[t[1] == "L" ? 0 : 1] = Int(t[2]);
                    break;
                case "Z" when _cards.TryGetValue(Int(t[1]), out var moved):
                    moved.Position = Int(t[2]);
                    moved.View = null;
                    break;
                case "V" when t[1] is "L" or "O":
                    _players[t[1] == "L" ? 0 : 1][t[2]] = t[3];
                    break;
                case "V" when _cards.TryGetValue(Int(t[1]), out var tagged):
                    tagged.Tags[t[2]] = t[3];
                    tagged.View = null;
                    break;
                case "B":
                    Step(new ReplayAction(ReplayActionKind.Play, _active));
                    _pendingPlay = true;
                    break;
                case "P":
                    Play(t, used: false);
                    break;
                case "U":
                    Play(t, used: true);
                    break;
                case "A":
                    var attacker = Card(t[1]);
                    var defender = Card(t[2]);
                    Step(new ReplayAction(ReplayActionKind.Attack, attacker?.Player ?? _active)
                    {
                        CardId = IdOf(attacker),
                        TargetId = defender?.CardId,
                        ActorRef = attacker?.Id,
                        TargetRef = defender?.Id,
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

        private void Play(string[] t, bool used)
        {
            var card = Card(t[1]);
            var kind = card?.Type == "HERO_POWER" ? ReplayActionKind.HeroPower : used ? ReplayActionKind.Use : ReplayActionKind.Play;
            // A card back played (a secret) is named when the log revealed it by the end of the game.
            var target = Card(t[2]);
            var action = new ReplayAction(kind, card?.Player ?? _active) { CardId = IdOf(card), TargetId = target?.CardId, ActorRef = card?.Id, TargetRef = target?.Id };
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

            if (kind == ReplayActionKind.Play && card is not null && IdOf(card) is { } id)
            {
                (card.Player == ReplayPlayer.Local ? _localPlayed : _opponentPlayed).Add((id, PlayerTurn));
            }
        }

        private void ApplyEntity(string[] t)
        {
            // Every number and field first: a damaged line is skipped whole, not half applied.
            var id = Int(t[1]);
            var player = Side(t[2]) ?? ReplayPlayer.Opponent;
            var zone = t[3];
            var position = Int(t[4]);
            var card = t[5] == "-" ? null : t[5];
            var type = t[6];
            _cards.TryGetValue(id, out var state);
            if (state is not null && state.Zone == "PLAY" && zone == "GRAVEYARD" && state.CardId is { } dead
                && state.Type is "MINION" or "WEAPON" or "LOCATION" or "HERO")
            {
                _deaths.Add(new ReplayDeath(state.Player, dead, state.Type) { Ref = id });
            }

            var enteredHand = zone == "HAND" && state?.Zone != "HAND";
            state ??= new CardState(id) { Order = _order++ };
            state.View = null;
            state.Player = player;
            state.Zone = zone;
            state.Position = position;
            state.CardId = card;
            state.Type = type;
            state.Tags.Clear();
            foreach (var pair in t.Skip(7))
            {
                var eq = pair.IndexOf('=', StringComparison.Ordinal);
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
            if (zone is "PLAY" or "HAND" or "SECRET")
            {
                _shown[id] = state;
            }
            else
            {
                _shown.Remove(id);
            }

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

            _turns.Add(new ReplayTurn(_turn, _active, _turnStartFrame, _frames.Count - 1)
            {
                Seconds = nextStart is { } next ? Math.Max(0, next - _turnSeconds) : null,
            });
            _turnStartFrame = _frames.Count;
        }

        private ReplayBoard Board(ReplayPlayer player)
        {
            // In the order the cards were first seen: the order of the whole list walked before, except that a slot an
            // X line freed was reused by the next new card (it came early, now it comes last).
            var mine = reference
                ? _cards.Values.Where(c => c.Player == player).ToList()
                : _shown.Values.Where(c => c.Player == player).OrderBy(c => c.Order).ToList();
            var tags = _players[player == ReplayPlayer.Local ? 0 : 1];
            return new ReplayBoard
            {
                Hero = mine.Where(c => c is { Zone: "PLAY", Type: "HERO" }).Select(ToCard).LastOrDefault(),
                HeroPower = mine.Where(c => c is { Zone: "PLAY", Type: "HERO_POWER" }).Select(ToCard).LastOrDefault(),
                Weapon = mine.Where(c => c is { Zone: "PLAY", Type: "WEAPON" }).Select(ToCard).LastOrDefault(),
                Minions = mine.Where(c => c is { Zone: "PLAY", Type: "MINION" or "LOCATION" }).OrderBy(c => c.Position).ThenBy(c => c.Id).Select(ToCard).ToList(),
                Hand = mine.Where(c => c.Zone == "HAND").OrderBy(c => c.Position).ThenBy(c => c.Id).Select(ToCard).ToList(),
                Secrets = mine.Where(c => c.Zone == "SECRET").OrderBy(c => c.Id).Select(ToCard).ToList(),
                DeckCount = _decks[player == ReplayPlayer.Local ? 0 : 1],
                Mana = Tag(tags, "RESOURCES") + Tag(tags, "TEMP_RESOURCES"),
                ManaUsed = Tag(tags, "RESOURCES_USED"),
                OverloadLocked = Tag(tags, "OVERLOAD_LOCKED"),
            };
        }

        /// <summary>A card's id: face up, else what the log revealed by the end of the game, else null.</summary>
        private string? IdOf(CardState? card) => card is null ? null : card.CardId ?? RevealOf(card)?.CardId;

        private ReplayReveal? RevealOf(CardState card) =>
            card.CardId is null && _entities.TryGetValue(card.Id, out var entity) ? _reveals.GetValueOrDefault(entity) : null;

        /// <summary>The card as a frame shows it, made again only after the card changed: frames share it (memory of a long game).</summary>
        private ReplayCard ToCard(CardState c) => reference ? NewCard(c) : c.View ??= NewCard(c);

        private ReplayCard NewCard(CardState c)
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
                Reveal = RevealOf(c),
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

        /// <summary>When the card was first seen: the order the frame's lists had.</summary>
        public int Order { get; init; }

        /// <summary>The card's last <see cref="ReplayCard"/>, until the card changes.</summary>
        public ReplayCard? View { get; set; }
    }
}
