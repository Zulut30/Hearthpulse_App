using Hearthlens.Core.Battlegrounds;
using Hearthlens.Core.Battlegrounds.Effects;
using Hearthlens.Core.Cards;
using Hearthlens.Core.Memory;
using Hearthlens.Core.Power;

namespace Hearthlens.Core.Tracking;

/// <summary>Battlegrounds state: tavern tier, lobby, opponents' last seen boards, combats.</summary>
public sealed class BattlegroundsTracker : IPowerListener
{
    private const string PlaceholderHero = "TB_BaconShop_HERO_PH";

    /// <summary>How long a combat latch may stay set without new combat evidence before it is treated as stale.</summary>
    private const int CombatLatchTimeoutMs = 120_000;

    private static readonly (string Tag, string Label)[] KeywordTags =
    [
        (GameTag.Taunt, "TAUNT"),
        (GameTag.DivineShield, "DIVINE_SHIELD"),
        (GameTag.Poisonous, "POISONOUS"),
        (GameTag.Venomous, "VENOMOUS"),
        (GameTag.Reborn, "REBORN"),
        (GameTag.Windfury, "WINDFURY"),
        (GameTag.Stealth, "STEALTH"),
    ];

    private readonly Dictionary<int, SeenBoard> _boards = new();

    // Minions removed by another minion's trigger after combat started and before the boards were captured
    // (e.g. Stitched Salvager's start-of-combat destroy), as they were when removed, by the trigger's source.
    private readonly Dictionary<int, List<SimMinion>> _removedBy = new();
    private readonly List<CombatRecord> _combats = new();
    private readonly List<TierStep> _tiers = new();
    private readonly HashSet<string> _shopRaces = new(StringComparer.Ordinal);

    private bool _inCombat;

    /// <summary>Round the current combat started in, to notice a missed end-of-combat transition.</summary>
    private int _combatTurn;

    /// <summary>Environment.TickCount64 of the last combat block activity, for stale-latch recovery.</summary>
    private long _combatActivityAt;
    private bool _boardCaptured;
    private bool _boardsSnapshotted;
    private int _combatOpponent;
    private int _localHealthAtCombat;
    private int _opponentHealthAtCombat;
    private int _pendingDamage = -1;
    private int _localAlive;
    private int _opponentAlive;
    private IReadOnlyList<BgMinion> _lastOwnBoard = Array.Empty<BgMinion>();
    private IReadOnlyList<Entity> _trinketOffer = Array.Empty<Entity>();
    private IReadOnlyList<Entity> _discoverOffer = Array.Empty<Entity>();
    private Entity? _discoverSource;
    private volatile CombatOdds? _combatOdds;

    /// <summary>Number of the newest fight simulation; older results are dropped when they arrive late.</summary>
    private int _simulationGeneration;
    private CancellationTokenSource? _simulationCancel;

    /// <summary>The accurate odds, once the background simulation has them.</summary>
    private volatile CombatOdds? _refinedOdds;

    /// <summary>Which simulation the refined odds belong to.</summary>
    private int _refinedGeneration = -1;

    /// <summary>Index of the combat the running simulation belongs to.</summary>
    private int _simulatedCombatIndex = -1;

    // Player-level cards whose effects could take part in a fight (only registered effects are used).
    private static readonly string[] ExtraCardTypes = ["HERO_POWER", "BATTLEGROUND_TRINKET", "BATTLEGROUND_QUEST_REWARD"];

    // Game entity tags naming lobby-wide cards that could have combat effects.
    private static readonly string[] GlobalCardTags = [GameTag.BaconAnomaly, GameTag.BaconDeity];

    // Effect markers an enchantment can put on a minion.
    private static readonly string[] GrantableTags = ["DEATHRATTLE", "BACON_RALLY", "AVENGE"];

    public CardDb Cards { get; set; } = CardDb.Empty;

    /// <summary>
    /// Card effects the combat simulator applies. Minions with combat-relevant text and no registered effect make the
    /// odds an estimate.
    /// </summary>
    public EffectRegistry Effects { get; set; } = EffectRegistry.Default;

    /// <summary>
    /// Options for the combat simulation, which runs on the log thread at the first attack: up to 4000 fights, stopping
    /// after 200 ms (at least 500 fights). Keyword-only boards need ~20–60 ms; a full 7v7 board full of effects
    /// ~100–160 ms once the code is warm, more on the first fight after start-up.
    /// </summary>
    public SimOptions SimOptions { get; set; } = new()
    {
        TimeBudget = TimeSpan.FromMilliseconds(200),

        // A live simulation must not hog the CPU while the game is running: the fight is only an estimate, so a
        // few hundred runs are enough and a slow PC stops early instead of stalling the game.
        MinRuns = 200,
        Runs = 2000,
    };

    public Func<string, HeroStats?>? HeroStatsProvider { get; set; }

    /// <summary>Tribes read from client memory (authoritative when present).</summary>
    public IReadOnlyList<string>? MemoryRaces { get; set; }

    /// <summary>
    /// Live gold costs of the Battlegrounds buttons, including discounts (null when client memory is unavailable).
    /// </summary>
    public BattlegroundsButtonCosts? LiveButtonCosts { get; set; }

    public bool LocalFinished { get; private set; }

    public void OnGameStarted(GameState game)
    {
        _boards.Clear();
        _combats.Clear();
        _tiers.Clear();
        _shopRaces.Clear();
        _inCombat = false;
        _combatTurn = 0;
        _combatActivityAt = 0;
        _boardCaptured = false;
        _lastOwnBoard = Array.Empty<BgMinion>();
        _trinketOffer = Array.Empty<Entity>();
        _discoverOffer = Array.Empty<Entity>();
        _discoverSource = null;
        _combatOdds = null;
        MemoryRaces = null;
        LiveButtonCosts = null;
        LocalFinished = false;
        _pendingDamage = -1;
    }

    public void OnChoices(GameState game, string choiceType, Entity? source, IReadOnlyList<Entity> options)
    {
        // Lesser/Greater Trinket offers arrive as a GENERAL choice of trinket cards.
        if (options.Count > 0 && options.All(IsTrinket))
        {
            _trinketOffer = options.ToArray();
            _discoverOffer = Array.Empty<Entity>();
            _discoverSource = null;
            return;
        }

        // Every other "pick one of these cards" (Discover from hero powers, spells, minions, trinkets,
        // the triple reward or the Dark Gift button) offers freshly created cards set aside.
        if (choiceType == "GENERAL" && options.Count > 1 && options.All(e => e.Zone == "SETASIDE" && e.CardId.Length > 0))
        {
            _discoverOffer = options.ToArray();
            _discoverSource = source;
        }
    }

    public void OnChoiceMade(GameState game)
    {
        _discoverOffer = Array.Empty<Entity>();
        _trinketOffer = Array.Empty<Entity>();
        _discoverSource = null;
    }

    public void OnEntityCreated(GameState game, Entity entity) => NoteShopCard(game, entity);

    public void OnEntityRevealed(GameState game, Entity entity) => NoteShopCard(game, entity);

    public void OnTagChanged(GameState game, Entity entity, string tag, string previous, string value)
    {
        if (!game.IsBattlegrounds)
        {
            return;
        }

        switch (tag)
        {
            case GameTag.BoardVisualState when entity.Id == game.GameEntityId:
                if (value == "2")
                {
                    if (!game.CombatOnScreen)
                    {
                        game.CombatOnScreenSince = Environment.TickCount64;
                    }

                    game.CombatOnScreen = true;
                    BeginCombat(game);
                    break;
                }

                // The server writes the whole fight, ending with this line, about a second after it starts; the client
                // then plays it for another 30 seconds or so. The client's replay (PowerParser) says when it is over
                // on screen, so the odds stay up while the fight plays. Without a replay this line is all there is.
                if (value == "1" && !game.CombatReplaySeen)
                {
                    game.CombatOnScreen = false;
                }

                if (_inCombat)
                {
                    EndCombat(game);
                }

                break;

            case GameTag.PlayerTechLevel when IsLocal(game, entity):
                var tier = entity.GetInt(GameTag.PlayerTechLevel);
                if (tier > 0 && (_tiers.Count == 0 || _tiers[^1].Tier != tier))
                {
                    _tiers.Add(new TierStep(game.BattlegroundsTurn, tier));
                }

                break;

            case GameTag.Damage or GameTag.Armor or GameTag.Health when entity.IsHero && !_inCombat:
                ResolvePendingDamage(game);
                break;

            case GameTag.Zone when _inCombat && !_boardCaptured && entity.IsMinion && previous == "PLAY" && value != "PLAY":
                RememberRemoval(game, entity);
                break;

            case GameTag.PlayState when entity.Id == game.LocalPlayer?.EntityId:
                if (value is "WON" or "LOST" or "TIED" or "CONCEDED")
                {
                    LocalFinished = true;
                }

                break;
        }
    }

    public void OnBlockStart(GameState game, string blockType, Entity? source)
    {
        if (!_inCombat)
        {
            return;
        }

        _combatActivityAt = Environment.TickCount64;
        if (_boardCaptured || blockType != "ATTACK")
        {
            return;
        }

        if (IsStartOfCombatAttack(game))
        {
            // Start-of-combat attacks (Illidan's Wingmen, Heroic Broodmother, Righteous Charge...) come before the other
            // start-of-combat effects have all resolved, and their attacker is not the fight's first attacker. Keep the
            // boards for history in case the fight ends before a regular attack, but simulate from the first regular one.
            if (!_boardsSnapshotted)
            {
                SnapshotBoards(game);
            }

            return;
        }

        CaptureBoards(game, source);
    }

    /// <summary>
    /// An ATTACK block inside a TRIGGER block of a hero power, minion or quest reward is a start-of-combat attack;
    /// regular attacks are not nested in such a block (blocks of the game or a player entity do not count).
    /// Inferred from the log's block structure; not yet checked against a real Illidan/Broodmother log.
    /// </summary>
    private static bool IsStartOfCombatAttack(GameState game) =>
        game.OpenBlocks.Any(b => b.Type == "TRIGGER" && b.Source is { } s && s.Id != game.GameEntityId
                                 && s.CardType is "HERO_POWER" or "MINION" or "BATTLEGROUND_QUEST_REWARD");

    public void OnBlockEnd(GameState game)
    {
        if (!_inCombat)
        {
            return;
        }

        _combatActivityAt = Environment.TickCount64;
        if (_boardCaptured || _boardsSnapshotted)
        {
            _localAlive = Minions(game, game.LocalPlayerId).Count();
            _opponentAlive = Minions(game, game.OpponentPlayerId).Count();
        }
    }

    public BattlegroundsView BuildView(GameState game)
    {
        ApplyRefinedOdds();
        EndStaleCombat(game);
        // Combat as the player sees it: from the server resolving it until the client finishes playing it.
        // If the client never sent the closing BOARD_VISUAL_STATE line (build-dependent), the latch is released
        // by the next round's shop or by the timeout so the overlay cannot stay stuck on Combat in the tavern.
        var combat = _inCombat || (game.CombatOnScreen && !CombatLatchStale(game));
        var localHero = game.LocalHero;
        var heroChosen = localHero is not null && localHero.CardId != PlaceholderHero && !localHero.InHand;
        var options = heroChosen
            ? new List<BgHeroOption>()
            : game.Controlled(game.LocalPlayerId, "HAND")
                .Where(e => e.IsHero && e.CardId.Length > 0 && e.CardId != PlaceholderHero)
                .OrderBy(e => e.ZonePosition)
                .Select(e => new BgHeroOption(
                    e.CardId,
                    Cards.NameOr(e.CardId, e.Name),
                    e.GetInt(GameTag.Armor) is > 0 and var a ? a : Cards.Get(e.CardId)?.Armor ?? 0,
                    HeroStatsProvider?.Invoke(Cards.BaseHeroId(e.CardId))))
                .ToList();

        var nextOpponent = localHero?.GetInt(GameTag.NextOpponentPlayerId) ?? 0;
        if (nextOpponent == 0)
        {
            nextOpponent = game.LocalPlayerEntity?.GetInt(GameTag.NextOpponentPlayerId) ?? 0;
        }

        var techLevel = game.LocalPlayerEntity?.GetInt(GameTag.PlayerTechLevel) ?? 0;
        if (techLevel == 0)
        {
            techLevel = localHero?.GetInt(GameTag.PlayerTechLevel) ?? 0;
        }

        var localBgId = localHero?.GetInt(GameTag.PlayerId) ?? 0;
        var opponents = LeaderboardHeroes(game)
            .Where(h => h.GetInt(GameTag.PlayerId) != localBgId)
            .Select(h =>
            {
                var playerId = h.GetInt(GameTag.PlayerId);
                _boards.TryGetValue(playerId, out var seen);
                var last = _combats.LastOrDefault(c => c.OpponentPlayerId == playerId);
                return new BgOpponent(
                    playerId,
                    h.CardId,
                    Cards.NameOr(h.CardId, h.Name),
                    h.Health,
                    h.GetInt(GameTag.Armor),
                    h.GetInt(GameTag.PlayerTechLevel),
                    h.GetInt(GameTag.PlayerLeaderboardPlace),
                    h.Health <= 0,
                    h.GetInt(GameTag.PlayerTriples),
                    seen?.Minions ?? Array.Empty<BgMinion>(),
                    seen?.Turn ?? 0,
                    last?.Result,
                    last is null ? 0 : last.Result == "Loss" ? last.DamageTaken : last.DamageDealt,
                    playerId == nextOpponent);
            })
            .OrderBy(o => o.Place == 0 ? 99 : o.Place)
            .ToList();

        var player = game.LocalPlayerEntity;
        var darkGift = game.Controlled(game.LocalPlayerId, "PLAY").FirstOrDefault(e => e.CardId.Contains("DarkGift", StringComparison.Ordinal));
        var anomalyDbf = game.GameEntity?.GetInt(GameTag.BaconAnomaly) ?? 0;
        var maxTier = game.LocalPlayerEntity?.GetInt(GameTag.BaconMaxTechLevel) ?? 0;

        return new BattlegroundsView(
            game.BattlegroundsTurn,
            Math.Max(1, techLevel),
            maxTier > 0 ? maxTier : 6,
            heroChosen ? localHero!.CardId : null,
            localHero?.Health ?? 0,
            localHero?.GetInt(GameTag.Armor) ?? 0,
            localHero?.GetInt(GameTag.PlayerLeaderboardPlace) ?? 0,
            game.IsDuos,
            combat,
            !heroChosen && options.Count > 0,
            options,
            MemoryRaces ?? _shopRaces.OrderBy(r => r, StringComparer.Ordinal).ToList(),
            MemoryRaces is not null,
            opponents,
            _combats.ToList(),
            _tiers.ToList(),
            _inCombat ? _lastOwnBoard : Minions(game, game.LocalPlayerId).Select(ToMinion).ToList(),
            anomalyDbf > 0 ? Cards.Get(anomalyDbf)?.Id : null,
            game.LocalPlayerEntity?.GetInt(GameTag.PlayerTriples) ?? 0,
            // Outside combat the opponent slot holds Bob's current offer (minions and tavern spells).
            combat || !heroChosen ? Array.Empty<BgMinion>() : ShopCards(game).Select(ToMinion).ToList(),
            game.Controlled(game.LocalPlayerId, "HAND").Where(e => e.IsMinion && e.CardId.Length > 0).Select(e => e.CardId).ToList())
        {
            // A choice offered while the fight is still on screen waits until the player can see it.
            TrinketOptions = heroChosen && !game.CombatOnScreen ? PendingTrinkets() : Array.Empty<BgTrinketOption>(),
            DiscoverOptions = heroChosen && !game.CombatOnScreen ? PendingDiscover(game) : Array.Empty<BgDiscoverOption>(),
            DiscoverSource = _discoverOffer.Count == 0 ? null : DiscoverSourceLabel(_discoverSource),
            CombatOdds = combat ? _combatOdds : null,
            Trinkets = game.Controlled(game.LocalPlayerId, "PLAY").Where(IsTrinket).Select(e => e.CardId).Distinct().ToList(),
            DarkGiftReady = darkGift is not null && darkGift.GetInt("EXHAUSTED") == 0 && !combat,
            DarkGiftCost = darkGift?.GetInt(GameTag.Cost) ?? 0,
            Gold = player is null ? 0 : Math.Max(0, player.GetInt("RESOURCES") + player.GetInt("TEMP_RESOURCES") - player.GetInt("RESOURCES_USED")),
            TavernUpgradeCost = LiveButtonCosts?.Upgrade,
            ButtonCosts = LiveButtonCosts,
        };
    }

    private static bool IsTrinket(Entity e) =>
        e.CardType == "BATTLEGROUND_TRINKET" || e.CardId.Contains("_MagicItem_", StringComparison.Ordinal);

    private IReadOnlyList<BgTrinketOption> PendingTrinkets()
    {
        // The offer closes once one of the options is taken (moves to play) or combat starts.
        if (_trinketOffer.Count == 0 || _inCombat || _trinketOffer.Any(e => e.Zone is "PLAY" or "GRAVEYARD" or "REMOVEDFROMGAME"))
        {
            _trinketOffer = Array.Empty<Entity>();
            return Array.Empty<BgTrinketOption>();
        }

        return _trinketOffer
            .Select(e => new BgTrinketOption(e.CardId, Cards.NameOr(e.CardId, e.Name)))
            .ToList();
    }

    private IReadOnlyList<BgDiscoverOption> PendingDiscover(GameState game)
    {
        // The choice closes when the player picks (or the pick leaves set-aside) or combat starts.
        if (_discoverOffer.Count == 0 || _inCombat || _discoverOffer.Any(e => e.Zone != "SETASIDE"))
        {
            _discoverOffer = Array.Empty<Entity>();
            return Array.Empty<BgDiscoverOption>();
        }

        return _discoverOffer
            .Select(e =>
            {
                var gift = game.Entities.GetValueOrDefault(e.GetInt("DARK_GIFT_ENTITY"));
                var giftName = gift is null || gift.CardId.Length == 0 ? null : Cards.NameOr(gift.CardId, gift.Name);
                var card = Cards.Get(e.CardId);
                var tier = e.GetInt(GameTag.TechLevel) is > 0 and var t ? t : card?.TechLevel ?? 0;
                var type = card?.Type is { Length: > 0 } cardType ? cardType : e.CardType;
                return new BgDiscoverOption(e.CardId, Cards.NameOr(e.CardId, e.Name), tier, giftName, type);
            })
            .ToList();
    }

    /// <summary>"Kind · name" of what offers the open choice, e.g. "Hero power · Murloc King".</summary>
    private string? DiscoverSourceLabel(Entity? source)
    {
        if (source is null || source.CardId.Length == 0)
        {
            return null;
        }

        var card = Cards.Get(source.CardId);
        var name = card?.Name is { Length: > 0 } n ? n : source.Name;
        var kind = source.CardId switch
        {
            var id when id.StartsWith("TB_BaconShop_Triples", StringComparison.Ordinal) => "Triple reward",
            var id when id.StartsWith("BG36_MidGameEffect", StringComparison.Ordinal) => "Dark Gift",
            _ => (card?.Type ?? source.CardType) switch
            {
                "HERO_POWER" => "Hero power",
                "MINION" => "Minion",
                "BATTLEGROUND_TRINKET" => "Trinket",
                "SPELL" or "BATTLEGROUND_SPELL" => "Spell",
                _ => "Discover",
            },
        };
        return kind is "Triple reward" or "Dark Gift" ? kind : $"{kind} · {name}";
    }

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
        _combatOpponent = game.OpponentPlayerEntity?.GetInt(GameTag.CurrentCombatPlayerId) ?? 0;
        _localHealthAtCombat = Durability(game.LocalHero);
        _opponentHealthAtCombat = Durability(LeaderboardHero(game, _combatOpponent));
    }

    /// <summary>
    /// Hero damage lands after the combat burst (armor first). Re-derive the last combat's damage
    /// from the health+armor snapshot taken when it started until the next combat begins.
    /// </summary>
    private void ResolvePendingDamage(GameState game)
    {
        if (_pendingDamage < 0 || _pendingDamage >= _combats.Count)
        {
            return;
        }

        var combat = _combats[_pendingDamage];
        // Damage and healing both move hero health after the fight: keep the largest damage seen, so a later
        // heal (or armor gain) cannot erase it or flip a decided win/loss into a tie.
        var taken = Math.Max(combat.DamageTaken, Math.Max(0, _localHealthAtCombat - Durability(game.LocalHero)));
        var dealt = Math.Max(combat.DamageDealt, Math.Max(0, _opponentHealthAtCombat - Durability(LeaderboardHero(game, combat.OpponentPlayerId))));
        var result = taken > 0 ? "Loss" : dealt > 0 ? "Win" : combat.Result;
        _combats[_pendingDamage] = combat with { DamageTaken = taken, DamageDealt = dealt, Result = result };
    }

    private static int Durability(Entity? hero) => hero is null ? 0 : hero.Health + hero.GetInt(GameTag.Armor);

    private void CaptureBoards(GameState game, Entity? firstAttacker)
    {
        _boardCaptured = true;
        // Start-of-combat effects have resolved by the first regular attack, so these boards are what actually fights.
        var enchantments = Enchantments(game);
        var ours = BuildSide(game, game.LocalPlayerId, isOpponent: false, enchantments);
        var theirs = BuildSide(game, game.OpponentPlayerId, isOpponent: true, enchantments);
        var attacker = firstAttacker is { IsMinion: true } a ? a : null;
        bool? oursFirst = attacker is null ? null : attacker.Controller == game.LocalPlayerId;
        var options = SimOptions with { Effects = Effects, FirstAttackerEntityId = attacker?.Id ?? 0 };
        LastCapture = new CombatCapture(ours, theirs, oursFirst, options.FirstAttackerEntityId);
        StartSimulation(ours, theirs, oursFirst, options);
        if (!_boardsSnapshotted)
        {
            // After start-of-combat attacks the history keeps the boards from before them (as they were shown).
            SnapshotBoards(game);
        }
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
    }

    private void EndCombat(GameState game)
    {
        _inCombat = false;
        if (_combatOpponent == 0)
        {
            return;
        }

        var result = _localAlive == 0 && _opponentAlive > 0 ? "Loss"
            : _opponentAlive == 0 && _localAlive > 0 ? "Win"
            : "Tie";
        var hero = LeaderboardHero(game, _combatOpponent)?.CardId ?? string.Empty;
        _combats.Add(new CombatRecord(game.BattlegroundsTurn, _combatOpponent, hero, result, 0, 0, _combatOdds));
        _pendingDamage = _combats.Count - 1;
        _combatOpponent = 0;
        ResolvePendingDamage(game);
    }

    private void NoteShopCard(GameState game, Entity entity)
    {
        if (!game.IsBattlegrounds || entity.Controller != game.OpponentPlayerId || !entity.InPlay
            || (!entity.IsMinion && entity.CardType is not ("SPELL" or "BATTLEGROUND_SPELL")))
        {
            return;
        }

        // A shop card dealt in a later round than the combat start means the fight is over even if the
        // client's BOARD_VISUAL_STATE transition was never logged.
        if (_inCombat && game.BattlegroundsTurn != _combatTurn)
        {
            game.CombatOnScreen = false;
            EndCombat(game);
        }

        // Mid-combat summons are opponent-controlled minions too: only a dealt shop counts for races.
        if (_inCombat || !entity.IsMinion)
        {
            return;
        }

        if (Cards.Get(entity.CardId) is { IsBattlegroundsPoolMinion: true } card)
        {
            foreach (var race in card.Races)
            {
                if (race != "ALL")
                {
                    _shopRaces.Add(race);
                }
            }
        }
    }

    private static bool IsLocal(GameState game, Entity entity) =>
        entity.Id == game.LocalPlayer?.EntityId || entity.Id == game.LocalHero?.Id;

    private static IEnumerable<Entity> Minions(GameState game, int controller) =>
        game.Entities.Values
            .Where(e => e.Controller == controller && e.InPlay && e.IsMinion)
            .OrderBy(e => e.ZonePosition);

    /// <summary>Bob's current offer: shop minions plus tavern spells, in row order.</summary>
    private static IEnumerable<Entity> ShopCards(GameState game) =>
        game.Entities.Values
            .Where(e => e.Controller == game.OpponentPlayerId && e.InPlay
                        && (e.IsMinion || e.CardType is "SPELL" or "BATTLEGROUND_SPELL"))
            .OrderBy(e => e.ZonePosition);

    /// <summary>Opponents' persistent leaderboard heroes (not combat copies or hero choices).</summary>
    private static IEnumerable<Entity> LeaderboardHeroes(GameState game)
    {
        var local = game.LocalHero;
        return game.Entities.Values
            .Where(e => e.IsHero && e.GetInt(GameTag.PlayerId) > 0 && e.GetInt(GameTag.PlayerLeaderboardPlace) > 0
                        && (e.Zone == "SETASIDE" || e == local) && e.CardId != PlaceholderHero)
            .GroupBy(e => e.GetInt(GameTag.PlayerId))
            // Combat copies share PLAYER_ID; the persistent leaderboard entity is the oldest one.
            .Select(g => g.MinBy(e => e.Id)!);
    }

    private static Entity? LeaderboardHero(GameState game, int playerId) =>
        LeaderboardHeroes(game).FirstOrDefault(h => h.GetInt(GameTag.PlayerId) == playerId);

    /// <summary>
    /// A minion left the board during start of combat inside a TRIGGER block of another minion: remember it for that
    /// minion (Stitched Salvager's deathrattle summons a copy of the minion it destroyed). Inferred from the log's block
    /// structure; not yet checked against a real log.
    /// </summary>
    private void RememberRemoval(GameState game, Entity removed)
    {
        for (var i = game.OpenBlocks.Count - 1; i >= 0; i--)
        {
            var block = game.OpenBlocks[i];
            if (block.Type != "TRIGGER" || block.Source is not { IsMinion: true } source || source.Id == removed.Id)
            {
                continue;
            }

            var noEnchantments = Array.Empty<string>().ToLookup(_ => 0);
            var copy = ToSim(removed, removed.Controller != game.LocalPlayerId, noEnchantments) with { HasUnsimulatedEffect = false };
            if (!_removedBy.TryGetValue(source.Id, out var list))
            {
                _removedBy[source.Id] = list = new List<SimMinion>();
            }

            list.Add(copy);
            return;
        }
    }

    /// <summary>Enchantment card ids by the minion they are attached to.</summary>
    private static ILookup<int, string> Enchantments(GameState game) =>
        game.Entities.Values
            .Where(e => e.CardType == "ENCHANTMENT" && e.InPlay && e.CardId.Length > 0 && e.GetInt(GameTag.Attached) > 0)
            .OrderBy(e => e.Id)
            .ToLookup(e => e.GetInt(GameTag.Attached), e => e.CardId);

    /// <summary>
    /// One side for the simulator: board minions plus what the player can see about that player. The opponent's hand
    /// and secrets are hidden information and are never read.
    /// </summary>
    private SimSide BuildSide(GameState game, int controller, bool isOpponent, ILookup<int, string> enchantments)
    {
        var minions = Minions(game, controller).Select(e => ToSim(e, isOpponent, enchantments)).ToList();
        var player = game.Players.TryGetValue(controller, out var slot) ? game.Entities.GetValueOrDefault(slot.EntityId) : null;
        var hero = game.HeroOf(player);
        var played = game.Controlled(controller, "PLAY").Where(e => e.CardId.Length > 0).OrderBy(e => e.Id).ToList();
        var heroPower = played.FirstOrDefault(e => e.CardType == "HERO_POWER");
        var extras = played.Where(e => ExtraCardTypes.Contains(e.CardType) || IsTrinket(e)).Select(e => e.CardId).ToList();
        // Lobby-wide cards: the anomaly and this season's Deity (tag name as hsdata names it; unverified in a real log).
        foreach (var tag in GlobalCardTags)
        {
            var dbf = game.GameEntity?.GetInt(tag) ?? 0;
            if (dbf > 0 && Cards.Get(dbf) is { } global)
            {
                extras.Add(global.Id);
            }
        }

        var hand = isOpponent
            ? Array.Empty<SimMinion>()
            : game.Controlled(controller, "HAND")
                .Where(e => e.IsMinion && e.CardId.Length > 0)
                .OrderBy(e => e.ZonePosition)
                .Select(e => ToSim(e, isOpponent: false, enchantments) with { HasUnsimulatedEffect = false })
                .ToArray();
        return new SimSide(minions)
        {
            HeroCardId = hero?.CardId ?? string.Empty,
            HeroPowerCardId = heroPower?.CardId ?? string.Empty,
            Extras = extras.Distinct().ToList(),
            // Only the local player's own hand: it is on their screen. The opponent's is hidden.
            Hand = hand,
            Tags = NumericTags(player),
            TavernTier = hero?.GetInt(GameTag.PlayerTechLevel) is > 0 and var tier ? tier : player?.GetInt(GameTag.PlayerTechLevel) ?? 0,
        };
    }

    private static IReadOnlyDictionary<string, int> NumericTags(Entity? entity)
    {
        var tags = new Dictionary<string, int>(StringComparer.Ordinal);
        if (entity is null)
        {
            return tags;
        }

        foreach (var (tag, value) in entity.Tags)
        {
            if (int.TryParse(value, out var number))
            {
                tags[tag] = number;
            }
        }

        return tags;
    }

    private SimMinion ToSim(Entity e, bool isOpponent, ILookup<int, string> enchantments)
    {
        // Golden minions have their own card id (golden stats and text); effects are keyed by the normal id + Golden.
        var card = Cards.Get(e.CardId);
        var (cardId, goldenId) = CombatRelevance.Normalize(e.CardId, Cards);
        var golden = goldenId || e.GetInt(GameTag.Premium) > 0;
        var mechanics = card?.Mechanics ?? Array.Empty<string>();
        var attached = enchantments[e.Id].ToList();
        // Known card: the generated table (or the text fallback for a newer card). Unknown card: its effect tags.
        var relevant = card is null && CombatRelevance.Find(e.CardId) is null
            ? CombatRelevance.TagsSuggestCombatEffect(e.Has)
            : CombatRelevance.IsCombatRelevant(e.CardId, card, Cards.Locale);
        // A deathrattle, Avenge or Rally the card does not print came from an enchantment (Dark Gift, Tamsin...).
        var granted = card is not null && CombatRelevance.HasGrantedEffect(e.Has, mechanics);
        var unsimulated = CombatRelevance.IsUnsimulated(cardId, golden, relevant, granted, attached, isOpponent, Effects);

        var keywords = KeywordsOf(e);
        // A reborn copy is a fresh copy of the card. Without card data, keep what the minion has minus one-shot keywords.
        var baseKeywords = card is not null
            ? Traits.KeywordsFromMechanics(mechanics)
            : keywords & (Keyword.Taunt | Keyword.Poisonous | Keyword.Windfury | Keyword.MegaWindfury);
        return new SimMinion(
            e.Attack,
            e.Health,
            e.Has(GameTag.Taunt),
            e.Has(GameTag.DivineShield),
            e.Has(GameTag.Poisonous),
            e.Has(GameTag.Venomous),
            e.Has(GameTag.Reborn),
            Traits.AttacksPerTurn(keywords),
            e.Has(GameTag.Stealth),
            card is not null ? card.Attack : e.Attack,
            unsimulated)
        {
            CardId = cardId,
            Golden = golden,
            EntityId = e.Id,
            Tribes = Traits.ParseTribes(card?.Races) | TribeTag(e.GetString(GameTag.CardRace)),
            MaxHealth = e.GetInt(GameTag.Health),
            BaseHealth = card?.Health ?? 0,
            BaseKeywords = baseKeywords,
            Mechanics = Traits.MechanicsFrom(mechanics) | Traits.MechanicsFrom(GrantableTags.Where(e.Has)),
            TechLevel = e.GetInt(GameTag.TechLevel) is > 0 and var tier ? tier : card?.TechLevel ?? 0,
            ScriptData1 = e.GetInt(GameTag.ScriptData1),
            ScriptData2 = e.GetInt(GameTag.ScriptData2),
            Enchantments = attached,
            Remembered = _removedBy.TryGetValue(e.Id, out var removed) ? removed.ToArray() : Array.Empty<SimMinion>(),
        };
    }

    private static Keyword KeywordsOf(Entity e) =>
        (e.Has(GameTag.Taunt) ? Keyword.Taunt : 0)
        | (e.Has(GameTag.DivineShield) ? Keyword.DivineShield : 0)
        | (e.Has(GameTag.Poisonous) ? Keyword.Poisonous : 0)
        | (e.Has(GameTag.Venomous) ? Keyword.Venomous : 0)
        | (e.Has(GameTag.Reborn) ? Keyword.Reborn : 0)
        | (e.Has(GameTag.Windfury) ? Keyword.Windfury : 0)
        | (e.Has(GameTag.MegaWindfury) ? Keyword.MegaWindfury : 0)
        | (e.Has(GameTag.Stealth) ? Keyword.Stealth : 0);

    /// <summary>CARDRACE as Power.log prints it: a name ("BEAST") or, for races the client does not name, a number.</summary>
    private static Tribe TribeTag(string value) =>
        int.TryParse(value, out var id) ? Traits.ParseTribe(Races.ById.GetValueOrDefault(id)) : Traits.ParseTribe(value);

    private BgMinion ToMinion(Entity e)
    {
        var keywords = KeywordTags.Where(k => e.Has(k.Tag)).Select(k => k.Label).ToList();
        var card = Cards.Get(e.CardId);
        var cost = e.GetInt(GameTag.Cost);
        if (cost <= 0 && card?.Cost is > 0)
        {
            cost = card.Cost;
        }

        return new BgMinion(
            e.CardId,
            Cards.NameOr(e.CardId, e.Name),
            e.Attack,
            e.Health,
            e.GetInt(GameTag.Premium) > 0,
            e.GetInt(GameTag.TechLevel),
            keywords)
        {
            CardType = e.CardType is { Length: > 0 } type ? type : card?.Type ?? "MINION",
            Cost = cost,
        };
    }

    /// <summary>What the last combat simulation was given (for tests and diagnostics).</summary>
    internal CombatCapture? LastCapture { get; private set; }

    internal sealed record CombatCapture(SimSide Ours, SimSide Theirs, bool? OursFirst, int FirstAttackerEntityId);

    private sealed record SeenBoard(int Turn, IReadOnlyList<BgMinion> Minions);
}
