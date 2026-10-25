using Hearthpulse.Core.Cards;
using Hearthpulse.Core.CardPool;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Power;

namespace Hearthpulse.Core.Secrets;

/// <summary>
/// The opponent's Secrets and what each can still be (docs/secrets-helper.md). A Secret is a card back: its class
/// (the icon colour), when it came, the Mana spent on it and the visible card that made it are all the player sees.
/// Each Secret starts as every Secret of its class in the game's format and loses the ones whose trigger happened in
/// front of the player without it firing (<see cref="SecretRules"/>).
/// <para>
/// Never used: the card id the log may carry for a Secret that was a card back in hand, <c>CREATOR_DBID</c>, the COST
/// tag of the hidden card. The events are judged in windows: from one action of the player (a card played, an attack,
/// a step of the turn) to the next. If any Secret fired or left play in a window, nothing in it is ruled out - one
/// Secret firing can stop another (a countered spell, a redirected attack).
/// </para>
/// </summary>
public sealed class SecretTracker : IPowerListener
{
    private const int MaxMinions = 7;
    private const int MaxHand = 10;

    /// <summary>Needs that are facts about the event itself rather than about the board around it.</summary>
    private const SecretNeeds EventFacts = SecretNeeds.OtherTarget | SecretNeeds.AttackerNeighbor | SecretNeeds.DefenderUnshielded | SecretNeeds.Targetable;

    private readonly Dictionary<int, ActiveSecret> _active = new();
    private readonly List<PendingEvent> _pending = new();

    /// <summary>Opponent cards drawn from their deck and not shown since: card backs, whatever id the log keeps.</summary>
    private readonly HashSet<int> _hiddenDraws = new();

    /// <summary>Opponent Secrets played from a hand slot the player could see, with the card it showed.</summary>
    private readonly Dictionary<int, string> _seenInHand = new();

    /// <summary>Mana the opponent spent on a card they played (entity id).</summary>
    private readonly Dictionary<int, int> _paidCost = new();

    /// <summary>Game turn on which the opponent played a minion from their hand (entity id).</summary>
    private readonly Dictionary<int, int> _playedOnTurn = new();

    private readonly HashSet<string> _crossed = new(StringComparer.Ordinal);
    private readonly object _crossedGate = new();

    private bool _windowFired;
    private LocalPlay? _play;
    private OpponentPlay? _opponentPlay;
    private Attack? _attack;
    private HashSet<int> _activeAtTurnStart = new();
    private bool _ownerHurtThisTurn;
    private SecretPool? _pool;

    /// <summary>A Secret the opponent has in play.</summary>
    private sealed class ActiveSecret(int entityId)
    {
        public int EntityId { get; } = entityId;

        /// <summary>The CLASS tag (icon colour); empty when the log has none (the opponent's class is used).</summary>
        public string ClassTag { get; init; } = string.Empty;

        public int Turn { get; init; }

        public string? KnownCardId { get; init; }

        public bool FromDeck { get; init; }

        public string? CreatorId { get; init; }

        /// <summary>Events that happened while it was in play and did not make any Secret fire.</summary>
        public List<(SecretTrigger Trigger, SecretNeeds Held)> Seen { get; } = new();
    }

    private readonly record struct Board(int OwnerMinions, int OwnerHand, int LocalMinions);

    private sealed record PendingEvent(SecretTrigger Trigger, IReadOnlyList<int> Secrets, Board Before, SecretNeeds Facts);

    private sealed record LocalPlay(Entity Source, Board Before, IReadOnlyList<int> Secrets, int LocalMinions, bool DrawnThisTurn, int ManaBefore);

    private sealed class OpponentPlay(int entityId)
    {
        public int EntityId { get; } = entityId;

        public int Paid { get; set; }
    }

    private sealed class Attack(Entity attacker)
    {
        public Entity Attacker { get; } = attacker;

        public Entity? Defender { get; set; }

        public int DefenderDamage { get; set; }

        public int DefenderArmor { get; set; }

        public IReadOnlyList<int> Secrets { get; set; } = [];

        public Board Before { get; set; }
    }

    /// <summary>Crosses a Secret out of the list by hand (its <see cref="SecretRow.Key"/>), or brings it back. Any thread.</summary>
    public void Toggle(string key)
    {
        lock (_crossedGate)
        {
            if (!_crossed.Remove(key))
            {
                _crossed.Add(key);
            }
        }
    }

    /// <summary>True while events wait for the end of their window (see <see cref="CommitIfQuiet"/>).</summary>
    public bool HasPending => _pending.Count > 0;

    /// <summary>
    /// Closes the open window when the game has gone quiet with no block open: what the player did has fully resolved.
    /// The session calls it when no Power.log lines came for a while. True when something was ruled out.
    /// </summary>
    public bool CommitIfQuiet(GameState game) => game.OpenBlocks.Count == 0 && Commit(game);

    // ---- IPowerListener ---------------------------------------------------------------------

    public void OnGameStarted(GameState game)
    {
        _active.Clear();
        _pending.Clear();
        _hiddenDraws.Clear();
        _seenInHand.Clear();
        _paidCost.Clear();
        _playedOnTurn.Clear();
        _activeAtTurnStart = new();
        _windowFired = false;
        _play = null;
        _opponentPlay = null;
        _attack = null;
        _ownerHurtThisTurn = false;
        lock (_crossedGate)
        {
            _crossed.Clear();
        }
    }

    public void OnEntityCreated(GameState game, Entity entity) => Track(game, entity);

    public void OnEntityRevealed(GameState game, Entity entity)
    {
        if (entity.Controller == game.OpponentPlayerId && entity.InHand)
        {
            _hiddenDraws.Remove(entity.Id);
        }

        if (_active.ContainsKey(entity.Id))
        {
            _windowFired = true;
        }
    }

    public void OnBlockStart(GameState game, string blockType, Entity? source)
    {
        if (source is null)
        {
            return;
        }

        if (_active.ContainsKey(source.Id) || IsOpponentSecret(game, source))
        {
            _windowFired = true;
        }

        if (game.OpenBlocks.Count > 0 || blockType is not ("PLAY" or "ATTACK"))
        {
            return;
        }

        Commit(game);
        var local = game.LocalPlayerId;
        if (blockType == "ATTACK")
        {
            _attack = source.Controller == local && IsLocalTurn(game) ? new Attack(source) : null;
            return;
        }

        if (source.Controller == local && local != 0 && IsLocalTurn(game))
        {
            var minions = game.Controlled(local, "PLAY").Count(e => e.IsMinion && e.Id != source.Id);
            _play = new LocalPlay(source, BoardOf(game), ActiveUnknown(game), minions,
                source.InHand && source.EnteredHandTurn == game.Turn, ManaLeft(game.LocalPlayerEntity));
        }
        else if (source.Controller == game.OpponentPlayerId && source.Controller != 0)
        {
            _opponentPlay = new OpponentPlay(source.Id);
        }
    }

    public void OnBlockEnd(GameState game)
    {
        if (game.OpenBlocks.Count > 0)
        {
            return;
        }

        if (_play is { } play)
        {
            _play = null;
            RaisePlay(game, play);
        }

        if (_opponentPlay is { } opponentPlay)
        {
            _opponentPlay = null;
            if (game.Entities.GetValueOrDefault(opponentPlay.EntityId) is { } played)
            {
                if (played.Zone == "SECRET")
                {
                    _paidCost[played.Id] = opponentPlay.Paid;
                }
                else if (played.IsMinion && played.InPlay)
                {
                    _playedOnTurn[played.Id] = game.Turn;
                }
            }
        }

        if (_attack is { } attack)
        {
            _attack = null;
            if (attack.Defender is { } defender && attack.Attacker.IsMinion)
            {
                var dealt = defender.GetInt(GameTag.Damage) - attack.DefenderDamage + (attack.DefenderArmor - defender.GetInt(GameTag.Armor));
                if (dealt >= 3)
                {
                    Raise(SecretTrigger.MinionDealtThreeDamage, attack.Secrets, attack.Before, TargetableIf(attack.Attacker));
                }
            }
        }
    }

    public void OnTagChanged(GameState game, Entity entity, string tag, string previous, string value)
    {
        switch (tag)
        {
            case GameTag.Zone:
                OnZone(game, entity, previous, value);
                Track(game, entity);
                break;
            case GameTag.Secret:
                Track(game, entity);
                break;
            case GameTag.Step when entity.Id == game.GameEntityId:
                OnStep(game, value);
                break;
            case "PROPOSED_DEFENDER" when entity.Id == game.GameEntityId:
                OnDefender(game, value);
                break;
            case GameTag.Damage when entity.Id == game.OpponentHero?.Id && Int(value) > Int(previous) && IsLocalTurn(game):
                _ownerHurtThisTurn = true;
                Raise(SecretTrigger.HeroDamaged, ActiveUnknown(game), BoardOf(game), SecretNeeds.None);
                break;
            case GameTag.Armor when entity.Id == game.OpponentHero?.Id && Int(value) < Int(previous) && IsLocalTurn(game):
                _ownerHurtThisTurn = true;
                break;
            case "NUM_CARDS_PLAYED_THIS_TURN" when value == "3" && IsLocalPlayer(game, entity) && IsLocalTurn(game):
                Raise(SecretTrigger.ThirdCardInTurn, ActiveUnknown(game), BoardOf(game), SecretNeeds.None);
                break;
            case "NUM_CARDS_DRAWN_THIS_TURN" when value == "2" && IsLocalPlayer(game, entity) && IsLocalTurn(game):
                Raise(SecretTrigger.SecondDraw, ActiveUnknown(game), BoardOf(game), SecretNeeds.None);
                break;
            case "RESOURCES_USED" when _opponentPlay is { } spend && entity.Id == game.OpponentPlayerEntity?.Id && Int(value) > Int(previous):
                spend.Paid += Int(value) - Int(previous);
                break;
            case "TEMP_RESOURCES" when _opponentPlay is { } spend && entity.Id == game.OpponentPlayerEntity?.Id && Int(value) < Int(previous):
                spend.Paid += Int(previous) - Int(value);
                break;
        }
    }

    private void OnZone(GameState game, Entity entity, string previous, string value)
    {
        var opponent = game.OpponentPlayerId;
        if (opponent == 0 || entity.Controller != opponent)
        {
            return;
        }

        if (value == "HAND")
        {
            if (previous == "DECK")
            {
                _hiddenDraws.Add(entity.Id);
            }
            else
            {
                _hiddenDraws.Remove(entity.Id);
            }
        }
        else if (previous == "HAND" && value == "SECRET" && !_hiddenDraws.Contains(entity.Id) && entity.CardId.Length > 0)
        {
            // The hand slot showed this card (you gave it to them, it came back to hand face up): the player knows it.
            _seenInHand[entity.Id] = entity.CardId;
        }

        if (previous == "SECRET" && _active.Remove(entity.Id))
        {
            _windowFired = true;
        }

        if (previous == "PLAY" && value == "GRAVEYARD" && entity.IsMinion && IsLocalTurn(game))
        {
            var secrets = ActiveUnknown(game);
            var board = BoardOf(game);
            Raise(SecretTrigger.FriendlyMinionDied, secrets, board, SecretNeeds.None);
            if (_playedOnTurn.TryGetValue(entity.Id, out var played) && played == game.Turn - 1)
            {
                Raise(SecretTrigger.FriendlyMinionDiedTurnAfterPlayed, secrets, board, SecretNeeds.None);
            }
        }
    }

    private void OnStep(GameState game, string step)
    {
        Commit(game);
        var localTurn = IsLocalTurn(game);
        switch (step)
        {
            case "MAIN_READY" when localTurn:
                _activeAtTurnStart = ActiveUnknown(game).ToHashSet();
                _ownerHurtThisTurn = false;
                break;
            case "MAIN_START_TRIGGERS" when !localTurn:
            {
                var secrets = ActiveUnknown(game);
                var board = BoardOf(game);
                Raise(SecretTrigger.OwnerTurnStarted, secrets, board, SecretNeeds.None);
                if (board.OwnerMinions == 2)
                {
                    Raise(SecretTrigger.OwnerTurnStartedTwoMinions, secrets, board, SecretNeeds.None);
                }

                break;
            }

            case "MAIN_END" when localTurn:
            {
                // Only Secrets that were there the whole turn: "during your opponent's turn" counts from its start.
                var secrets = ActiveUnknown(game).Where(_activeAtTurnStart.Contains).ToList();
                var board = BoardOf(game);
                var player = game.LocalPlayerEntity;
                Raise(SecretTrigger.TurnEnded, secrets, board, SecretNeeds.None);
                if (player?.GetInt("NUM_CARDS_PLAYED_THIS_TURN") > 0)
                {
                    Raise(SecretTrigger.TurnEndedAfterPlays, secrets, board, SecretNeeds.None);
                }

                if (player is not null && ManaKnown(player) && ManaLeft(player) == 0)
                {
                    Raise(SecretTrigger.TurnEndedNoMana, secrets, board, SecretNeeds.None);
                }

                if (!_ownerHurtThisTurn)
                {
                    Raise(SecretTrigger.TurnEndedOwnerUndamaged, secrets, board, SecretNeeds.None);
                }

                break;
            }
        }
    }

    private void OnDefender(GameState game, string value)
    {
        if (_attack is not { Defender: null } attack || game.Entities.GetValueOrDefault(Int(value)) is not { } defender
            || defender.Controller != game.OpponentPlayerId)
        {
            return;
        }

        var attacker = attack.Attacker;
        attack.Defender = defender;
        attack.DefenderDamage = defender.GetInt(GameTag.Damage);
        attack.DefenderArmor = defender.GetInt(GameTag.Armor);
        attack.Secrets = ActiveUnknown(game);
        attack.Before = BoardOf(game);
        var secrets = attack.Secrets;
        var board = attack.Before;
        if (defender.Id == game.OpponentHero?.Id)
        {
            // Misdirection needs another character to send the attack to: your hero when a minion attacks, else a minion.
            var other = attacker.IsMinion || game.Entities.Values.Any(e => e.InPlay && e.IsMinion && e.Id != attacker.Id);
            Raise(SecretTrigger.HeroAttacked, secrets, board, other ? SecretNeeds.OtherTarget : SecretNeeds.None);
            if (attacker.IsMinion)
            {
                var neighbor = game.Controlled(attacker.Controller, "PLAY")
                    .Any(e => e.IsMinion && Math.Abs(e.ZonePosition - attacker.ZonePosition) == 1);
                Raise(SecretTrigger.HeroAttackedByMinion, secrets, board,
                    TargetableIf(attacker) | (neighbor ? SecretNeeds.AttackerNeighbor : SecretNeeds.None));
            }
        }
        else if (defender.IsMinion)
        {
            Raise(SecretTrigger.MinionAttacked, secrets, board,
                defender.Has(GameTag.DivineShield) ? SecretNeeds.None : SecretNeeds.DefenderUnshielded);
        }

        Raise(SecretTrigger.EnemyAttacks, secrets, board, SecretNeeds.None);
        if (attacker.IsMinion)
        {
            Raise(SecretTrigger.EnemyMinionAttacks, secrets, board, TargetableIf(attacker));
        }
    }

    private void RaisePlay(GameState game, LocalPlay play)
    {
        var source = play.Source;
        var secrets = play.Secrets;
        var board = play.Before;
        switch (source.CardType)
        {
            case "HERO_POWER":
                Raise(SecretTrigger.HeroPowerUsed, secrets, board, SecretNeeds.None);
                break;
            case "SPELL":
                Raise(SecretTrigger.SpellCast, secrets, board, SecretNeeds.None);
                if (game.Entities.GetValueOrDefault(source.GetInt("CARD_TARGET")) is { IsMinion: true })
                {
                    Raise(SecretTrigger.SpellOnMinion, secrets, board, SecretNeeds.None);
                }

                break;
            case "MINION" when source.InPlay:
                // "After your opponent plays a minion": only if it is still on the board once the play has resolved.
                Raise(SecretTrigger.MinionPlayed, secrets, board, TargetableIf(source));
                if (play.LocalMinions >= 3)
                {
                    Raise(SecretTrigger.MinionPlayedOnFourth, secrets, board, TargetableIf(source));
                }

                break;
            case "WEAPON":
                Raise(SecretTrigger.WeaponPlayed, secrets, board, SecretNeeds.None);
                break;
        }

        if (source.CardType != "HERO_POWER" && play.DrawnThisTurn)
        {
            Raise(SecretTrigger.CardPlayedSameTurnDrawn, secrets, board, SecretNeeds.None);
        }

        if (game.LocalPlayerEntity is { } player && play.ManaBefore > 0 && ManaKnown(player) && ManaLeft(player) == 0)
        {
            Raise(SecretTrigger.SpentAllMana, secrets, board, SecretNeeds.None);
        }
    }

    private void Raise(SecretTrigger trigger, IReadOnlyList<int> secrets, Board before, SecretNeeds facts)
    {
        if (secrets.Count > 0)
        {
            _pending.Add(new PendingEvent(trigger, secrets, before, facts));
        }
    }

    /// <summary>Ends the open window: its events are kept on the Secrets they saw, unless a Secret fired in it.</summary>
    private bool Commit(GameState game)
    {
        var applied = false;
        if (!_windowFired && _pending.Count > 0)
        {
            var after = BoardOf(game);
            foreach (var e in _pending)
            {
                var held = Held(e, after);
                foreach (var id in e.Secrets)
                {
                    if (_active.TryGetValue(id, out var secret))
                    {
                        secret.Seen.Add((e.Trigger, held));
                        applied = true;
                    }
                }
            }
        }

        _pending.Clear();
        _windowFired = false;
        return applied;
    }

    /// <summary>The needs that held for an event: the board ones before and after it, plus the event's own facts.</summary>
    private static SecretNeeds Held(PendingEvent e, Board after)
    {
        var held = e.Facts & EventFacts;
        if (e.Before.OwnerMinions < MaxMinions && after.OwnerMinions < MaxMinions)
        {
            held |= SecretNeeds.BoardSpace;
        }

        if (e.Before.OwnerHand < MaxHand && after.OwnerHand < MaxHand)
        {
            held |= SecretNeeds.HandSpace;
        }

        if (e.Before.OwnerMinions > 0 && after.OwnerMinions > 0)
        {
            held |= SecretNeeds.OwnerMinion;
        }

        if (e.Before.LocalMinions > 0 && after.LocalMinions > 0)
        {
            held |= SecretNeeds.EnemyMinion;
        }

        return held;
    }

    /// <summary>True when the rule's trigger was seen with everything it needs.</summary>
    internal static bool RulesOut(SecretRule rule, SecretTrigger trigger, SecretNeeds held) =>
        rule.Triggers.Contains(trigger) && (rule.Needs & ~held) == SecretNeeds.None;

    // ---- view -------------------------------------------------------------------------------

    /// <summary>The opponent's Secrets for the overlay; null when they have none in play.</summary>
    public SecretsView? BuildView(GameState game, CardDb cards)
    {
        if (game.OpponentPlayerId == 0 || !game.IsConstructedLike)
        {
            return null;
        }

        Refresh(game);
        if (_active.Count == 0)
        {
            return null;
        }

        if (_pool is null || !ReferenceEquals(_pool.Cards, cards))
        {
            _pool = new SecretPool(cards);
        }

        HashSet<string> crossed;
        lock (_crossedGate)
        {
            crossed = new HashSet<string>(_crossed, StringComparer.Ordinal);
        }

        var format = SecretPool.FormatOf(game);
        var opponentClass = cards.Get(game.OpponentHero?.CardId)?.CardClass ?? string.Empty;
        var limitCopies = format != SecretFormat.Arena && PoolContext.SupportedGameTypes.Contains(game.GameType);
        var shown = ShownCopies(game);
        var known = _active.Values.Where(s => s.KnownCardId is not null).Select(s => s.KnownCardId!).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var slots = new List<SecretSlot>();
        var rows = new Dictionary<string, (SecretOption Option, int Possible, bool Known)>(StringComparer.Ordinal);
        var classes = new List<string>();
        foreach (var secret in _active.Values.OrderBy(s => s.EntityId))
        {
            var cardClass = ClassOf(secret, opponentClass);
            if (!classes.Contains(cardClass))
            {
                classes.Add(cardClass);
            }

            var pool = _pool.For(cardClass, format);
            var paid = _paidCost.TryGetValue(secret.EntityId, out var p) ? p : (int?)null;
            if (secret.KnownCardId is { } knownId)
            {
                var option = pool.FirstOrDefault(o => o.CardIds.Contains(knownId, StringComparer.OrdinalIgnoreCase)) ?? OptionOf(cards, knownId);
                rows[option.Key] = (option, rows.GetValueOrDefault(option.Key).Possible, true);
                slots.Add(new SecretSlot(secret.EntityId, cardClass, secret.Turn, paid, CreatorName(cards, secret), knownId, 1));
                continue;
            }

            var possible = pool
                .Where(o => o.Rule is null || !secret.Seen.Any(s => RulesOut(o.Rule, s.Trigger, s.Held)))
                // The same Secret cannot be in play twice.
                .Where(o => !o.CardIds.Any(known.Contains))
                .Where(o => !limitCopies || !secret.FromDeck || o.CardIds.Sum(id => shown.GetValueOrDefault(id)) < (o.Legendary ? 1 : 2))
                .ToList();
            // A Secret cheaper than what they paid would have cost less; a cost increase can still explain it, so a
            // filter that leaves nothing is dropped rather than emptying the list.
            if (paid is > 0 and var cost && possible.Where(o => o.Cost >= cost).ToList() is { Count: > 0 } byCost)
            {
                possible = byCost;
            }

            foreach (var option in pool)
            {
                var entry = rows.GetValueOrDefault(option.Key);
                rows[option.Key] = (option, entry.Possible + (possible.Contains(option) ? 1 : 0), entry.Known);
            }

            slots.Add(new SecretSlot(secret.EntityId, cardClass, secret.Turn, paid, CreatorName(cards, secret), null,
                possible.Count(o => !crossed.Contains(o.Key))));
        }

        var list = rows.Values
            .Select(r => new SecretRow(r.Option.Key, r.Option.CardId, r.Option.Name, r.Option.Cost, r.Option.CardClass, r.Possible,
                crossed.Contains(r.Option.Key), r.Known))
            .OrderBy(r => r.IsOpen ? 0 : r.Crossed ? 1 : 2)
            .ThenBy(r => r.Known ? 0 : 1)
            .ThenBy(r => classes.IndexOf(r.CardClass))
            .ThenBy(r => r.Cost)
            .ThenBy(r => r.Name, StringComparer.CurrentCulture)
            .ToList();
        return new SecretsView(slots, list)
        {
            Pool = string.Join(", ", classes.Select(ConstructedMeta.ClassName)) + " · " + FormatName(format),
        };
    }

    private static string FormatName(SecretFormat format) => format switch
    {
        SecretFormat.Standard => Loc.T("Secrets.Format.Standard"),
        SecretFormat.Classic => Loc.T("Secrets.Format.Classic"),
        SecretFormat.Arena => Loc.T("Secrets.Format.Arena"),
        _ => Loc.T("Secrets.Format.Wild"),
    };

    private static SecretOption OptionOf(CardDb cards, string cardId)
    {
        var card = cards.Get(cardId);
        return new SecretOption(SecretRules.BaseId(cardId), cardId, card?.Name ?? cardId, card?.Cost ?? 0, card?.CardClass ?? string.Empty,
            card?.IsLegendary ?? false, SecretRules.For(cardId), [cardId]);
    }

    private static string ClassOf(ActiveSecret secret, string opponentClass) =>
        secret.ClassTag is { Length: > 0 } tag && tag is not ("INVALID" or "NEUTRAL") ? tag : opponentClass;

    private static string? CreatorName(CardDb cards, ActiveSecret secret) =>
        secret.CreatorId is { } id ? cards.NameOf(id) : null;

    /// <summary>Copies of each card from the opponent's starting deck the player has seen face up (played or revealed).</summary>
    private Dictionary<string, int> ShownCopies(GameState game)
    {
        var opponent = game.OpponentPlayerId;
        var shown = game.Entities.Values
            .Where(e => e.InitialController == opponent && e.IsOriginalDeckCard && e.CardId.Length > 0 && e.Zone == "GRAVEYARD")
            .GroupBy(e => e.CardId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        foreach (var secret in _active.Values)
        {
            if (secret is { KnownCardId: { } id, FromDeck: true })
            {
                shown[id] = shown.GetValueOrDefault(id) + 1;
            }
        }

        return shown;
    }

    // ---- state helpers ----------------------------------------------------------------------

    /// <summary>Starts tracking Secrets that entered the opponent's Secret zone (SECRET=1 can arrive after the zone).</summary>
    private void Refresh(GameState game)
    {
        var opponent = game.OpponentPlayerId;
        if (opponent == 0)
        {
            return;
        }

        foreach (var entity in game.Controlled(opponent, "SECRET"))
        {
            if (entity.Has(GameTag.Secret) && !_active.ContainsKey(entity.Id))
            {
                _active[entity.Id] = Register(game, entity);
            }
        }
    }

    /// <summary>Starts tracking a Secret the moment it is one (in their Secret zone with SECRET=1), so its turn is right.</summary>
    private void Track(GameState game, Entity entity)
    {
        if (IsOpponentSecret(game, entity) && !_active.ContainsKey(entity.Id))
        {
            _active[entity.Id] = Register(game, entity);
        }
    }

    private ActiveSecret Register(GameState game, Entity entity)
    {
        // Named only when the card that made it is itself face up: a hidden creator (CREATOR_DBID) is never used.
        var creator = game.Entities.GetValueOrDefault(entity.GetInt(GameTag.DisplayedCreator) is > 0 and var d ? d : entity.GetInt(GameTag.Creator));
        var visibleCreator = creator is { CardId.Length: > 0 } && creator.CardType is not ("PLAYER" or "GAME" or "")
                             && creator.Zone != "DECK" && !(creator.Controller == game.OpponentPlayerId && creator.Zone is "HAND" or "SECRET");
        return new ActiveSecret(entity.Id)
        {
            ClassTag = entity.GetString(GameTag.Class),
            Turn = GameState.PlayerTurnOf(game.Turn),
            KnownCardId = _seenInHand.GetValueOrDefault(entity.Id),
            FromDeck = entity.IsOriginalDeckCard,
            CreatorId = visibleCreator ? creator!.CardId : null,
        };
    }

    /// <summary>Active Secrets the player does not know (only those can be narrowed down).</summary>
    private IReadOnlyList<int> ActiveUnknown(GameState game)
    {
        Refresh(game);
        return _active.Values.Where(s => s.KnownCardId is null).Select(s => s.EntityId).ToList();
    }

    private static bool IsOpponentSecret(GameState game, Entity entity) =>
        entity.Controller == game.OpponentPlayerId && entity.Zone == "SECRET" && entity.Has(GameTag.Secret);

    private static Board BoardOf(GameState game)
    {
        int ownerMinions = 0, ownerHand = 0, localMinions = 0;
        var opponent = game.OpponentPlayerId;
        var local = game.LocalPlayerId;
        foreach (var e in game.Entities.Values)
        {
            if (e.Controller == opponent)
            {
                if (e.InPlay && e.IsMinion)
                {
                    ownerMinions++;
                }
                else if (e.InHand)
                {
                    ownerHand++;
                }
            }
            else if (e.Controller == local && e.InPlay && e.IsMinion)
            {
                localMinions++;
            }
        }

        return new Board(ownerMinions, ownerHand, localMinions);
    }

    private static bool IsLocalTurn(GameState game) => game.LocalPlayerEntity?.GetInt("CURRENT_PLAYER") == 1;

    private static bool IsLocalPlayer(GameState game, Entity entity) => entity.Id == game.LocalPlayerEntity?.Id;

    /// <summary>
    /// A minion the Secret can act on: Elusive minions (can't be targeted by spells) and Dormant ones may be skipped by
    /// a Secret, so their events never rule anything out.
    /// </summary>
    private static SecretNeeds TargetableIf(Entity minion) =>
        minion.Has("CANT_BE_TARGETED_BY_SPELLS") || minion.Has("DORMANT") || minion.Has("UNTOUCHABLE")
            ? SecretNeeds.None
            : SecretNeeds.Targetable;

    /// <summary>Mana crystals left this turn.</summary>
    private static int ManaLeft(Entity? player) =>
        player is null ? 0
        : player.GetInt("RESOURCES") + player.GetInt("TEMP_RESOURCES") - player.GetInt("RESOURCES_USED") - player.GetInt("OVERLOAD_LOCKED");

    /// <summary>Only plain Mana is judged: temporary crystals and Overload make "no Mana left" uncertain.</summary>
    private static bool ManaKnown(Entity player) => player.GetInt("TEMP_RESOURCES") == 0 && player.GetInt("OVERLOAD_LOCKED") == 0;

    private static int Int(string value) => int.TryParse(value, out var n) ? n : 0;
}
