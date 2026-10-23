using Hearthpulse.Core.Power;
using Hearthpulse.Core.Replay;

namespace Hearthpulse.Core.Tracking;

/// <summary>
/// The Battlegrounds match replay: per round the board that fought, the combat opponent's board as it was shown in the
/// fight, and every card Bob offered; plus the lobby. Only what the player saw (no hands, no secrets of others).
/// </summary>
public sealed partial class BattlegroundsTracker
{
    private const int MaxShopPerRound = 60;
    private const int MaxRounds = 60;

    private readonly SortedDictionary<int, BattlegroundsRound> _rounds = new();
    private readonly Dictionary<int, List<string>> _shopOffers = new();
    private readonly HashSet<int> _shopEntities = new();

    private void ResetRounds()
    {
        _rounds.Clear();
        _shopOffers.Clear();
        _shopEntities.Clear();
    }

    private void NoteRound(GameState game, IReadOnlyList<BgMinion> theirs)
    {
        var turn = game.BattlegroundsTurn;
        if (_rounds.Count >= MaxRounds && !_rounds.ContainsKey(turn))
        {
            return;
        }

        _rounds[turn] = new BattlegroundsRound
        {
            Turn = turn,
            Tier = game.LocalPlayerEntity?.GetInt(GameTag.PlayerTechLevel) is > 0 and var tier ? tier : _tiers.LastOrDefault()?.Tier ?? 1,
            Health = Durability(game.LocalHero),
            Board = _lastOwnBoard.Select(ToReplayMinion).ToList(),
            OpponentHero = _combatOpponent > 0 ? LeaderboardHero(game, _combatOpponent)?.CardId : null,
            OpponentBoard = theirs.Select(ToReplayMinion).ToList(),
        };
    }

    private void NoteShopOffer(GameState game, Entity entity)
    {
        if (entity.CardId.Length == 0 || !_shopEntities.Add(entity.Id))
        {
            return;
        }

        var turn = game.BattlegroundsTurn;
        if (!_shopOffers.TryGetValue(turn, out var offers))
        {
            if (_shopOffers.Count >= MaxRounds)
            {
                return;
            }

            _shopOffers[turn] = offers = new List<string>();
        }

        if (offers.Count < MaxShopPerRound)
        {
            offers.Add(entity.CardId);
        }
    }

    /// <summary>The game's rounds and lobby for its replay file.</summary>
    public BattlegroundsReplayData ReplayData(GameState game)
    {
        var turns = _rounds.Keys.Concat(_shopOffers.Keys).Distinct().Order();
        var rounds = turns.Select(turn =>
        {
            var round = _rounds.GetValueOrDefault(turn) ?? new BattlegroundsRound { Turn = turn, Tier = TierAt(turn) };
            var combat = _combats.LastOrDefault(c => c.Turn == turn);
            return round with
            {
                Shop = _shopOffers.GetValueOrDefault(turn) ?? [],
                Result = combat?.Result,
                DamageTaken = combat?.DamageTaken ?? 0,
                DamageDealt = combat?.DamageDealt ?? 0,
                OpponentHero = round.OpponentHero ?? (combat?.OpponentHeroCardId is { Length: > 0 } hero ? hero : null),
            };
        }).ToList();

        var local = game.LocalHero;
        var lobby = LeaderboardHeroes(game)
            .Select(h => new BattlegroundsLobbyHero(h.CardId, h.GetInt(GameTag.PlayerLeaderboardPlace), h == local))
            .OrderBy(h => h.Place == 0 ? 99 : h.Place)
            .ToList();
        return new BattlegroundsReplayData { Rounds = rounds, Lobby = lobby };
    }

    private int TierAt(int turn) => _tiers.LastOrDefault(t => t.Turn <= turn)?.Tier ?? 1;

    private static ReplayMinion ToReplayMinion(BgMinion minion) => new(minion.CardId, minion.Attack, minion.Health, minion.Golden);
}
