using System.Globalization;
using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Battlegrounds.Effects;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Memory;
using Hearthpulse.Core.Power;

namespace Hearthpulse.Core.Tracking;

/// <summary>Combats: when they start and end, the boards captured for the simulator, its odds and the damage taken.</summary>
public sealed partial class BattlegroundsTracker
{
    /// <summary>
    /// Combat the server has finished but whose end transition never reached the log: if no combat block has been
    /// seen for the timeout, treat the fight as over instead of staying on Combat for the rest of the game.
    /// </summary>
    private void EndStaleCombat(GameState game)
    {
        if (_inCombat && _combatActivityAt > 0 && Environment.TickCount64 - _combatActivityAt > CombatLatchTimeoutMs)
        {
            game.CombatOnScreen = false;
            EndCombat(game);
        }
    }

    /// <summary>True (and releases the latch) when the client's combat replay signal is older than the timeout.</summary>
    internal static bool CombatLatchStale(GameState game) => CombatLatchStale(game, Environment.TickCount64);

    /// <summary>Same check with the clock passed in, so tests do not depend on how long the machine has been up.</summary>
    internal static bool CombatLatchStale(GameState game, long nowMs)
    {
        if (game.CombatOnScreenSince <= 0 || nowMs - game.CombatOnScreenSince <= CombatLatchTimeoutMs)
        {
            return false;
        }

        game.CombatOnScreen = false;
        game.CombatOnScreenSince = 0;
        return true;
    }

    /// <summary>Final board for history, captured at the last combat.</summary>
    public IReadOnlyList<BgMinion> LastOwnBoard => _lastOwnBoard;

    /// <summary>This game's combats so far, oldest first (for history).</summary>
    public IReadOnlyList<CombatRecord> Combats => _combats;

    public int FinalTechLevel => _tiers.Count > 0 ? _tiers[^1].Tier : 1;

    private void BeginCombat(GameState game)
    {
        _inCombat = true;
        _combatTurn = game.BattlegroundsTurn;
        _combatActivityAt = Environment.TickCount64;
        _boardCaptured = false;
        _boardsSnapshotted = false;
        _removedBy.Clear();
        _combatOdds = null;
        Interlocked.Increment(ref _simulationGeneration);
        _pendingDamage = -1;
        _localAlive = 0;
        _opponentAlive = 0;
        _heroHit = null;
        _heroBlow = null;
        _combatOpponent = game.OpponentPlayerEntity?.GetInt(GameTag.CurrentCombatPlayerId) ?? 0;
        _localHealthAtCombat = Durability(game.LocalHero);
        _opponentHealthAtCombat = Durability(LeaderboardHero(game, _combatOpponent));
    }

    /// <summary>
    /// A fight decided without a hero attack in view (a log joined late): its damage is read from the lobby's health,
    /// which moves after the combat burst (armor first), from the health+armor snapshot taken when it started until the
    /// next combat begins. Only the loser's side counts and the result never changes: the lobby's health also moves for
    /// reasons of its own (a hero hurting itself in its tavern, a heal), and a tie stays a tie.
    /// </summary>
    private void ResolvePendingDamage(GameState game)
    {
        if (_pendingDamage < 0 || _pendingDamage >= _combats.Count)
        {
            return;
        }

        var combat = _combats[_pendingDamage];
        // Keep the largest damage seen, so a later heal (or armor gain) cannot erase it.
        var taken = combat.Result != "Loss" ? 0 : Math.Max(combat.DamageTaken, Math.Max(0, _localHealthAtCombat - Durability(game.LocalHero)));
        var dealt = combat.Result != "Win" ? 0
            : Math.Max(combat.DamageDealt, Math.Max(0, _opponentHealthAtCombat - Durability(LeaderboardHero(game, combat.OpponentPlayerId))));
        _combats[_pendingDamage] = combat with { DamageTaken = taken, DamageDealt = dealt };
    }

    /// <summary>The lobby's combat damage cap, as the game entity announces it (<see cref="DamageRules.Cap"/>).</summary>
    internal static int DamageCap(GameState game)
    {
        var entity = game.GameEntity;
        if (entity is null)
        {
            return 0;
        }

        int? enabled = entity.Tags.ContainsKey(GameTag.BaconCombatDamageCapEnabled) ? entity.GetInt(GameTag.BaconCombatDamageCapEnabled) : null;
        return DamageRules.Cap(entity.GetInt(GameTag.BaconCombatDamageCap), enabled);
    }

    private static int Durability(Entity? hero) => hero is null ? 0 : hero.Health + hero.GetInt(GameTag.Armor);

    private void CaptureBoards(GameState game, Entity? firstAttacker)
    {
        _boardCaptured = true;
        if (!_boardsSnapshotted)
        {
            // After start-of-combat attacks the history keeps the boards from before them (as they were shown).
            // Taken first: it also reads the combat opponent, whose health the simulation needs.
            SnapshotBoards(game);
        }

        // Start-of-combat effects have resolved by the first regular attack, so these boards are what actually fights.
        var enchantments = Enchantments(game);
        // Health for lethal comes from the leaderboard heroes (the fighting heroes are copies), as it was when the fight
        // started; an opponent tier the combat copy lacks comes from there too.
        var opponentHero = LeaderboardHero(game, _combatOpponent);
        var ours = BuildSide(game, game.LocalPlayerId, isOpponent: false, enchantments) with { Health = _localHealthAtCombat, Turn = game.BattlegroundsTurn };
        var theirs = BuildSide(game, game.OpponentPlayerId, isOpponent: true, enchantments) with { Health = _opponentHealthAtCombat, Turn = game.BattlegroundsTurn };
        if (theirs.TavernTier == 0 && opponentHero?.GetInt(GameTag.PlayerTechLevel) is > 0 and var opponentTier)
        {
            theirs = theirs with { TavernTier = opponentTier };
        }

        var attacker = firstAttacker is { IsMinion: true } a ? a : null;
        bool? oursFirst = attacker is null ? null : attacker.Controller == game.LocalPlayerId;
        var options = SimOptions with
        {
            Effects = Effects,
            FirstAttackerEntityId = attacker?.Id ?? 0,
            DamageCap = DamageCap(game),
            CountDamage = !game.IsDuos,
        };
        LastCapture = new CombatCapture(ours, theirs, oursFirst, options.FirstAttackerEntityId);
        StartSimulation(ours, theirs, oursFirst, options);
    }

    /// <summary>
    /// Takes the accurate odds from the background simulation. Runs on the tracking thread, so the combat list is
    /// only ever written here.
    /// </summary>
    private void ApplyRefinedOdds()
    {
        var refined = _refinedOdds;
        if (refined is null || _refinedGeneration != _simulationGeneration)
        {
            return;
        }

        _refinedOdds = null;
        _combatOdds = refined;
        if (_simulatedCombatIndex >= 0 && _simulatedCombatIndex < _combats.Count)
        {
            _combats[_simulatedCombatIndex] = _combats[_simulatedCombatIndex] with { Odds = refined };
        }
    }

    /// <summary>
    /// Estimates the fight. A quick estimate is calculated right away (the odds must be on screen while the fight
    /// runs) and the accurate one is simulated in the background: a full simulation of two big boards takes seconds
    /// on a slow PC, and doing that inline blocked the log reader, so the overlay stopped following the game.
    /// </summary>
    private void StartSimulation(SimSide ours, SimSide theirs, bool? oursFirst, SimOptions options)
    {
        var generation = Interlocked.Increment(ref _simulationGeneration);
        // A newer fight (or a replay of the game) makes the running simulation useless: stop it instead of letting
        // several of them fight over a slow CPU.
        var cancel = new CancellationTokenSource();
        Interlocked.Exchange(ref _simulationCancel, cancel)?.Cancel();
        _refinedOdds = null;
        _refinedGeneration = -1;
        _simulatedCombatIndex = _combats.Count;

        try
        {
            _combatOdds = CombatSimulator.Run(ours, theirs, oursFirst, options with
            {
                Runs = 150,
                MinRuns = 0,
                TimeBudget = TimeSpan.FromMilliseconds(40),
            });
        }
        catch (Exception ex)
        {
            Diag.Error("Combat simulation failed", ex);
        }

        _ = Task.Run(() =>
        {
            try
            {
                var odds = CombatSimulator.Run(ours, theirs, oursFirst, options with { Cancel = cancel.Token });
                if (Volatile.Read(ref _simulationGeneration) == generation)
                {
                    _refinedOdds = odds;
                    _refinedGeneration = generation;
                }
            }
            catch (Exception ex)
            {
                Diag.Error("Combat simulation failed", ex);
            }
        });
    }

    /// <summary>Records both boards for history (the opponent's last seen board, our final board).</summary>
    private void SnapshotBoards(GameState game)
    {
        _boardsSnapshotted = true;
        // The combat opponent id is tagged just after the board switches to combat.
        _combatOpponent = game.OpponentPlayerEntity?.GetInt(GameTag.CurrentCombatPlayerId) is > 0 and var id ? id : _combatOpponent;
        _opponentHealthAtCombat = Durability(LeaderboardHero(game, _combatOpponent));

        var theirs = Minions(game, game.OpponentPlayerId).Select(ToMinion).ToList();
        _lastOwnBoard = Minions(game, game.LocalPlayerId).Select(ToMinion).ToList();
        _localAlive = _lastOwnBoard.Count;
        _opponentAlive = theirs.Count;
        if (_combatOpponent > 0)
        {
            _boards[_combatOpponent] = new SeenBoard(game.BattlegroundsTurn, theirs);
        }

        NoteRound(game, theirs);
    }

    private void EndCombat(GameState game)
    {
        _inCombat = false;
        if (_combatOpponent == 0)
        {
            return;
        }

        // The hero attack that closes the fight decides it and says the damage; without one, the boards.
        FinishHeroHit();
        var result = _heroBlow is { } blow ? (blow.Ours ? "Win" : "Loss")
            : _localAlive == 0 && _opponentAlive > 0 ? "Loss"
            : _opponentAlive == 0 && _localAlive > 0 ? "Win"
            : "Tie";
        var damage = _heroBlow?.Damage ?? 0;
        var hero = LeaderboardHero(game, _combatOpponent)?.CardId ?? string.Empty;
        _combats.Add(new CombatRecord(game.BattlegroundsTurn, _combatOpponent, hero, result, result == "Loss" ? damage : 0, result == "Win" ? damage : 0,
            _combatOdds));
        _pendingDamage = result != "Tie" && damage == 0 ? _combats.Count - 1 : -1;
        _combatOpponent = 0;
        ResolvePendingDamage(game);
    }
}
