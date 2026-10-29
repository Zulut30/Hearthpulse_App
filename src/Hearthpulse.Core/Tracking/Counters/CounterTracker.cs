using System.Globalization;
using Hearthpulse.Core.Power;

namespace Hearthpulse.Core.Tracking.Counters;

/// <summary>
/// A card a player played from hand. The opponent's cards are still hidden when the play starts, so what the card is
/// (type, cost, school) is read from the entity once it is revealed: <see cref="Entity"/>.
/// </summary>
public sealed record PlayedCardInfo(Entity Entity, int Turn)
{
    public string CardId => Entity.CardId;

    public string CardType => Entity.CardType;

    // The cost it was played for. A card played for 0 has the tag at 0, and its COST may go back up after the play.
    public int Cost => Entity.Tags.ContainsKey("TAG_LAST_KNOWN_COST_IN_HAND") ? Entity.GetInt("TAG_LAST_KNOWN_COST_IN_HAND") : Entity.GetInt(GameTag.Cost);

    public int SpellSchool => Entity.GetInt("SPELL_SCHOOL");

    public int Overload => Entity.GetInt("OVERLOAD");

    public bool FromDeck => Entity.IsOriginalDeckCard;

    /// <summary>The entity the card was played on (spell or Battlecry target), 0 when none.</summary>
    public int Target => Entity.GetInt("CARD_TARGET");
}

/// <summary>
/// Records what each player did this game that the game itself does not count in a tag: cards played (with cost,
/// spell school, origin), minions summoned and died, cards discarded, attacks and draws. Counters read these logs
/// together with the player tags the client keeps (CORPSES, CTHUN_ABILITY*_USED, HERALD_COLOSSAL_AMOUNT...).
/// </summary>
public sealed class CounterTracker : IPowerListener
{
    private readonly Dictionary<int, PlayerLog> _logs = [];
    private Entity? _pendingChoice;

    /// <summary>
    /// The counters built for the last update: <see cref="Counters.Build"/> hands one out again when it would come out
    /// the same, so an update builds only what changed. Off: every counter is built (tests compare both ways).
    /// </summary>
    internal Counters.CounterViews Views { get; } = new();

    /// <summary>What one player did this game.</summary>
    public sealed class PlayerLog
    {
        public List<PlayedCardInfo> Played { get; } = [];

        /// <summary>Minions that entered play (played or summoned), by card id.</summary>
        public List<string> Summoned { get; } = [];

        /// <summary>Friendly minions that died, by card id.</summary>
        public List<string> Died { get; } = [];

        /// <summary>Friendly weapons and locations that went from play to the graveyard, by card id.</summary>
        public List<string> Destroyed { get; } = [];

        public List<string> Discarded { get; } = [];

        public int Attacks { get; internal set; }

        public int HeroAttacks { get; internal set; }

        public int Drawn { get; internal set; }

        /// <summary>Damage the player's hero took while it was the player's own turn.</summary>
        public int DamageTakenOnOwnTurn { get; internal set; }

        /// <summary>Picks the local player made, with the card that offered them (Discover, Adapt, C'Thun abilities...).</summary>
        public List<(Entity Source, int Turn)> Choices { get; } = [];

        /// <summary>Minions that died while a card this player played was resolving, by the played entity's id.</summary>
        public Dictionary<int, int> KillsByPlay { get; } = [];

        /// <summary>Triggered effects (BlockType=TRIGGER: "at the end of your turn"...) of the player's cards, by card id.</summary>
        public Dictionary<string, int> Triggers { get; } = new(StringComparer.Ordinal);

        /// <summary>Times each quest reached its goal, by card id (repeatable quests complete more than once).</summary>
        public Dictionary<string, int> QuestsCompleted { get; } = new(StringComparer.Ordinal);
    }

    public PlayerLog For(int playerId) => _logs.TryGetValue(playerId, out var log) ? log : _logs[playerId] = new PlayerLog();

    public void OnGameStarted(GameState game)
    {
        _logs.Clear();
        _pendingChoice = null;
    }

    public void OnBlockStart(GameState game, string blockType, Entity? source)
    {
        if (source is null || source.Controller == 0)
        {
            return;
        }

        // A hand card's type may still be unknown here (the opponent's), so only what it can't be is ruled out.
        if (blockType == "PLAY" && source.CardType is not ("HERO_POWER" or "GAME" or "PLAYER" or "ENCHANTMENT") && source.Zone is "HAND" or "")
        {
            var log = For(source.Controller);
            if (log.Played.TrueForAll(p => p.Entity.Id != source.Id))
            {
                log.Played.Add(new PlayedCardInfo(source, PlayerTurn(game)));
            }
        }
        else if (blockType == "ATTACK")
        {
            var log = For(source.Controller);
            log.Attacks++;
            if (source.IsHero)
            {
                log.HeroAttacks++;
            }
        }
        else if (blockType == "TRIGGER" && source.CardId.Length > 0)
        {
            var triggers = For(source.Controller).Triggers;
            triggers[source.CardId] = triggers.GetValueOrDefault(source.CardId) + 1;
        }
    }

    public void OnChoices(GameState game, string choiceType, Entity? source, IReadOnlyList<Entity> options) =>
        _pendingChoice = choiceType == "GENERAL" ? source : null;

    public void OnChoiceMade(GameState game)
    {
        if (_pendingChoice is { } source && game.LocalPlayerId != 0)
        {
            For(game.LocalPlayerId).Choices.Add((source, PlayerTurn(game)));
        }

        _pendingChoice = null;
    }

    public void OnTagChanged(GameState game, Entity entity, string tag, string previous, string value)
    {
        if (tag == "NUM_CARDS_DRAWN_THIS_TURN")
        {
            // The per-turn count resets to 0 each turn; only increases are draws.
            var drawn = Delta(previous, value);
            if (drawn > 0 && game.Players.Values.FirstOrDefault(p => p.EntityId == entity.Id) is { } slot)
            {
                For(slot.PlayerId).Drawn += drawn;
            }

            return;
        }

        if (tag == GameTag.Damage && entity.IsHero && entity.InPlay && entity.Controller != 0)
        {
            var taken = Delta(previous, value);
            if (taken > 0 && IsCurrentPlayer(game, entity.Controller))
            {
                For(entity.Controller).DamageTakenOnOwnTurn += taken;
            }

            return;
        }

        if (tag == "QUEST_PROGRESS" && entity.Controller != 0 && entity.CardId.Length > 0)
        {
            var total = entity.GetInt("QUEST_PROGRESS_TOTAL");
            if (total > 0 && Parse(value) >= total && Parse(previous) < total)
            {
                var done = For(entity.Controller).QuestsCompleted;
                done[entity.CardId] = done.GetValueOrDefault(entity.CardId) + 1;
            }

            return;
        }

        if (tag != GameTag.Zone || entity.Controller == 0 || entity.CardId.Length == 0)
        {
            return;
        }

        var player = For(entity.Controller);
        if (entity.IsMinion && value == "PLAY" && previous != "PLAY")
        {
            player.Summoned.Add(entity.CardId);
        }
        else if (entity.IsMinion && previous == "PLAY" && value == "GRAVEYARD")
        {
            player.Died.Add(entity.CardId);
            // Deaths are resolved inside the block of the card that caused them ("if this kills a minion").
            if (game.OpenBlocks.FirstOrDefault(b => b.Type == "PLAY") is { Source: { Controller: > 0 } played })
            {
                var kills = For(played.Controller).KillsByPlay;
                kills[played.Id] = kills.GetValueOrDefault(played.Id) + 1;
            }
        }
        else if (entity.CardType is "WEAPON" or "LOCATION" && previous == "PLAY" && value == "GRAVEYARD")
        {
            player.Destroyed.Add(entity.CardId);
        }
        else if (previous == "HAND" && value == "GRAVEYARD" && game.OpenBlocks.All(b => b.Type != "PLAY" || b.Source?.Id != entity.Id))
        {
            player.Discarded.Add(entity.CardId);
        }
    }

    /// <summary>The player's own turn number (each player's turns counted separately).</summary>
    internal static int PlayerTurn(GameState game) => game.Turn <= 0 ? 0 : (game.Turn + 1) / 2;

    /// <summary>Whether it is this player's turn (CURRENT_PLAYER on the player entity).</summary>
    internal static bool IsCurrentPlayer(GameState game, int playerId) =>
        game.Players.TryGetValue(playerId, out var slot) && game.Entities.GetValueOrDefault(slot.EntityId)?.GetInt("CURRENT_PLAYER") == 1;

    private static int Parse(string value) => int.TryParse(value, CultureInfo.InvariantCulture, out var n) ? n : 0;

    private static int Delta(string previous, string value) => Parse(value) - Parse(previous);
}
