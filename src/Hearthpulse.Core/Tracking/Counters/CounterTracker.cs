using Hearthpulse.Core.Cards;
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

    public int Cost => Entity.GetInt("TAG_LAST_KNOWN_COST_IN_HAND") is > 0 and var known ? known : Entity.GetInt(GameTag.Cost);

    public int SpellSchool => Entity.GetInt("SPELL_SCHOOL");

    public int Overload => Entity.GetInt("OVERLOAD");

    public bool FromDeck => Entity.IsOriginalDeckCard;
}

/// <summary>
/// Records what each player did this game that the game itself does not count in a tag: cards played (with cost,
/// spell school, origin), minions summoned and died, cards discarded, attacks and draws. Counters read these logs
/// together with the player tags the client keeps (CORPSES, CTHUN_ABILITY*_USED, HERALD_COLOSSAL_AMOUNT...).
/// </summary>
public sealed class CounterTracker : IPowerListener
{
    private readonly Dictionary<int, PlayerLog> _logs = [];

    /// <summary>What one player did this game.</summary>
    public sealed class PlayerLog
    {
        public List<PlayedCardInfo> Played { get; } = [];

        /// <summary>Minions that entered play (played or summoned), by card id.</summary>
        public List<string> Summoned { get; } = [];

        /// <summary>Friendly minions that died, by card id.</summary>
        public List<string> Died { get; } = [];

        public List<string> Discarded { get; } = [];

        public int Attacks { get; internal set; }

        public int Drawn { get; internal set; }
    }

    public PlayerLog For(int playerId) => _logs.TryGetValue(playerId, out var log) ? log : _logs[playerId] = new PlayerLog();

    public void OnGameStarted(GameState game) => _logs.Clear();

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
            if (log.Played.All(p => p.Entity.Id != source.Id))
            {
                log.Played.Add(new PlayedCardInfo(source, PlayerTurn(game)));
            }
        }
        else if (blockType == "ATTACK")
        {
            For(source.Controller).Attacks++;
        }
    }

    public void OnTagChanged(GameState game, Entity entity, string tag, string previous, string value)
    {
        if (tag == "NUM_CARDS_DRAWN_THIS_TURN")
        {
            // The per-turn count resets to 0 each turn; only increases are draws.
            var drawn = (int.TryParse(value, out var now) ? now : 0) - (int.TryParse(previous, out var before) ? before : 0);
            if (drawn > 0 && game.Players.Values.FirstOrDefault(p => p.EntityId == entity.Id) is { } slot)
            {
                For(slot.PlayerId).Drawn += drawn;
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
        }
        else if (previous == "HAND" && value == "GRAVEYARD" && game.OpenBlocks.All(b => b.Type != "PLAY" || b.Source?.Id != entity.Id))
        {
            player.Discarded.Add(entity.CardId);
        }
    }

    /// <summary>The player's own turn number (each player's turns counted separately).</summary>
    internal static int PlayerTurn(GameState game) => game.Turn <= 0 ? 0 : (game.Turn + 1) / 2;
}
