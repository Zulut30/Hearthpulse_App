using System.ComponentModel;
using System.Reflection;
using Hearthpulse.Core;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Replay;
using Hearthpulse.UI.Main;
using Hearthpulse.UI.Main.Match;

namespace Hearthpulse.Tests;

/// <summary>
/// The match page's tables as the views bind them (shared view model, Avalonia build): the table, its sides and cards
/// are the same objects from step to step and tell the bindings only what changed. Each in-place class mirrors a Core
/// record by hand, so the contract tests walk the record's properties: a property added to the record and forgotten
/// here fails them.
/// </summary>
public sealed class MatchTableModelTests : IDisposable
{
    private readonly string _folder = TestDataFolder.Scratch("hp-match-table-model");
    private readonly ReplayStore _store;

    public MatchTableModelTests() => _store = new ReplayStore(Path.Combine(_folder, "replays"));

    public void Dispose() => TestDataFolder.TryDelete(_folder);

    /// <summary>
    /// A Constructed game long enough for a feed of 200 rows: both sides play a minion, then each turn the side's minion
    /// hits the other one twice (its damage grows, nobody dies, the table's cards stay the same).
    /// </summary>
    internal static string[] LongGame(int turns = 90)
    {
        var events = new List<string>
        {
            "T 1 L 0", "E 1 L PLAY 0 HERO_08 HERO HEALTH=30", "E 2 O PLAY 0 HERO_01 HERO HEALTH=30",
            "S BEGIN_MULLIGAN", "S MAIN_READY",
            "V L RESOURCES 1", "B", "E 3 L PLAY 1 CS2_120 MINION ATK=1 HEALTH=900", "P 3 -",
            "T 2 O 30", "V O RESOURCES 2", "B", "E 4 O PLAY 1 CS2_182 MINION ATK=1 HEALTH=900", "P 4 -",
        };
        var damage = new Dictionary<int, int> { [3] = 0, [4] = 0 };
        for (var turn = 3; turn <= turns; turn++)
        {
            var local = turn % 2 == 1;
            var (attacker, target) = local ? (3, 4) : (4, 3);
            events.Add(FormattableString.Invariant($"T {turn} {(local ? 'L' : 'O')} {turn * 30}"));
            for (var hit = 0; hit < 2; hit++)
            {
                events.Add(FormattableString.Invariant($"A {attacker} {target}"));
                events.Add(FormattableString.Invariant($"V {target} DAMAGE {++damage[target]}"));
            }
        }

        events.Add("R WON");
        return [.. events];
    }

    private MatchViewModel Model()
    {
        Directory.CreateDirectory(_folder);
        return new MatchViewModel(new MainViewModel(new HistoryStore(Path.Combine(_folder, $"{Guid.NewGuid():N}.db")), new AppSettings(), TestDataFolder.App),
            (_, token) => Task.Delay(Timeout.Infinite, token));
    }

    private MatchPage Page(string[] events)
    {
        var name = _store.Save(MatchTimelineTests.Game(null).Key, ReplayFile.Write(new ReplayHeader(), events));
        var page = MatchPage.Load(MatchTimelineTests.Game(name), _store, CardDb.Empty, null);
        Assert.Null(page.Missing);
        return page;
    }

    [Fact]
    public void A_step_that_only_changes_a_minions_health_keeps_the_table_its_sides_and_every_card()
    {
        var model = Model();
        var page = Page(LongGame());
        model.Show(page);
        Assert.True(model.FeedRows.Count >= 200);
        var table = model.Table;
        var attacks = page.Replay!.Frames.Where(f => f.Action.Kind == ReplayActionKind.Attack).Select(f => f.Index).ToList();
        model.GoFrame.Execute(attacks[10]);
        var (top, bottom) = (table.Top, table.Bottom);
        var cards = Cards(table);
        var target = cards.Single(c => c.Targeted);
        var health = target.Health;
        var raised = Watch(cards);
        var sides = new List<string?>();
        top.PropertyChanged += (_, e) => sides.Add(e.PropertyName);
        bottom.PropertyChanged += (_, e) => sides.Add(e.PropertyName);

        // The next step is the same minion hit again: one more damage, the same cards on the table.
        model.GoFrame.Execute(attacks[11]);

        Assert.Same(table, model.Table);
        Assert.Same(top, table.Top);
        Assert.Same(bottom, table.Bottom);
        Assert.Equal(cards, Cards(table), ReferenceEqualityComparer.Instance);
        Assert.Equal(health - 1, target.Health);
        Assert.Equal(model.Step!.Bottom.Board.Concat(model.Step.Top.Board).Single(c => c.Targeted).Health, target.Health);
        Assert.Contains(nameof(MatchCardItem.Health), raised[target]);
        Assert.DoesNotContain(nameof(MatchCardItem.CardId), raised[target]);
        Assert.DoesNotContain(nameof(MatchCardItem.Attack), raised[target]);
        // The attacker, the heroes, the counters: nothing of theirs changed, so nothing was raised.
        Assert.All(raised.Where(r => !ReferenceEquals(r.Key, target)), r => Assert.Empty(r.Value));
        Assert.Empty(sides);
    }

    [Fact]
    public void The_table_shows_the_step_of_every_frame_from_either_side_and_is_emptied_with_the_page()
    {
        var model = Model();
        var page = Page(MatchTimelineTests.Events);
        model.Show(page);
        var table = model.Table;
        foreach (var bottomLocal in new[] { true, false })
        {
            if (!bottomLocal)
            {
                model.BottomOpponent = true;
            }

            for (var frame = 0; frame <= model.FrameMax; frame++)
            {
                var version = table.Version;
                var same = frame == model.Frame;
                model.GoFrame.Execute(frame);
                var step = page.Step(frame, bottomLocal ? ReplayPlayer.Local : ReplayPlayer.Opponent)!;
                Assert.Equal(step.Index, model.Step!.Index);
                Assert.True(table.HasStep);
                Assert.Equal((step.Index, step.Count, step.Turn, step.Action, step.Detail, step.LocalActs, step.ActorRef, step.TargetRef, step.IsAttack),
                    (table.Index, table.Count, table.Turn, table.Action, table.Detail, table.LocalActs, table.ActorRef, table.TargetRef, table.IsAttack));
                Assert.Equal(step.Choices, table.Choices);
                AssertSide(step.Top, table.Top);
                AssertSide(step.Bottom, table.Bottom);
                // The arrow layer looks for its ends again when the version moves: on every new step.
                Assert.True(same || table.Version > version);
            }
        }

        model.Clear();
        Assert.False(table.HasStep);
        Assert.Empty(Cards(table));
        Assert.Null(table.Top.Weapon);
        Assert.Equal(string.Empty, table.Action);
    }

    [Fact]
    public void The_battlegrounds_table_shows_every_step_and_keeps_the_cards_that_stay()
    {
        var model = Model();
        var (state, recorder, tracker) = BattlegroundsReplayTests.Play(BattlegroundsReplayTests.Game5th());
        var name = _store.Save("bg-table", recorder.Battlegrounds.Text(new ReplayHeader(), tracker.ReplayData(state)));
        var page = MatchPage.Load(new GameRecord
        {
            Key = "bg-table", StartedAt = new DateTime(2026, 10, 23, 10, 0, 0), EndedAt = new DateTime(2026, 10, 23, 10, 12, 0),
            Mode = "Battlegrounds", GameType = "GT_BATTLEGROUNDS", Placement = 5, Turns = 2, Replay = name,
        }, _store, CardDb.Empty, null);
        Assert.True(page.HasBattlegroundsSteps);
        model.Show(page);
        var table = model.BgTable;
        var (kept, combats, reused, tavern) = (0, 0, 0, false);
        var (offer, opponents) = (new List<MatchBgCardItem>(), new List<MatchBgCardItem>());
        for (var frame = 0; frame <= model.FrameMax; frame++)
        {
            var before = table.Board.ToList();
            model.GoFrame.Execute(frame);
            var step = page.BattlegroundsStep(frame)!;
            Assert.Equal(step.Index, model.BattlegroundsStep!.Index);
            Assert.Same(table, model.BgTable);
            Assert.Equal((step.Title, step.Action, step.Detail, step.Gold, step.ShopFrozen, step.ResultKey, step.InCombat, step.ShowShop, step.ShowEmptyBoard),
                (table.Title, table.Action, table.Detail, table.Gold, table.ShopFrozen, table.ResultKey, table.InCombat, table.ShowShop, table.ShowEmptyBoard));
            Assert.Equal((step.BeforeTavern, step.ChoicesOnTop, step.ChoicesBelow, step.PartnerFighting, step.Turn, step.Phase, step.Kind),
                (table.BeforeTavern, table.ChoicesOnTop, table.ChoicesBelow, table.PartnerFighting, table.Turn, table.Phase, table.Kind));
            Assert.Equal(step.Board, table.Board.Select(c => c.View));
            Assert.Equal(step.Hand, table.Hand.Select(c => c.View));

            // A row the step shows holds the step's cards; a hidden one keeps what it showed last (see below).
            if (step.ShowShop)
            {
                Assert.Equal(step.Shop, table.Shop.Select(c => c.View));
                if (!tavern && offer.Count > 0 && table.Shop.Count > 0)
                {
                    // The next tavern's offer stands on the cards of the last one: no row is built for it.
                    Assert.Same(offer[0], table.Shop[0]);
                    reused++;
                }

                offer = table.Shop.ToList();
            }
            else
            {
                // Hidden through the combat: the offer's cards are kept as they were.
                Assert.Equal(offer, table.Shop, ReferenceEqualityComparer.Instance);
            }

            if (step.InCombat)
            {
                Assert.Equal(step.OpponentBoard, table.OpponentBoard.Select(c => c.View));
                opponents = table.OpponentBoard.ToList();
                combats++;
            }
            else
            {
                Assert.Equal(opponents, table.OpponentBoard, ReferenceEqualityComparer.Instance);
            }

            tavern = step.ShowShop;
            Assert.Equal(step.Choices, table.Choices);
            Assert.Equal(step.Hero is null, table.Hero is null);
            Assert.Equal(step.Opponent is null, table.Opponent is null);
            if (step.Hero is { } hero)
            {
                Assert.Equal((hero.HeroCardId, hero.Name, hero.HealthText, hero.ArmorText, hero.HealthTip, hero.Tier),
                    (table.Hero!.HeroCardId, table.Hero.Name, table.Hero.HealthText, table.Hero.ArmorText, table.Hero.HealthTip, table.Hero.Tier));
                Assert.Equal(hero.HeroPowers, table.Hero.HeroPowers.Select(c => c.View));
                Assert.Equal(hero.Trinkets, table.Hero.Trinkets.Select(c => c.View));
            }

            // The list of the stretch's events: the rows the view binds are the model's, the mark at its index.
            Assert.Equal(model.Actions, model.ActionRows.Select(r => r.View));
            Assert.Equal(model.Actions.ToList().FindIndex(a => a.Current), model.ActionCurrentIndex);

            // A card that is still at its place with the same card id is the same object.
            for (var i = 0; i < Math.Min(before.Count, table.Board.Count); i++)
            {
                if (ReferenceEquals(before[i], table.Board[i]))
                {
                    kept++;
                }
            }
        }

        Assert.True(kept > 0, "no board card was kept from one step to the next");
        Assert.True(combats > 0 && reused > 0, "the game has a combat between two taverns");
        model.Clear();
        Assert.False(table.HasStep);
        Assert.Empty(table.Board);
        Assert.Empty(table.Shop);
        Assert.Empty(table.OpponentBoard);
        Assert.Null(table.Hero);
        Assert.Empty(model.ActionRows);
    }

    [Fact]
    public void A_card_bought_into_the_middle_of_a_row_inserts_one_item_and_keeps_the_others()
    {
        MatchBgCardView Card(string id, int health = 1) => new(id, id) { Health = health, ShowStats = true };
        var list = new System.Collections.ObjectModel.ObservableCollection<MatchBgCardItem>();
        Assert.True(MatchItems.Sync(list, [Card("A"), Card("B"), Card("C")]));
        var (a, b, c) = (list[0], list[1], list[2]);
        var changes = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        list.CollectionChanged += (_, e) => changes.Add(e.Action);

        Assert.True(MatchItems.Sync(list, [Card("A"), Card("X"), Card("B"), Card("C", health: 5)]));
        Assert.Equal([a, list[1], b, c], list, ReferenceEqualityComparer.Instance);
        Assert.Equal("X", list[1].CardId);
        Assert.Equal(5, c.Health);
        Assert.Equal([System.Collections.Specialized.NotifyCollectionChangedAction.Add], changes);

        // Sold from the middle: one item goes, the others stay.
        changes.Clear();
        Assert.True(MatchItems.Sync(list, [Card("A"), Card("B"), Card("C", health: 5)]));
        Assert.Equal([a, b, c], list, ReferenceEqualityComparer.Instance);
        Assert.Equal([System.Collections.Specialized.NotifyCollectionChangedAction.Remove], changes);

        // The same row again: nothing changes and nothing is told.
        changes.Clear();
        Assert.False(MatchItems.Sync(list, [Card("A"), Card("B"), Card("C", health: 5)]));
        Assert.Empty(changes);

        // Another card where one stood: its item shows the new card (no new row).
        Assert.True(MatchItems.Sync(list, [Card("A"), Card("Y"), Card("C", health: 5)]));
        Assert.Same(b, list[1]);
        Assert.Equal("Y", b.CardId);
        Assert.Empty(changes);
    }

    // ---- The in-place classes against the records they mirror.

    [Fact]
    public void A_card_item_mirrors_every_property_of_its_record_and_raises_the_one_that_changed() =>
        AssertMirrors(new MatchCardView("CS2_120", "River Crocolisk", false) { Ref = 3, Note = "Turn 2", Tooltip = "tip" }, v => new MatchCardItem(v), (item, v) => item.Update(v));

    [Fact]
    public void A_battlegrounds_card_item_mirrors_every_property_of_its_record_and_raises_the_one_that_changed() =>
        AssertMirrors(new MatchBgCardView("BGS_004", "Wrath Weaver") { Tooltip = "tip" }, v => new MatchBgCardItem(v), (item, v) => item.Update(v));

    [Fact]
    public void An_action_item_mirrors_every_property_of_its_record_and_raises_the_one_that_changed() =>
        AssertMirrors(new MatchActionRow(4, "i", "Bought", true, "BGS_004", false) { KeyReason = "why" }, v => new MatchActionItem(v), (item, v) => item.Update(v));

    [Fact]
    public void A_table_side_mirrors_every_property_of_its_record_and_raises_the_one_that_changed() =>
        AssertMirrors(new MatchSideView("You", true, "HERO_08", "Jaina", 30, 2, null, null, [], [], [], "3/5", "20") { HeroRef = 1, DeckCount = 20, HandCount = 4, SecretCount = 1 },
            v => Updated(new MatchTableSide(), s => s.Update(v)), (side, v) => side.Update(v));

    [Fact]
    public void A_battlegrounds_hero_mirrors_every_property_of_its_record_and_raises_the_one_that_changed() =>
        AssertMirrors(new MatchBgHeroView("TB_BaconShop_HERO_08", "Hagatha", 30, 5, 2), v => Updated(new MatchBgTableHero(), h => h.Update(v)), (hero, v) => hero.Update(v));

    [Fact]
    public void The_table_mirrors_every_property_of_its_step_and_raises_the_one_that_changed()
    {
        var side = new MatchSideView("You", true, "HERO_08", "Jaina", 30, 0, null, null, [], [], [], "1/1", "20");
        AssertMirrors(new MatchStepView(3, 20, "Turn 2", "Played", "detail", true, side, side) { ActorRef = 3, TargetRef = 4 },
            v => Updated(new MatchTable(), t => t.Update(v)), (table, v) => table.Update(v));
    }

    [Fact]
    public void The_battlegrounds_table_mirrors_every_property_of_its_step_and_raises_the_one_that_changed() =>
        AssertMirrors(new MatchBgStepView(3, 40, 2, MatchPhase.Shop, MatchStepKind.Buy, "Tavern 2", "Bought", "detail") { Gold = "3 / 4" },
            v => Updated(new MatchBgTable(), t => t.Update(v)), (table, v) => table.Update(v));

    private static T Updated<T>(T model, Action<T> update)
    {
        update(model);
        return model;
    }

    /// <summary>
    /// Every scalar property of the record (texts, numbers, flags, enums) has a property of the same name and value on
    /// the in-place object; changing one on the record and showing it raises that property, and the values are equal
    /// again (the worked-out ones too: "+2", tooltips, what follows from the phase).
    /// </summary>
    private static void AssertMirrors<TView, TModel>(TView view, Func<TView, TModel> create, Action<TModel, TView> update)
        where TView : class
        where TModel : INotifyPropertyChanged
    {
        var model = create(view);
        var scalars = typeof(TView).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => IsScalar(p.PropertyType)).ToList();
        Assert.NotEmpty(scalars);
        void Same(TView now)
        {
            foreach (var property in scalars)
            {
                var mirror = typeof(TModel).GetProperty(property.Name);
                Assert.True(mirror is not null, $"{typeof(TModel).Name} has no {property.Name}");
                Assert.True(Equals(property.GetValue(now), mirror.GetValue(model)), $"{typeof(TModel).Name}.{property.Name}");
            }
        }

        Same(view);
        var raised = new List<string?>();
        model.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        var clone = typeof(TView).GetMethod("<Clone>$")!;
        foreach (var property in scalars.Where(p => p.SetMethod is not null))
        {
            var changed = (TView)clone.Invoke(view, null)!;
            property.SetValue(changed, Other(property.GetValue(view), property.PropertyType));
            raised.Clear();
            update(model, changed);
            Assert.True(raised.Contains(property.Name), $"{typeof(TModel).Name} did not raise {property.Name}");
            Same(changed);

            // Back to the first view: raised again, and nothing is left of the change.
            raised.Clear();
            update(model, view);
            Assert.Contains(property.Name, raised);
            Same(view);
        }

        // The same view again raises nothing.
        raised.Clear();
        update(model, (TView)clone.Invoke(view, null)!);
        Assert.Empty(raised);
    }

    private static bool IsScalar(Type type) => type == typeof(string) || type == typeof(int) || type == typeof(bool) || type.IsEnum;

    private static object Other(object? value, Type type) => value switch
    {
        bool flag => !flag,
        int number => number + 3,
        string text => text + "!",
        null when type == typeof(string) => "Win",
        _ when type.IsEnum => Enum.GetValues(type).Cast<object>().First(v => !v.Equals(value)),
        _ => throw new InvalidOperationException($"no other value for {type}"),
    };

    private static List<MatchCardItem> Cards(MatchTable table) =>
        new[] { table.Top, table.Bottom }.SelectMany(s => s.Board.Concat(s.Hand).Concat(s.Secrets).Concat(new[] { s.Weapon, s.HeroPower }.OfType<MatchCardItem>())).ToList();

    private static Dictionary<MatchCardItem, List<string?>> Watch(IEnumerable<MatchCardItem> cards)
    {
        var raised = new Dictionary<MatchCardItem, List<string?>>(ReferenceEqualityComparer.Instance);
        foreach (var card in cards)
        {
            var names = raised[card] = [];
            card.PropertyChanged += (_, e) => names.Add(e.PropertyName);
        }

        return raised;
    }

    private static void AssertSide(MatchSideView side, MatchTableSide shown)
    {
        Assert.Equal((side.Label, side.IsLocal, side.HeroCardId, side.Hero, side.HealthText, side.ArmorText, side.HealthTip, side.Mana, side.Deck),
            (shown.Label, shown.IsLocal, shown.HeroCardId, shown.Hero, shown.HealthText, shown.ArmorText, shown.HealthTip, shown.Mana, shown.Deck));
        Assert.Equal((side.HeroRef, side.HeroActing, side.HeroTargeted, side.HealthChangeText, side.DeckCount, side.HandCount, side.SecretCount, side.DeckTip, side.HandTip, side.SecretTip),
            (shown.HeroRef, shown.HeroActing, shown.HeroTargeted, shown.HealthChangeText, shown.DeckCount, shown.HandCount, shown.SecretCount, shown.DeckTip, shown.HandTip, shown.SecretTip));
        Assert.Equal(side.Board, shown.Board.Select(c => c.View));
        Assert.Equal(side.Hand, shown.Hand.Select(c => c.View));
        Assert.Equal(side.Secrets, shown.Secrets.Select(c => c.View));
        Assert.Equal(side.Weapon, shown.Weapon?.View);
        Assert.Equal(side.HeroPower, shown.HeroPower?.View);
    }
}
