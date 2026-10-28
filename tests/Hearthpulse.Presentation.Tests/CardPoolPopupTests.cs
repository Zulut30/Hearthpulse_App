using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Threading;
using Hearthpulse.App.Infrastructure;
using Hearthpulse.App.Overlay;
using Hearthpulse.Core;
using Hearthpulse.Core.CardPool;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Tracking;
using AvaOverlay = Hearthpulse.UI.Overlay.OverlayViewModel;

namespace Hearthpulse.Presentation.Tests;

/// <summary>
/// The pool popup next to a hovered card: holding it, opening the browser with a right-click (the shipped WPF panel in a
/// hidden window, nothing on screen), letting go of it, and its compact lines - in both compiled view models.
/// </summary>
public sealed class CardPoolPopupTests
{
    private static readonly DateTime T0 = new(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);

    private static readonly TimeSpan Rested = OverlayViewModel.CardPoolHoldDelay + TimeSpan.FromMilliseconds(50);

    [Fact]
    public void Right_click_on_a_held_popup_opens_the_browser_in_the_wpf_overlay() => OnSta(() =>
    {
        var vm = new OverlayViewModel();
        var panel = new CardPoolPanel { Anchored = true, DataContext = vm };
        panel.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(OverlayViewModel.ShowCardPoolPopup)) { Converter = new BooleanToVisibilityConverter() });
        var root = new Canvas { Width = 1920, Height = 1080 };
        root.Children.Add(panel);
        // A window that is never shown: the panel is in a live visual tree (IsVisible and its hide handler work).
        using var source = new HwndSource(new HwndSourceParameters("pool popup test", 1920, 1080) { WindowStyle = 0 }) { RootVisual = root };
        vm.Update(Snapshot(Pool("TEST_GEN", 40)), CardDb.Empty, new AppSettings(), null, null);
        root.UpdateLayout();
        Assert.True(panel.IsVisible);

        vm.UpdateCardPoolPointer(true, false, false, T0);
        vm.UpdateCardPoolPointer(true, false, false, T0 + Rested);
        Assert.True(vm.CardPoolHeld);

        // Opening hides the popup, and its hide handler reports the pointer gone (held pool released) while the browser
        // opens: the browser must stay open (it used to close at once, on the turn it was not opened in yet).
        vm.UpdateCardPoolPointer(true, false, true, T0 + Rested);
        Assert.True(vm.CardPoolBrowserOpen);
        Assert.False(panel.IsVisible);
        Assert.False(vm.ShowCardPoolPanel);
    });

    [Fact]
    public void Both_view_models_open_the_browser_from_a_held_popup_even_when_the_hide_reenters()
    {
        foreach (var vm in Models())
        {
            vm.Show(Pool("TEST_GEN", 40));
            // As the WPF panel does: hiding reports the pointer gone, from inside the property change.
            vm.Changed += name =>
            {
                if (name == "ShowCardPoolPopup" && !vm.Popup)
                {
                    vm.Pointer(false, false, false, T0 + Rested);
                }
            };
            vm.Pointer(true, false, false, T0);
            vm.Pointer(true, false, false, T0 + Rested);
            vm.Pointer(true, false, true, T0 + Rested);
            Assert.True(vm.BrowserOpen, vm.Name);
        }
    }

    [Fact]
    public void A_held_pool_lets_go_on_a_left_press_and_when_the_pointer_leaves()
    {
        foreach (var vm in Models())
        {
            vm.Show(Pool("TEST_GEN", 40));
            // Crossing the popup quickly holds nothing; resting on it does.
            vm.Pointer(true, false, false, T0);
            vm.Pointer(true, false, false, T0 + TimeSpan.FromMilliseconds(100));
            Assert.False(vm.Held, vm.Name);
            vm.Pointer(true, false, false, T0 + Rested);
            Assert.True(vm.Held, vm.Name);

            // The hovered card is gone (played, or the linger ran out): the held popup stays while the player reads it.
            vm.Show(null);
            Assert.True(vm.Popup, vm.Name);

            // A left press is the game's (playing a card, aiming through the panel): the hold ends and the popup goes.
            vm.Pointer(true, true, false, T0 + Rested * 2);
            Assert.False(vm.Held, vm.Name);
            Assert.False(vm.Popup, vm.Name);

            // Resting with the left button held (dragging a card over it) never holds.
            vm.Show(Pool("TEST_GEN", 40));
            vm.Pointer(true, true, false, T0);
            vm.Pointer(true, true, false, T0 + Rested * 3);
            Assert.False(vm.Held, vm.Name);

            // Held, then the pointer leaves: the popup goes with the pool.
            vm.Pointer(true, false, false, T0 + Rested * 3);
            vm.Pointer(true, false, false, T0 + Rested * 5);
            Assert.True(vm.Held, vm.Name);
            vm.Show(null);
            vm.Pointer(false, false, false, T0 + Rested * 6);
            Assert.False(vm.Popup, vm.Name);
        }
    }

    [Fact]
    public void The_popup_shows_averages_on_one_line_and_the_most_played_cards_as_rows()
    {
        var previous = Loc.Instance.Language;
        var culture = CultureInfo.CurrentCulture;
        try
        {
            Loc.Instance.SetLanguage(Loc.English);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            var cards = new List<PoolCard>
            {
                Card("A", "SPELL", 2, null, null),
                Card("B", "MINION", 4, 3, 5),
                Card("C", "MINION", 9, 6, 6),
                Card("D", "WEAPON", 3, 4, 2),
            };
            var meta = new Dictionary<string, int> { ["B"] = 300, ["C"] = 100 };
            foreach (var vm in Models())
            {
                vm.Show(View("TEST_GEN", cards, meta));
                // Mana over all 4 cards, attack over the minions and the weapon, health over the minions.
                Assert.Equal([("Mana", "4.5"), ("Attack", "4.3"), ("Health", "5.5")], vm.Averages());
                // The whole name and the share of the pool's meta-deck games.
                Assert.Equal([("B", "Card B with a long name", 4, "75%"), ("C", "Card C with a long name", 9, "25%")], vm.Top());

                // Spells only: no attack or health.
                vm.Show(View("SPELLS", [Card("S1", "SPELL", 1, null, null), Card("S2", "SPELL", 2, null, null)], new Dictionary<string, int>()));
                Assert.Equal([("Mana", "1.5")], vm.Averages());
                Assert.Empty(vm.Top());
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            Loc.Instance.SetLanguage(previous);
        }
    }

    [Fact]
    public void A_this_game_list_shows_on_its_own_or_above_the_pool_in_both_view_models()
    {
        var previous = Loc.Instance.Language;
        try
        {
            Loc.Instance.SetLanguage(Loc.English);
            var list = new MatchListView("JAIL_940", "Undeath Sentence", "Your minions that died · Deathrattle", 3,
                [new MatchListRow("DR_1", "Loot Hoarder", 2, "COMMON", 2), new MatchListRow("DR_2", "Abomination", 5, "RARE", 1)]);
            foreach (var vm in Models())
            {
                // No pool: the popup is the list, its header names it and the card.
                vm.Show(null, list);
                Assert.True(vm.Popup, vm.Name);
                Assert.Equal(("Your minions that died · Deathrattle: 3", "Undeath Sentence", ""), vm.ListHeader());
                Assert.Equal([("DR_1", "×2"), ("DR_2", "")], vm.ListRows());

                // With the card's own pool: the list sits above it under its own label.
                vm.Show(Pool("JAIL_940", 12), list);
                Assert.Equal(("Pool of 12 cards", "Generator JAIL_940", "Your minions that died · Deathrattle: 3"), vm.ListHeader());

                // Nothing died yet: an empty list says so.
                vm.Show(null, list with { Total = 0, Rows = [] });
                Assert.Empty(vm.ListRows());
                Assert.Equal("None this game yet", vm.ListNote());

                // Held while the list is read, then gone with the pointer.
                vm.Pointer(true, false, false, T0);
                vm.Pointer(true, false, false, T0 + Rested);
                vm.Show(null);
                Assert.True(vm.Popup, vm.Name);
                vm.Pointer(false, false, false, T0 + Rested * 2);
                Assert.False(vm.Popup, vm.Name);
            }
        }
        finally
        {
            Loc.Instance.SetLanguage(previous);
        }
    }

    [Fact]
    public void The_wpf_popup_lays_out_a_this_game_list() => OnSta(() =>
    {
        var vm = new OverlayViewModel();
        var panel = new CardPoolPanel { Anchored = true, DataContext = vm };
        panel.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(OverlayViewModel.ShowCardPoolPopup)) { Converter = new BooleanToVisibilityConverter() });
        var root = new Canvas { Width = 1920, Height = 1080 };
        root.Children.Add(panel);
        using var source = new HwndSource(new HwndSourceParameters("pool popup test", 1920, 1080) { WindowStyle = 0 }) { RootVisual = root };
        var list = new MatchListView("JAIL_940", "Undeath Sentence", "Your minions that died · Deathrattle", 2, [new MatchListRow("DR_1", "Loot Hoarder", 2, "COMMON", 2)]);
        vm.Update(Snapshot(null, list), CardDb.Empty, new AppSettings(), null, null);
        root.UpdateLayout();

        Assert.True(panel.IsVisible);
        // Header, one row, the bottom space: a short tooltip.
        Assert.InRange(panel.ActualHeight, 60, 160);
    });

    [Fact]
    public void An_excavate_pool_shows_the_next_tier_as_rows_and_the_others_on_a_line_each_in_both_view_models()
    {
        var previous = Loc.Instance.Language;
        try
        {
            Loc.Instance.SetLanguage(Loc.English);
            // Rock in a Death Knight's hand, one Excavate done: the next treasure is rare.
            var view = Excavate(new ExcavateState(2, 4));
            foreach (var vm in Models())
            {
                vm.Show(view);
                Assert.True(vm.Popup, vm.Name);
                var (title, next, rows, tiers, sections) = vm.Excavate();
                Assert.Equal("Excavate: tier 2 of 4", title);
                Assert.Equal("Next treasure · rare", next);
                Assert.Equal(Hearthpulse.Core.CardPool.Excavate.TierTreasures(2, "DEATHKNIGHT").Select(t => t.Id).Order(), rows.Order());
                Assert.Equal(["T3", "T4", "T1"], tiers.Select(t => t.Label));
                Assert.Equal("The Azerite Rat", tiers[1].Names);
                Assert.False(sections, vm.Name);
                Assert.Empty(vm.Averages());

                // A pool from a deck row (no game): every tier on its line, no next one.
                vm.Show(Excavate(null));
                (title, next, rows, tiers, _) = vm.Excavate();
                Assert.Equal("Excavate: 4 tiers", title);
                Assert.Equal(string.Empty, next);
                Assert.Empty(rows);
                Assert.Equal(["T1", "T2", "T3", "T4"], tiers.Select(t => t.Label));
            }
        }
        finally
        {
            Loc.Instance.SetLanguage(previous);
        }
    }

    [Fact]
    public void The_wpf_popup_lays_out_an_excavate_pool() => OnSta(() =>
    {
        var vm = new OverlayViewModel();
        var panel = new CardPoolPanel { Anchored = true, DataContext = vm };
        panel.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(OverlayViewModel.ShowCardPoolPopup)) { Converter = new BooleanToVisibilityConverter() });
        var root = new Canvas { Width = 1920, Height = 1080 };
        root.Children.Add(panel);
        using var source = new HwndSource(new HwndSourceParameters("pool popup test", 1920, 1080) { WindowStyle = 0 }) { RootVisual = root };
        vm.Update(Snapshot(Excavate(new ExcavateState(1, 4))), CardDb.Empty, new AppSettings(), null, null);
        root.UpdateLayout();

        Assert.True(panel.IsVisible);
        // Header, six rows of the next tier, three quiet lines and the hint: no taller than a pool with its top cards.
        Assert.InRange(panel.ActualHeight, 200, 360);
    });

    private static CardPoolView Excavate(ExcavateState? state) =>
        new CardPoolEngine(CardPoolCatalog.Empty, CardDb.Empty)
            .Build("WW_001t", new PoolContext("FT_WILD", "DEATHKNIGHT", "WARRIOR") { Excavate = state }, PoolTrigger.Hover)!;

    /// <summary>The same calls on the WPF and the Avalonia build of the shared view model.</summary>
    private sealed class Model
    {
        public required string Name { get; init; }
        public required Action<CardPoolView?, MatchListView?> ShowWith { get; init; }
        public required Action<bool, bool, bool, DateTime> Pointer { get; init; }
        public required Func<bool> HeldNow { get; init; }
        public required Func<bool> BrowserNow { get; init; }
        public required Func<bool> PopupNow { get; init; }
        public required Func<IReadOnlyList<(string, string)>> Averages { get; init; }
        public required Func<IReadOnlyList<(string, string, int, string)>> Top { get; init; }
        public required Func<(string, string, string)> ListHeader { get; init; }
        public required Func<IReadOnlyList<(string, string)>> ListRows { get; init; }
        public required Func<string> ListNote { get; init; }
        public required Func<(string Title, string Next, IReadOnlyList<string> Rows, IReadOnlyList<(string Label, string Names)> Tiers, bool Sections)> Excavate { get; init; }
        public event Action<string?>? Changed;

        public void Show(CardPoolView? pool, MatchListView? list = null) => ShowWith(pool, list);

        public bool Held => HeldNow();
        public bool BrowserOpen => BrowserNow();
        public bool Popup => PopupNow();

        public void Raise(string? name) => Changed?.Invoke(name);
    }

    private static IEnumerable<Model> Models()
    {
        var wpf = new OverlayViewModel();
        var a = new Model
        {
            Name = "WPF",
            ShowWith = (pool, list) => wpf.Update(Snapshot(pool, list), CardDb.Empty, new AppSettings(), null, null),
            Pointer = wpf.UpdateCardPoolPointer,
            HeldNow = () => wpf.CardPoolHeld,
            BrowserNow = () => wpf.CardPoolBrowserOpen,
            PopupNow = () => wpf.ShowCardPoolPopup,
            Averages = () => wpf.CardPoolAverages.Select(x => (x.Label, x.Value)).ToList(),
            Top = () => wpf.CardPoolTopCards.Select(x => (x.CardId, x.Name, x.Cost, x.ShareText)).ToList(),
            ListHeader = () => (wpf.CardPoolTitle, wpf.CardPoolPhrase, wpf.CardPoolListTitle),
            ListRows = () => wpf.CardPoolList.Select(x => (x.CardId, x.CountText)).ToList(),
            ListNote = () => wpf.CardPoolListNote,
            Excavate = () => (wpf.CardPoolTitle, wpf.CardPoolExcavateNext, wpf.CardPoolExcavateRows.Select(r => r.CardId).ToList(),
                wpf.CardPoolExcavateTiers.Select(t => (t.Label, t.Names)).ToList(), wpf.ShowCardPoolPanelSections),
        };
        ((INotifyPropertyChanged)wpf).PropertyChanged += (_, e) => a.Raise(e.PropertyName);
        yield return a;

        var ava = new AvaOverlay();
        var b = new Model
        {
            Name = "Avalonia",
            ShowWith = (pool, list) => ava.Update(Snapshot(pool, list), CardDb.Empty, new AppSettings(), null, null),
            Pointer = ava.UpdateCardPoolPointer,
            HeldNow = () => ava.CardPoolHeld,
            BrowserNow = () => ava.CardPoolBrowserOpen,
            PopupNow = () => ava.ShowCardPoolPopup,
            Averages = () => ava.CardPoolAverages.Select(x => (x.Label, x.Value)).ToList(),
            Top = () => ava.CardPoolTopCards.Select(x => (x.CardId, x.Name, x.Cost, x.ShareText)).ToList(),
            ListHeader = () => (ava.CardPoolTitle, ava.CardPoolPhrase, ava.CardPoolListTitle),
            ListRows = () => ava.CardPoolList.Select(x => (x.CardId, x.CountText)).ToList(),
            ListNote = () => ava.CardPoolListNote,
            Excavate = () => (ava.CardPoolTitle, ava.CardPoolExcavateNext, ava.CardPoolExcavateRows.Select(r => r.CardId).ToList(),
                ava.CardPoolExcavateTiers.Select(t => (t.Label, t.Names)).ToList(), ava.ShowCardPoolPanelSections),
        };
        ((INotifyPropertyChanged)ava).PropertyChanged += (_, e) => b.Raise(e.PropertyName);
        yield return b;
    }

    private static LiveSnapshot Snapshot(CardPoolView? pool, MatchListView? list = null)
    {
        var constructed = new ConstructedView(5, null, false, "HERO_03", "ROGUE", "HERO_01", "WARRIOR", null, [], 0, 0, [], [], 0, 0, 0, null, null)
        {
            CardPool = pool,
            MatchList = list,
            CardPoolAnchor = pool is null && list is null ? null : new CardPoolAnchor(PoolAnchorZone.Hand, 1, 5),
        };
        return LiveSnapshot.Empty with { Scene = "GAMEPLAY", HearthstoneRunning = true, InGame = true, GameType = "GT_RANKED", Constructed = constructed };
    }

    private static PoolCard Card(string id, string type, int cost, int? attack, int? health) =>
        new(id, 0, $"Card {id} with a long name", string.Empty, type, "COMMON", cost, attack, health, ["MAGE"], [], null, [], [], "CORE", true);

    private static CardPoolView Pool(string id, int count) =>
        View(id, Enumerable.Range(0, count).Select(i => i % 3 == 0 ? Card($"{id}_{i}", "SPELL", i % 9, null, null) : Card($"{id}_{i}", "MINION", i % 9, i % 7, 1 + (i % 8))).ToList(), new Dictionary<string, int>());

    private static CardPoolView View(string id, IReadOnlyList<PoolCard> cards, IReadOnlyDictionary<string, int> meta)
    {
        var (cost, attack, health) = PoolStats.Histograms(cards);
        var entries = cards.Select(c => new PoolEntry(c.Id, c.Name, c.Cost, c.Attack, c.Health, c.Type, c.Rarity, "MAGE", meta.GetValueOrDefault(c.Id))).ToList();
        return new CardPoolView(id, "Generator " + id, PoolTrigger.Hover, "Discover a minion", "Mage + Neutral · Standard", cards.Count, cost, attack, health, [], null, null, entries);
    }

    private static void OnSta(Action test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                if (app.Resources.MergedDictionaries.Count == 0)
                {
                    app.Resources.MergedDictionaries.Add(ThemeEngine.Build(ThemePalette.Resolve("Tavern", 0, 0, 1)));
                }

                test();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new InvalidOperationException(failure.Message, failure);
        }
    }
}
