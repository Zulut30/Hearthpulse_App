using System.Globalization;
#if HEARTHPULSE_WPF
using Hearthpulse.App.Infrastructure;
#else
using Hearthpulse.UI.Infrastructure;
#endif
using Hearthpulse.Core;
using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Tracking;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Overlay;
#else
namespace Hearthpulse.UI.Overlay;
#endif

/// <summary>
/// Flattens live snapshots into display rows. Lists are only replaced when their content
/// changes, so item containers (and their images) survive the ~10 Hz snapshot stream.
/// </summary>
public sealed partial class OverlayViewModel : Observable
{
    private readonly Dictionary<string, string> _fingerprints = new();
    private BattlegroundsView? _bg;
    private CardDb _cards = CardDb.Empty;
    private string _locale = "enUS";

    // ---- visibility -----------------------------------------------------------------------
    private bool _showConstructed;
    private bool _showOpponent;
    private bool _showBattlegrounds;
    private bool _showHeroPick;
    private bool _showTrinkets;
    private bool _showShop;
    private bool _showDiscover;
    private bool _showOdds;

    public bool ShowConstructed { get => _showConstructed; private set => Set(ref _showConstructed, value); }
    public bool ShowOpponent { get => _showOpponent; private set => Set(ref _showOpponent, value); }
    public bool ShowBattlegrounds { get => _showBattlegrounds; private set => Set(ref _showBattlegrounds, value); }
    public bool ShowHeroPick { get => _showHeroPick; private set => Set(ref _showHeroPick, value); }
    public bool ShowTrinkets { get => _showTrinkets; private set => Set(ref _showTrinkets, value); }
    public bool ShowShop { get => _showShop; private set => Set(ref _showShop, value); }
    public bool ShowDiscover { get => _showDiscover; private set => Set(ref _showDiscover, value); }
    public bool ShowOdds { get => _showOdds; private set => Set(ref _showOdds, value); }

    // ---- pick suggestions (Settings: "Pick suggestions") ----------------------------------------
    private PickHints _pickHints = PickHints.StatsOnly;

    /// <summary>
    /// "Mark the best": BEST PICK / BUY badges, gold styling and Discover ranks are shown. Otherwise every Recommended /
    /// Best flag on the badge items is false and <see cref="DiscoverBadge.Rank"/> is 0.
    /// </summary>
    public bool ShowBestBadges => _pickHints == PickHints.Highlight;

    /// <summary>"Stats only": the same numbers on every option and nothing singled out.</summary>
    public bool StatsOnly => _pickHints == PickHints.StatsOnly;

    /// <summary>Pick suggestions are not "Off": the hero, trinket, tavern and Discover panels may show.</summary>
    public bool ShowPickPanels => _pickHints != PickHints.Off;

    // ---- constructed ----------------------------------------------------------------------
    private string _deckName = string.Empty;
    private string _deckRecord = string.Empty;
    private string _deckCounts = string.Empty;
    private string _nextDraw = string.Empty;
    private SyncList<DeckTile> _deck = new();
    private bool _showChance = true;

    public string DeckName { get => _deckName; private set => Set(ref _deckName, value); }
    public string DeckRecord { get => _deckRecord; private set => Set(ref _deckRecord, value); }
    public string DeckCounts { get => _deckCounts; private set => Set(ref _deckCounts, value); }
    public string NextDraw { get => _nextDraw; private set => Set(ref _nextDraw, value); }
    public IReadOnlyList<DeckTile> Deck => _deck;
    public bool ShowChance { get => _showChance; private set => Set(ref _showChance, value); }

    private string _opponentTitle = string.Empty;
    private string _opponentSubtitle = string.Empty;
    private string _opponentHeroId = string.Empty;
    private int _opponentHand;
    private int _opponentDeck;
    private int _opponentSecrets;
    private string _opponentMatchup = string.Empty;
    private string _opponentMatchupRate = string.Empty;
    private double _opponentMatchupEdge;
    private string _opponentMatchupTip = string.Empty;
    private SyncList<PlayedTile> _played = new();
    private SyncList<HandTile> _hand = new();

    public string OpponentTitle { get => _opponentTitle; private set => Set(ref _opponentTitle, value); }
    public string OpponentSubtitle { get => _opponentSubtitle; private set => Set(ref _opponentSubtitle, value); }
    public string OpponentHeroId { get => _opponentHeroId; private set => Set(ref _opponentHeroId, value); }
    public int OpponentHand { get => _opponentHand; private set => Set(ref _opponentHand, value); }
    public int OpponentDeck { get => _opponentDeck; private set => Set(ref _opponentDeck, value); }
    public int OpponentSecrets { get => _opponentSecrets; private set => Set(ref _opponentSecrets, value); }
    /// <summary>Your record with this deck against the opponent's class, short ("2–1"); empty before the first game.</summary>
    public string OpponentMatchup { get => _opponentMatchup; private set => Set(ref _opponentMatchup, value); }

    /// <summary>The win rate of <see cref="OpponentMatchup"/> ("67 %"), coloured by <see cref="OpponentMatchupEdge"/>.</summary>
    public string OpponentMatchupRate { get => _opponentMatchupRate; private set => Set(ref _opponentMatchupRate, value); }

    /// <summary>The win rate above or below an even split: its sign colours the rate.</summary>
    public double OpponentMatchupEdge { get => _opponentMatchupEdge; private set => Set(ref _opponentMatchupEdge, value); }

    /// <summary>Whose score it is and against what, for the tooltip of the short line.</summary>
    public string OpponentMatchupTip { get => _opponentMatchupTip; private set => Set(ref _opponentMatchupTip, value); }
    public IReadOnlyList<PlayedTile> Played => _played;
    public IReadOnlyList<HandTile> Hand => _hand;

    // ---- battlegrounds --------------------------------------------------------------------
    private int _turn;
    private string _phase = string.Empty;
    private SyncList<TierButton> _tiers = new();
    private IReadOnlyList<string> _tribes = Array.Empty<string>();
    private string _rating = string.Empty;
    private string _ratingDelta = string.Empty;
    private int _ratingDeltaValue;
    private IReadOnlyList<CombatDot> _combats = Array.Empty<CombatDot>();
    private IReadOnlyList<HeroPickCard> _heroPicks = Array.Empty<HeroPickCard>();
    private IReadOnlyList<ShopBadge> _shopBadges = Array.Empty<ShopBadge>();
    private IReadOnlyList<TrinketBadge> _trinketPicks = Array.Empty<TrinketBadge>();
    private IReadOnlyList<DiscoverBadge> _discoverBadges = Array.Empty<DiscoverBadge>();
    private string _oddsWin = string.Empty;
    private string _oddsTie = string.Empty;
    private string _oddsLoss = string.Empty;
    private string _oddsNote = string.Empty;
    private string _oddsDamageWin = string.Empty;
    private string _oddsDamageWinTip = string.Empty;
    private string _oddsDamageLoss = string.Empty;
    private string _oddsDamageLossTip = string.Empty;
    private string _oddsLethal = string.Empty;
    private string _oddsKill = string.Empty;
    private bool _lethalDanger;
    private string _metaSource = string.Empty;
    private int _selectedTier;
    private IReadOnlyList<BrowserGroup> _browser = Array.Empty<BrowserGroup>();
    private string _anomaly = string.Empty;
    private LobbyPopup? _lobby;
    private double _lobbyTop;
    private double _lobbyLeft;

    public int Turn { get => _turn; private set => Set(ref _turn, value); }
    public string Phase { get => _phase; private set => Set(ref _phase, value); }
    public IReadOnlyList<TierButton> Tiers => _tiers;
    public IReadOnlyList<string> Tribes { get => _tribes; private set => Set(ref _tribes, value); }
    public string Rating { get => _rating; private set => Set(ref _rating, value); }
    public string RatingDelta { get => _ratingDelta; private set => Set(ref _ratingDelta, value); }
    public int RatingDeltaValue { get => _ratingDeltaValue; private set => Set(ref _ratingDeltaValue, value); }
    public IReadOnlyList<CombatDot> Combats { get => _combats; private set => Set(ref _combats, value); }
    public IReadOnlyList<HeroPickCard> HeroPicks { get => _heroPicks; private set => Set(ref _heroPicks, value); }
    public IReadOnlyList<ShopBadge> ShopBadges { get => _shopBadges; private set => Set(ref _shopBadges, value); }
    public IReadOnlyList<TrinketBadge> TrinketPicks { get => _trinketPicks; private set => Set(ref _trinketPicks, value); }
    public IReadOnlyList<DiscoverBadge> DiscoverBadges { get => _discoverBadges; private set => Set(ref _discoverBadges, value); }
    public string OddsWin { get => _oddsWin; private set => Set(ref _oddsWin, value); }
    public string OddsTie { get => _oddsTie; private set => Set(ref _oddsTie, value); }
    public string OddsLoss { get => _oddsLoss; private set => Set(ref _oddsLoss, value); }
    public string OddsNote { get => _oddsNote; private set => Set(ref _oddsNote, value); }

    /// <summary>Hero damage if we win ("+8 ±2") / if we lose ("−6 ±2"); empty when that result never came up.</summary>
    public string OddsDamageWin { get => _oddsDamageWin; private set => Set(ref _oddsDamageWin, value); }
    public string OddsDamageWinTip { get => _oddsDamageWinTip; private set => Set(ref _oddsDamageWinTip, value); }
    public string OddsDamageLoss { get => _oddsDamageLoss; private set => Set(ref _oddsDamageLoss, value); }
    public string OddsDamageLossTip { get => _oddsDamageLossTip; private set => Set(ref _oddsDamageLossTip, value); }

    /// <summary>"you die: 12%" while any simulated fight kills the player's hero; empty otherwise.</summary>
    public string OddsLethal { get => _oddsLethal; private set => Set(ref _oddsLethal, value); }

    /// <summary>"you kill: 30%" while any simulated fight kills the opponent's hero; empty otherwise.</summary>
    public string OddsKill { get => _oddsKill; private set => Set(ref _oddsKill, value); }

    /// <summary>The fight can kill the player: the odds panel turns red.</summary>
    public bool LethalDanger { get => _lethalDanger; private set => Set(ref _lethalDanger, value); }
    public string MetaSource { get => _metaSource; private set => Set(ref _metaSource, value); }

    private string _discoverTitle = string.Empty;

    /// <summary>What the open choice comes from ("Hero power · …") and whether there is a clear best pick.</summary>
    public string DiscoverTitle { get => _discoverTitle; private set => Set(ref _discoverTitle, value); }
    public int SelectedTier { get => _selectedTier; private set => Set(ref _selectedTier, value); }
    public IReadOnlyList<BrowserGroup> Browser { get => _browser; private set => Set(ref _browser, value); }
    public string Anomaly { get => _anomaly; private set => Set(ref _anomaly, value); }
    public LobbyPopup? Lobby { get => _lobby; private set => Set(ref _lobby, value); }
    public double LobbyTop { get => _lobbyTop; private set => Set(ref _lobbyTop, value); }
    public double LobbyLeft { get => _lobbyLeft; private set => Set(ref _lobbyLeft, value); }

    /// <summary>Current Battlegrounds game is Duos (the leaderboard is laid out in teams).</summary>
    public bool IsDuos { get; private set; }

    // ---- lobby list, session, board -------------------------------------------------------
    private bool _showLobby;
    private bool _showSession;
    private SyncList<LobbyRow> _lobbyRows = new();
    private string _sessionRating = string.Empty;
    private IReadOnlyList<PlaceChip> _sessionPlaces = Array.Empty<PlaceChip>();
    private bool _layoutEditing;

    public bool ShowLobby { get => _showLobby; private set => Set(ref _showLobby, value); }
    public bool ShowSession { get => _showSession; private set => Set(ref _showSession, value); }
    public IReadOnlyList<LobbyRow> LobbyRows => _lobbyRows;
    public string SessionRating { get => _sessionRating; private set => Set(ref _sessionRating, value); }
    public IReadOnlyList<PlaceChip> SessionPlaces { get => _sessionPlaces; private set => Set(ref _sessionPlaces, value); }

    /// <summary>Layout mode: every panel is shown with a frame and can be dragged, resized and reset.</summary>
    public bool LayoutEditing { get => _layoutEditing; set => Set(ref _layoutEditing, value); }

    public event Action? ResetLayoutRequested;

    // ---- toast ----------------------------------------------------------------------------
    private string _toastTitle = string.Empty;
    private string _toastDetail = string.Empty;
    private string _toastResult = string.Empty;
    private bool _showToast;

    public string ToastTitle { get => _toastTitle; private set => Set(ref _toastTitle, value); }
    public string ToastDetail { get => _toastDetail; private set => Set(ref _toastDetail, value); }
    public string ToastResult { get => _toastResult; private set => Set(ref _toastResult, value); }
    public bool ShowToast { get => _showToast; set => Set(ref _showToast, value); }

    public OverlayViewModel()
    {
        Loc.Instance.LanguageChanged += OnLanguageChanged;
        SelectTier = new RelayCommand(p =>
        {
            var tier = Convert.ToInt32(p, CultureInfo.InvariantCulture);
            SelectedTier = SelectedTier == tier ? 0 : tier;
            RebuildBrowser();
            RebuildTiers();
        });
        FinishLayout = new RelayCommand(() => LayoutEditing = false);
        ResetLayout = new RelayCommand(() => ResetLayoutRequested?.Invoke());
        CloseBrowser = new RelayCommand(() =>
        {
            SelectedTier = 0;
            _selectedTribe = string.Empty;
            RebuildTribeChips();
            RebuildBrowser();
            RebuildTiers();
        });
    }

    /// <summary>
    /// The interface language changed: texts built here are rebuilt now or on the next snapshot (the host pushes the
    /// last one again), so the caches that skip unchanged lists are cleared.
    /// </summary>
    private void OnLanguageChanged()
    {
        _fingerprints.Clear();
        _handMarksKey = string.Empty;
        Lobby = null;
        RebuildBrowser();
        RebuildCounters();
        RebuildBgCounters();
        if (_lastOutcome is { } outcome)
        {
            ToastTexts(outcome);
        }
    }

    private GameOutcome? _lastOutcome;

    public RelayCommand SelectTier { get; }

    public RelayCommand CloseBrowser { get; }

    public RelayCommand FinishLayout { get; }

    public RelayCommand ResetLayout { get; }

    public void Update(LiveSnapshot snapshot, CardDb cards, AppSettings settings, DeckAggregate? deck, MatchupAggregate? matchup)
    {
        _cards = cards;
        _locale = cards.Locale;
        var enabled = settings.OverlayEnabled && snapshot.InGame;

        var constructed = enabled ? snapshot.Constructed : null;
        ShowConstructed = constructed is not null && settings.ShowDeckTracker && constructed.DeckKnown;
        ShowOpponent = constructed is not null && settings.ShowOpponentTracker;
        ShowChance = settings.ShowDrawChance;
        if (constructed is not null)
        {
            UpdateConstructed(constructed, deck, matchup);
        }
        else
        {
            _deckLeft.Clear();
        }

        UpdateLive(constructed, settings);
        UpdateHandMarks(constructed, settings);
        UpdateCardPool(constructed, settings);
        UpdateArchetype(constructed, settings);
        UpdateTurnTimer(constructed, settings);
        UpdateLadder(snapshot, settings);
        UpdateSecrets(constructed, settings);

        var bg = enabled ? snapshot.Battlegrounds : null;
        _bg = bg;
        UpdateBgCounters(bg, settings);
        UpdateArena(snapshot.ArenaDraft, settings);
        ShowBattlegrounds = bg is not null && settings.ShowBattlegroundsBar;
        ApplyPickSettings(settings);
        ShowHeroPick = bg is { IsHeroSelection: true } && ShowPickPanels && bg.HeroOptions.Count > 0;
        var playing = bg is { IsHeroSelection: false, HeroCardId: not null } && ShowPickPanels;
        ShowTrinkets = playing && bg!.TrinketOptions.Count > 0;
        ShowDiscover = playing && bg!.DiscoverAdvice.Count > 0;
        ShowOdds = settings.ShowCombatOdds && bg is { InCombat: true, CombatOdds: not null };
        // Buy hints sit on the tavern row, so only while recruiting and while no choice covers the shop.
        ShowShop = playing && !bg!.InCombat && bg.TrinketOptions.Count == 0 && bg.DiscoverOptions.Count == 0 && bg.ShopAdvice.Count > 0;
        ShowLobby = bg is { IsHeroSelection: false } && settings.ShowLobbyPanel && bg.Opponents.Count > 0;
        ShowSession = bg is not null && settings.ShowSessionPanel;
        if (bg is not null)
        {
            UpdateBattlegrounds(bg, snapshot);
            UpdateLobbyAndSession(bg, snapshot);
        }
        else if (SelectedTier != 0)
        {
            SelectedTier = 0;
            Browser = Array.Empty<BrowserGroup>();
        }

        if (bg is null)
        {
            Lobby = null;
        }
    }

    private void ApplyPickSettings(AppSettings settings)
    {
        var hints = _pickHints != settings.PickHints;
        _pickHints = settings.PickHints;
        if (hints)
        {
            Raise(nameof(ShowBestBadges));
            Raise(nameof(StatsOnly));
            Raise(nameof(ShowPickPanels));
        }
    }

    private void UpdateConstructed(ConstructedView view, DeckAggregate? deck, MatchupAggregate? matchup)
    {
        DeckName = view.DeckName ?? Loc.T("Overlay.Deck.Fallback");
        var (record, recordTip, _) = DeckHeader.Describe(deck, view.DeckMeta);
        DeckRecord = record;
        DeckRecordTip = recordTip;
        DeckCounts = Loc.F("Overlay.Deck.Counts", view.DeckCount, view.HandCount);
        var best = view.Deck.Where(r => r.Remaining > 0).MaxBy(r => r.DrawChance);
        NextDraw = best is null || view.DeckCount == 0 ? string.Empty : Loc.F("Overlay.Deck.NextDraw", best.Name, best.DrawChance);
        Replace(nameof(Deck), view.Deck, r => $"{r.CardId}{r.Remaining}{r.Total}{r.DrawChance:0.00}", () =>
        {
            var changed = DeckChanges(view.Deck);
            Sync(ref _deck, nameof(Deck), view.Deck
            .Select(r => new DeckTile(
                r.CardId,
                r.Name,
                r.Cost,
                r.Rarity == "LEGENDARY" && r.Total == 1 ? "★" : r.Remaining.ToString(CultureInfo.InvariantCulture),
                r.Total > 1 || r.Rarity == "LEGENDARY" || r.IsAdded,
                r.Remaining == 0,
                r.Rarity,
                r.Remaining > 0 && view.DeckCount > 0 ? OverlayFormat.Percent(r.DrawChance) : string.Empty,
                r.IsAdded,
                changed.Contains(DeckKey(r))))
            .ToList());
        });

        OpponentHeroId = view.OpponentHeroCardId;
        OpponentTitle = string.IsNullOrEmpty(view.OpponentName) ? ClassName(view.OpponentClass) : view.OpponentName!;
        OpponentSubtitle = string.Join(" · ", new[] { ClassName(view.OpponentClass), view.OpponentRank?.Display }.Where(s => !string.IsNullOrEmpty(s)));
        OpponentHand = view.OpponentHandCount;
        OpponentDeck = view.OpponentDeckCount;
        OpponentSecrets = view.OpponentSecrets;
        // Short on the panel ("2–1 · 67 %"); whose games these are goes to the tooltip.
        var hasMatchup = matchup is { Games: > 0 };
        OpponentMatchup = hasMatchup ? Loc.F("Overlay.Opponent.Matchup", matchup!.Wins, matchup.Losses) : string.Empty;
        OpponentMatchupRate = hasMatchup && matchup!.Wins + matchup.Losses > 0 ? matchup.WinRate.ToString("P0", CultureInfo.CurrentCulture) : string.Empty;
        OpponentMatchupEdge = hasMatchup && matchup!.Wins + matchup.Losses > 0 ? matchup.WinRate - 0.5 : 0;
        OpponentMatchupTip = hasMatchup ? Loc.P("Overlay.Opponent.MatchupTip", matchup!.Games, ClassName(view.OpponentClass)) : string.Empty;
        Replace(nameof(Played), view.OpponentPlayed, p => $"{p.CardId}{p.Count}", () => Sync(ref _played, nameof(Played), view.OpponentPlayed
            .Select(p => new PlayedTile(p.CardId, p.Name, p.Cost, p.Count > 1 ? $"×{p.Count}" : string.Empty, Loc.F("Overlay.Turn.Short", p.FirstTurn), p.IsCreated))
            .ToList()));
        Replace(nameof(Hand), view.OpponentHand, h => $"{h.Position}{h.Turn}{h.CardId}{h.CreatedBy}{h.CreatedById}{h.FromMulligan}", () => Sync(ref _hand, nameof(Hand), view.OpponentHand
            .Select(h => new HandTile(
                h.Position.ToString(CultureInfo.InvariantCulture),
                h.Turn > 0 ? Loc.F("Overlay.Turn.Short", h.Turn) : Loc.T("Overlay.Hand.MulliganShort"),
                // A card nothing is known about shows only its slot and turn: ten rows of "Unknown" said nothing. A card
                // kept in the mulligan says so in the turn column already.
                h.IsCoin ? Loc.T("Overlay.Hand.Coin") : h.CardName ?? (h.CreatedBy is not null ? Loc.F("Overlay.Hand.CreatedBy", h.CreatedBy) : string.Empty),
                h.CardName is not null || h.IsCoin,
                h.CardName is null && !h.IsCoin && h.CreatedBy is null && !h.FromMulligan)
            {
                PreviewCardId = h.CardId is { } id && !h.IsCoin ? id : h.CreatedById,
            })
            .ToList()));
    }

    private void UpdateBattlegrounds(BattlegroundsView view, LiveSnapshot snapshot)
    {
        Turn = view.Turn;
        IsDuos = view.IsDuos;
        Phase = view.IsHeroSelection ? Loc.T("Overlay.Phase.HeroSelection") : view.InCombat ? Loc.T("Overlay.Phase.Combat") : Loc.T("Overlay.Phase.Recruit");
        var anomaly = view.AnomalyCardId is { } a ? _cards.NameOf(a) : string.Empty;
        Anomaly = anomaly;

        var rating = snapshot.Rating is null ? 0 : view.IsDuos ? snapshot.Rating.Duos : snapshot.Rating.Solo;
        Rating = rating > 0 ? rating.ToString("N0", CultureInfo.CurrentCulture) : "—";
        var delta = view.IsDuos ? null : snapshot.Session.Delta;
        RatingDeltaValue = delta ?? 0;
        RatingDelta = delta is { } dv && dv != 0 ? (dv > 0 ? $"+{dv}" : $"−{-dv}") : string.Empty;

        Replace(nameof(Tribes), view.Races, r => r, () =>
        {
            Tribes = view.Races.Select(r => Races.Display(r, _locale)).ToList();
            RebuildTribeChips();
        });
        Combats = view.Combats.TakeLast(8)
            .Select(c => new CombatDot(c.Result, Loc.F("Overlay.Combat.Tip", c.Turn, _cards.NameOf(c.OpponentHeroCardId), ResultText(c.Result))
                + (c.DamageTaken > 0 ? Loc.F("Overlay.Combat.Took", c.DamageTaken) : c.DamageDealt > 0 ? Loc.F("Overlay.Combat.Dealt", c.DamageDealt) : string.Empty)
                + (c.Odds is { } o ? Loc.F("Overlay.Combat.Predicted", OddsPercent(o.Win, o.Runs), OddsPercent(o.Loss, o.Runs)) : string.Empty)))
            .ToList();

        if (view.CombatOdds is { } odds)
        {
            OddsWin = OddsPercent(odds.Win, odds.Runs);
            OddsTie = OddsPercent(odds.Tie, odds.Runs);
            OddsLoss = OddsPercent(odds.Loss, odds.Runs);
            SetOddsShares(odds.Win, odds.Tie, odds.Loss);
            OddsNote = odds.Approximate
                ? Loc.P("Overlay.Odds.Estimate", odds.Unsimulated)
                : Loc.T("Overlay.Odds.Simulated");
            if (odds.DamageCap > 0)
            {
                OddsNote = Loc.F("Overlay.Odds.Cap", OddsNote, odds.DamageCap);
            }

            OddsDamageWin = CombatOddsText.Damage(odds.DamageDealt, dealt: true);
            OddsDamageWinTip = CombatOddsText.DamageTip(odds.DamageDealt, dealt: true);
            OddsDamageLoss = CombatOddsText.Damage(odds.DamageTaken, dealt: false);
            OddsDamageLossTip = CombatOddsText.DamageTip(odds.DamageTaken, dealt: false);
            OddsLethal = odds.LethalTaken is > 0 and var lethal ? Loc.F("Overlay.Odds.Lethal", OddsPercent(lethal, odds.Runs)) : string.Empty;
            OddsKill = odds.LethalDealt is > 0 and var kill ? Loc.F("Overlay.Odds.Kill", OddsPercent(kill, odds.Runs)) : string.Empty;
            LethalDanger = OddsLethal.Length > 0;
        }
        RebuildTiers();

        // Only "Mark the best" singles an option out; "Stats only" keeps the advisor's numbers but every flag false.
        var highlight = ShowBestBadges;

        // Lobby-adjusted numbers only mean something once the tribes are known.
        var lobbyKnown = view.Races.Count >= 4;
        var heroTierTip = string.Join("\n", new[] { view.MetaSource ?? string.Empty, Loc.T("Overlay.Tier.Heroes") }.Where(s => s.Length > 0));
        Replace(nameof(HeroPicks), view.HeroOptions, h => $"{heroTierTip}{lobbyKnown}{h.CardId}{h.Meta?.AdjustedPosition:0.000}{highlight && h.Recommended}{string.Join(',', h.TopComps ?? [])}", () => HeroPicks = !view.HeroOptions.Any(h => h.Meta is not null) ? [] : view.HeroOptions
            .Select(h =>
            {
                var adjusted = h.Meta is not null && lobbyKnown;
                var delta = adjusted ? h.Meta!.AdjustedPosition - h.Meta.AveragePosition : 0;
                return new HeroPickCard(
                    h.CardId,
                    h.Name,
                    h.Meta?.Tier ?? "–",
                    h.Meta is null ? "—" : (adjusted ? h.Meta.AdjustedPosition : h.Meta.AveragePosition).ToString("0.00", CultureInfo.CurrentCulture),
                    h.Meta is null ? Loc.T("Overlay.Badge.NoData") : adjusted ? Loc.T("Overlay.Badge.AvgPlaceLobby") : Loc.T("Overlay.Badge.AvgPlace"),
                    Math.Abs(delta) < 0.005 ? string.Empty : delta < 0 ? $"▲{-delta:0.00}" : $"▼{delta:0.00}",
                    -delta,
                    h.Meta is null ? string.Empty : Loc.F("Overlay.Badge.PickRate", h.Meta.PickRate.ToString("P0", CultureInfo.CurrentCulture)),
                    h.Stats is { Games: > 0 } s ? Loc.P("Overlay.Hero.Personal", s.Games, s.AveragePlace) : string.Empty,
                    string.Join(" · ", (h.TopComps ?? Array.Empty<string>()).Take(2)),
                    highlight && h.Recommended,
                    h.Meta is not null) { TierTip = heroTierTip };
            })
            .ToList());

        var trinketTierTip = Loc.T("Overlay.Trinket.Source") + "\n" + Loc.T("Overlay.Tier.Heroes");
        Replace(nameof(TrinketPicks), view.TrinketOptions, t => $"{t.CardId}{t.Meta?.AveragePlacement:0.000}{highlight && t.Recommended}", () => TrinketPicks = !view.TrinketOptions.Any(t => t.Meta is not null) ? [] : view.TrinketOptions
            .Select(t => new TrinketBadge(
                t.CardId,
                t.Name,
                t.Meta?.Tier ?? "–",
                t.Meta is null ? "—" : t.Meta.AveragePlacement.ToString("0.00", CultureInfo.CurrentCulture),
                t.Meta is null ? Loc.T("Overlay.Badge.NoData") : Loc.F("Overlay.Badge.PickRate", t.Meta.PickRate.ToString("P0", CultureInfo.CurrentCulture)),
                highlight && t.Recommended,
                t.Meta is not null) { TierTip = trinketTierTip })
            .ToList());

        var mode = $"{_pickHints}";

        // One slot per option in screen order, so each badge lands above its own card.
        Replace(nameof(DiscoverBadges), view.DiscoverAdvice, d => $"{mode}{d.Slot}{d.Score}{d.Best}{d.Reason}{d.Placement:0.000}{d.TechLevel}", () =>
        {
            var gifts = view.DiscoverOptions;
            var ranked = view.DiscoverAdvice.OrderByDescending(d => d.Score).Select(d => d.Slot).ToList();
            DiscoverBadges = view.DiscoverAdvice
                .OrderBy(d => d.Slot)
                .Select(d => new DiscoverBadge(
                    highlight ? ranked.IndexOf(d.Slot) + 1 : 0,
                    ShortReason(d.Reason, highlight),
                    d.Slot < gifts.Count ? gifts[d.Slot].DarkGift ?? string.Empty : string.Empty,
                    d.Placement is { } p ? p.ToString("0.00", CultureInfo.CurrentCulture) : "—",
                    d.Placement is null ? Loc.T("Overlay.Badge.NoDataYet") : Loc.T("Overlay.Badge.AvgPlace"),
                    highlight && d.Best,
                    d.Placement is not null,
                    d.TechLevel))
                .ToList();
        });
        var source = view.DiscoverSource ?? Loc.T("Overlay.Discover.Name");
        DiscoverTitle = view.DiscoverAdvice.Count == 0
            ? string.Empty
            : !highlight
                ? view.DiscoverSource is { } offeredBy ? Loc.F("Overlay.Discover.OfferedBy", offeredBy) : Loc.T("Overlay.Discover.CommunityStats")
                : view.DiscoverAdvice.Any(d => d.Best)
                    ? Loc.F("Overlay.Discover.BestMarked", source)
                    : Loc.F("Overlay.Discover.CloseCall", source);

        MetaSource = view.MetaSource ?? string.Empty;

        var shopSize = view.Shop.Count;
        Replace(nameof(ShopBadges), view.ShopAdvice, s => $"{mode}{shopSize}{s.Slot}{s.Score}{s.Best}{s.Reason}{s.Placement:0.000}", () => ShopBadges = view.ShopAdvice
            .Select(s => (Pick: s, Label: ShortReason(s.Reason, highlight)))
            // "Mark the best" badges the best buy and other strong ones; "Stats only" shows what is known about every minion.
            .Where(x => highlight ? x.Pick.Best || x.Pick.Score >= StrongPick : x.Label.Length > 0 || x.Pick.Placement is not null)
            .Select(x => new ShopBadge(
                ShopRowCenter + ((x.Pick.Slot - ((shopSize - 1) / 2.0)) * ShopMinionPitch) - (ShopBadgeWidth / 2),
                x.Label,
                highlight && x.Pick.Best,
                x.Pick.Placement is { } p ? Loc.F("Overlay.Badge.AvgPlaceValue", p.ToString("0.00", CultureInfo.CurrentCulture)) : string.Empty))
            .ToList());

        if (SelectedTier > 0 && _fingerprints.GetValueOrDefault("BrowserRaces") != string.Join(',', view.Races))
        {
            RebuildBrowser();
        }
    }

    private void UpdateLobbyAndSession(BattlegroundsView view, LiveSnapshot snapshot)
    {
        var rows = view.Opponents
            .Select(o => new LobbyRow(
                o.Place,
                o.HeroCardId,
                o.HeroName,
                o.Health + o.Armor,
                0,
                o.TechLevel,
                o.Triples,
                o.LastResult switch { "Win" => Loc.F("Overlay.Lobby.Won", o.LastDamage), "Loss" => Loc.F("Overlay.Lobby.Lost", o.LastDamage), "Tie" => Loc.T("Overlay.Lobby.Tie"), _ => string.Empty },
                o.LastResult ?? string.Empty,
                o.IsNextOpponent,
                o.Dead,
                false))
            .ToList();
        if (view.HeroCardId is { } hero)
        {
            rows.Add(new LobbyRow(view.Place, hero, _cards.NameOf(hero), view.Health + view.Armor, 0, view.TechLevel, view.Triples,
                string.Empty, string.Empty, false, view.Health <= 0, true));
        }

        // Empty during hero selection: no opponents seen and no hero picked yet.
        var top = Math.Max(1, rows.Select(r => r.Health).DefaultIfEmpty(0).Max());
        // In place: a fight changes one or two rows, and the others keep their portraits.
        Replace(nameof(LobbyRows), rows, r => $"{r.Place}{r.HeroCardId}{r.Health}{r.Tier}{r.Triples}{r.Last}{r.Next}{r.Dead}", () => Sync(ref _lobbyRows, nameof(LobbyRows), rows
            .Select(r => r with { HealthFraction = r.Dead ? 0 : Math.Clamp(r.Health / (double)top, 0.04, 1) })
            .OrderBy(r => r.Place <= 0 ? 99 : r.Place)
            .ToList()));

        var session = snapshot.Session;
        SessionRating = session.StartRating is { } start && session.CurrentRating is { } now
            ? $"{start:N0} → {now:N0}"
            : Rating;
        var cut = view.IsDuos ? 2 : 4;
        Replace(nameof(SessionPlaces), session.Placements, p => p.ToString(CultureInfo.InvariantCulture), () =>
            SessionPlaces = session.Placements.TakeLast(12).Select(p => new PlaceChip(p, p <= cut)).ToList());
    }

    // Tavern row geometry in 1080-unit overlay space (HDT: minion 0.12H plus a 0.0039H margin each side).
    public const double ShopRowWidth = 7 * ShopMinionPitch;
    public const double ShopBadgeWidth = 126;
    private const double ShopMinionPitch = 138;
    private const double ShopRowCenter = ShopRowWidth / 2;
    private const int StrongPick = 40;

    private static readonly string[] ReasonTags = ["Triple", "Pair", "Core", "Add-on", "Cycle", "Fits tribe", "Board synergy"];

    /// <summary>String keys of <see cref="ReasonTags"/> (and "Best stats"), for the badge text.</summary>
    private static readonly Dictionary<string, string> ReasonTagKeys = new(StringComparer.Ordinal)
    {
        ["Triple"] = "Overlay.Tag.Triple",
        ["Pair"] = "Overlay.Tag.Pair",
        ["Core"] = "Overlay.Tag.Core",
        ["Add-on"] = "Overlay.Tag.AddOn",
        ["Cycle"] = "Overlay.Tag.Cycle",
        ["Fits tribe"] = "Overlay.Tag.FitsTribe",
        ["Board synergy"] = "Overlay.Tag.BoardSynergy",
        ["Best stats"] = "Overlay.Tag.BestStats",
    };
    // Tags about a target comp: the overlay shows no comp since the game plan panel went, so they would point at nothing.
    private static readonly string[] CompReasonTags = ["Core", "Add-on", "Cycle", "Fits tribe"];

    /// <summary>
    /// Badge text from the advisor's reason: its tags only, without the comp name or the tags about a comp.
    /// <paramref name="bestStats"/> false drops the "Best stats" fallback.
    /// </summary>
    private static string ShortReason(string reason, bool bestStats)
    {
        var parts = reason.Split(" · ");
        var tags = parts.Where(p => ReasonTags.Contains(p) && !CompReasonTags.Contains(p)).Take(2).ToList();
        if (tags.Count == 0 && bestStats && parts.Contains("Best stats available"))
        {
            tags.Add("Best stats");
        }

        return string.Join(" · ", tags.Select(t => Loc.T(ReasonTagKeys[t])));
    }

    private void RebuildTiers()
    {
        if (_bg is not { } bg)
        {
            return;
        }

        var max = Math.Max(6, bg.MaxTechLevel);
        // Runs on every Battlegrounds snapshot: only a button whose state moved is replaced.
        Sync(ref _tiers, nameof(Tiers), Enumerable.Range(1, max)
            .Select(t => new TierButton(t, t == bg.TechLevel, t == SelectedTier, t > 6 && bg.MaxTechLevel < 7))
            .ToList());
    }

    private void RebuildBrowser()
    {
        UpdateBrowserHeading();
        if (_bg is not { } bg || SelectedTier == 0)
        {
            Browser = Array.Empty<BrowserGroup>();
            return;
        }

        _fingerprints["BrowserRaces"] = string.Join(',', bg.Races);
        var lobby = bg.Races.ToHashSet(StringComparer.Ordinal);

        var minions = _cards.BattlegroundsMinions
            .Where(c => c.TechLevel == SelectedTier && (bg.IsDuos || !c.IsBattlegroundsDuosExclusive))
            .Where(c => lobby.Count == 0 || c.Races.Count == 0 || c.Races.Any(r => r == "ALL" || lobby.Contains(r)))
            .Where(c => InChosenTribe(c.Races))
            .ToList();

        // Before the lobby is known, show every tribe in the pool.
        var order = lobby.Count > 0
            ? bg.Races.ToList()
            : minions.SelectMany(c => c.Races).Where(r => r != "ALL").Distinct().OrderBy(r => r, StringComparer.Ordinal).ToList();
        var known = order.ToHashSet(StringComparer.Ordinal);
        var groups = new List<BrowserGroup>();
        foreach (var race in order.Append("ALL").Append(string.Empty))
        {
            var cards = minions
                .Where(c => race.Length == 0 ? c.Races.Count == 0 : race == "ALL" ? c.Races.Contains("ALL") : c.Races.Contains(race) && !c.Races.Contains("ALL"))
                .Where(c => race.Length == 0 || race == "ALL" || c.Races.First(known.Contains) == race)
                .OrderBy(c => c.Name, StringComparer.CurrentCulture)
                .Select(c => new BrowserCard(c.Id, c.Name))
                .ToList();
            if (cards.Count > 0)
            {
                var title = race.Length == 0 ? Loc.T("Overlay.Browser.Neutral") : race == "ALL" ? Loc.T("Overlay.Browser.AllTribes") : Races.Display(race, _locale);
                groups.Add(new BrowserGroup(title, cards));
            }
        }

        // A tribe chosen on the bar shows its minions only; tavern spells belong to no tribe.
        var spells = _cards.BattlegroundsSpells.Where(c => c.TechLevel == SelectedTier && _selectedTribe.Length == 0).OrderBy(c => c.Name, StringComparer.CurrentCulture)
            .Select(c => new BrowserCard(c.Id, c.Name)).ToList();
        if (spells.Count > 0)
        {
            groups.Add(new BrowserGroup(Loc.T("Overlay.Browser.TavernSpells"), spells));
        }

        Browser = groups;
    }

    /// <summary>Updates the leaderboard hover popup; <paramref name="slot"/> is 1..8 or 0 for none.</summary>
    public void HoverLeaderboard(int slot, double top, double left) =>
        ShowLobbyPopup(slot > 0 ? _bg?.Opponents.FirstOrDefault(o => o.Place == slot) : null, top, left);

    /// <summary>Updates the popup for the hero the game reports under the mouse; null clears it.</summary>
    public void HoverLeaderboardHero(string? heroCardId, double top, double left)
    {
        BgOpponent? opponent = null;
        if (!string.IsNullOrEmpty(heroCardId) && _bg is { } bg)
        {
            opponent = bg.Opponents.FirstOrDefault(o => o.HeroCardId == heroCardId)
                       ?? bg.Opponents.FirstOrDefault(o => BaseHero(o.HeroCardId) == BaseHero(heroCardId));
        }

        ShowLobbyPopup(opponent, top, left);
    }

    /// <summary>Hero id without a skin suffix, so a skinned portrait still matches its hero.</summary>
    private static string BaseHero(string cardId)
    {
        var skin = cardId.IndexOf("_SKIN", StringComparison.OrdinalIgnoreCase);
        return skin > 0 ? cardId[..skin] : cardId;
    }

    private void ShowLobbyPopup(BgOpponent? opponent, double top, double left)
    {
        if (opponent is null)
        {
            Lobby = null;
            return;
        }

        LobbyTop = top;
        LobbyLeft = left;
        if (Lobby?.HeroCardId == opponent.HeroCardId && Lobby.Board.Count == opponent.LastBoard.Count && Lobby.Health == opponent.Health
            && Lobby.ResultKey == (opponent.LastResult ?? string.Empty) && Lobby.History.Count == FightsWith(opponent).Count)
        {
            return;
        }

        var turnsAgo = _bg!.Turn - opponent.LastBoardTurn;
        var fights = FightsWith(opponent);
        // The tribe most of the board shares, once it is at least two minions (amalgams count for every tribe, so not here).
        var tribe = opponent.LastBoard
            .SelectMany(m => (_cards.Get(m.CardId)?.Races ?? Array.Empty<string>()).Where(r => r != "ALL"))
            .GroupBy(r => r)
            .Select(g => (Race: g.Key, Count: g.Count()))
            .OrderByDescending(g => g.Count)
            .FirstOrDefault();
        Lobby = new LobbyPopup(
            opponent.HeroCardId,
            opponent.HeroName,
            opponent.Health,
            opponent.Armor,
            opponent.TechLevel,
            opponent.Place,
            opponent.Triples,
            opponent.LastResult switch
            {
                null => string.Empty,
                "Win" => Loc.F("Overlay.LobbyPopup.Won", opponent.LastDamage),
                "Loss" => Loc.F("Overlay.LobbyPopup.Lost", opponent.LastDamage),
                var r => ResultText(r),
            },
            opponent.LastBoard.Count == 0 ? Loc.T("Overlay.LobbyPopup.BoardNotSeen") : turnsAgo <= 0 ? Loc.T("Overlay.LobbyPopup.BoardThisTurn") : Loc.P("Overlay.LobbyPopup.BoardAgo", turnsAgo),
            opponent.IsNextOpponent,
            opponent.Dead,
            opponent.LastBoard.Select(m => new LobbyMinion(m.CardId, m.Name, m.Attack, m.Health, m.Golden, string.Join(" · ", m.Keywords))).ToList())
        {
            ResultKey = opponent.LastResult ?? string.Empty,
            History = fights.Count >= 2 ? fights.TakeLast(8).Select(c => new CombatDot(c.Result, string.Empty)).ToList() : [],
            Tribe = tribe.Count >= 2 ? $"{Races.Display(tribe.Race, _locale)} ×{tribe.Count}" : string.Empty,
        };
    }

    /// <summary>The fights with this opponent so far this game, oldest first.</summary>
    private List<CombatRecord> FightsWith(BgOpponent opponent) =>
        _bg?.Combats.Where(c => c.OpponentPlayerId == opponent.PlayerId).ToList() ?? [];

    public void ShowOutcome(GameOutcome outcome)
    {
        _lastOutcome = outcome;
        ToastTexts(outcome);
        ShowToast = true;
    }

    /// <summary>Title, colour key and detail of the end-of-game toast (rebuilt after a language change).</summary>
    private void ToastTexts(GameOutcome outcome)
    {
        var bg = outcome.Mode is "Battlegrounds" or "Duos";
        ToastTitle = bg ? Loc.F("Overlay.Toast.Place", Ordinal(outcome.Placement)) : outcome.Result == "Win" ? Loc.T("Overlay.Toast.Victory") : outcome.Result == "Loss" ? Loc.T("Overlay.Toast.Defeat") : ResultText(outcome.Result);
        ToastResult = bg ? (outcome.Placement <= (outcome.Mode == "Duos" ? 2 : 4) ? "Win" : "Loss") : outcome.Result;
        var hero = _cards.NameOf(outcome.HeroCardId);
        ToastDetail = outcome.RatingDelta is { } d
            ? Loc.F("Overlay.Toast.Rating", hero, d >= 0 ? "+" : "−", Math.Abs(d), outcome.RatingAfter)
            : hero;
    }

    /// <summary>
    /// Changes a bound list in place (<see cref="SyncList{T}"/>): the items control keeps the rows that did not change.
    /// Into or out of empty there is no row to keep: then the list is a new object and the view is told, so bindings on
    /// the list itself (the panel shows while it is not empty) look again in both UIs.
    /// </summary>
    private void Sync<T>(ref SyncList<T> list, string name, IReadOnlyList<T> items)
    {
        if (list.Count > 0 && items.Count > 0)
        {
            list.Sync(items);
            return;
        }

        if (list.Count != items.Count)
        {
            list = new SyncList<T>(items);
            Raise(name);
        }
    }

    private void Replace<T>(string key, IReadOnlyList<T> source, Func<T, string> fingerprint, Action apply)
    {
        var print = string.Join('|', source.Select(fingerprint));
        if (_fingerprints.TryGetValue(key, out var previous) && previous == print)
        {
            return;
        }

        _fingerprints[key] = print;
        apply();
    }

    public static string ClassName(string? cardClass) => string.IsNullOrEmpty(cardClass)
        ? string.Empty
        : Localized(cardClass switch
        {
            "DEMONHUNTER" => "Demon Hunter",
            "DEATHKNIGHT" => "Death Knight",
            _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(cardClass.ToLowerInvariant()),
        });

    /// <summary>A class name in the interface language ("Death Knight" has the key Overlay.Class.DeathKnight).</summary>
    private static string Localized(string className) => Loc.Or("Overlay.Class." + className.Replace(" ", string.Empty, StringComparison.Ordinal), className);

    /// <summary>A combat or game result ("Win", "Loss", "Tie") in the interface language; anything else as it is.</summary>
    private static string ResultText(string? result) => result switch
    {
        "Win" => Loc.T("Overlay.Result.Win"),
        "Loss" => Loc.T("Overlay.Result.Loss"),
        "Tie" => Loc.T("Overlay.Result.Tie"),
        _ => result ?? string.Empty,
    };

    /// <summary>
    /// A Monte Carlo result cannot prove 0% or 100%: show open bounds unless the odds came from an exact board
    /// (empty board, <see cref="Hearthpulse.Core.Battlegrounds.CombatOdds.Runs"/> == 0).
    /// </summary>
    private static string OddsPercent(double value, int runs)
    {
        if (runs <= 0)
        {
            return OverlayFormat.Percent(value);
        }

        if (value < 0.0005)
        {
            return OverlayFormat.Below(0.001, 1);
        }

        return value > 0.9995 ? OverlayFormat.Above(0.999, 1) : OverlayFormat.Percent(value, 1);
    }

    private static string Ordinal(int n) => n switch
    {
        1 => Loc.T("Overlay.Ordinal.First"),
        2 => Loc.T("Overlay.Ordinal.Second"),
        3 => Loc.T("Overlay.Ordinal.Third"),
        _ => Loc.F("Overlay.Ordinal.Other", n),
    };
}

