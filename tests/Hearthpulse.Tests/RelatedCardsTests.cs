using Hearthpulse.Core.CardPool;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Memory;
using Hearthpulse.Core.Power;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Tracking;
using static Hearthpulse.Tests.EctoplasmListTests;

namespace Hearthpulse.Tests;

/// <summary>
/// A card in hand's related cards (the tokens it makes, the cards it names) and the Graveyard panel's dead minions;
/// synthetic card data and catalog, synthetic Power.log lines.
/// </summary>
public sealed class RelatedCardsTests
{
    private static readonly CardDb Cards = CardDb.Parse("enUS", new MemoryStream("""
        [
          {"id":"CS2_101","dbfId":1,"name":"Reinforce","type":"HERO_POWER","set":"CORE","collectible":false},
          {"id":"GEN_1","dbfId":10,"name":"Muster for Battle","type":"SPELL","cost":3,"set":"TGT","collectible":true},
          {"id":"GEN_1t","dbfId":11,"name":"Silver Hand Recruit","type":"MINION","cost":1,"set":"TGT"},
          {"id":"GEN_1t2","dbfId":12,"name":"Light's Justice","type":"WEAPON","cost":1,"set":"TGT"},
          {"id":"GEN_1e","dbfId":13,"name":"Muster buff","type":"ENCHANTMENT","set":"TGT"},
          {"id":"GEN_1t3","dbfId":14,"name":"Other set token","type":"MINION","cost":1,"set":"OTHER"},
          {"id":"CORE_GEN_1","dbfId":15,"name":"Muster for Battle","type":"SPELL","cost":3,"set":"CORE","collectible":true},
          {"id":"SHUF_1","dbfId":20,"name":"Shuffler","type":"MINION","cost":2,"set":"X","collectible":true},
          {"id":"FIRE_1","dbfId":21,"name":"Fireball","type":"SPELL","cost":4,"set":"X","collectible":true},
          {"id":"CHARGE_1","dbfId":22,"name":"Charge","type":"SPELL","cost":1,"set":"X","collectible":true},
          {"id":"PLAIN","dbfId":30,"name":"Chillwind Yeti","type":"MINION","cost":4,"set":"X","collectible":true},
          {"id":"BE_017","dbfId":128389,"name":"N'Zoth, Dream's End","type":"MINION","cost":10,"set":"BE","collectible":true},
          {"id":"BE_017t3","dbfId":128390,"name":"Ability Three","type":"SPELL","cost":0,"set":"BE"},
          {"id":"BE_017t4","dbfId":128391,"name":"Ability Four","type":"SPELL","cost":0,"set":"BE"},
          {"id":"BE_017t5","dbfId":128392,"name":"Ability Five","type":"SPELL","cost":0,"set":"BE"},
          {"id":"BE_017t6","dbfId":128393,"name":"Ability Six","type":"SPELL","cost":0,"set":"BE"},
          {"id":"BE_017t6e","dbfId":128394,"name":"Ability Six buff","type":"ENCHANTMENT","set":"BE"},
          {"id":"EDR_872","dbfId":40,"name":"Spark of Life","type":"SPELL","cost":2,"set":"EDR","collectible":true},
          {"id":"EDR_872A","dbfId":41,"name":"Spark A","type":"SPELL","cost":2,"set":"EDR"},
          {"id":"EDR_872B","dbfId":42,"name":"Spark B","type":"SPELL","cost":2,"set":"EDR"},
          {"id":"LOE_115","dbfId":50,"name":"Raven Idol","type":"SPELL","cost":0,"set":"LOE","collectible":true},
          {"id":"LOE_115a","dbfId":51,"name":"Break Free","type":"SPELL","cost":0,"set":"LOE"},
          {"id":"LOE_115b","dbfId":52,"name":"Awakened","type":"SPELL","cost":0,"set":"LOE"},
          {"id":"Core_LOE_115","dbfId":53,"name":"Raven Idol","type":"SPELL","cost":0,"set":"CORE","collectible":true},
          {"id":"BE_013","dbfId":80,"name":"C'Thun, the Shattered","type":"MINION","cost":10,"set":"BE","collectible":true},
          {"id":"BE_013t3","dbfId":81,"name":"Eye of C'Thun","type":"SPELL","cost":5,"text":"Deal 7 damage.","set":"BE"},
          {"id":"BE_013t6","dbfId":82,"name":"Eye of C'Thun","type":"SPELL","cost":5,"text":"Deal 7 damage.","set":"BE"},
          {"id":"BE_013t4","dbfId":83,"name":"Eye of C'Thun","type":"SPELL","cost":5,"text":"Deal 10 damage.","set":"BE"},
          {"id":"BE_013t5","dbfId":84,"name":"Body of C'Thun","type":"SPELL","cost":5,"text":"Deal 7 damage.","set":"BE"},
          {"id":"BIG_1","dbfId":60,"name":"Many Tokens","type":"SPELL","cost":5,"set":"BIG","collectible":true},
          {"id":"BIG_1t1","dbfId":61,"name":"T1","type":"MINION","cost":1,"set":"BIG"},
          {"id":"BIG_1t2","dbfId":62,"name":"T2","type":"MINION","cost":1,"set":"BIG"},
          {"id":"BIG_1t3","dbfId":63,"name":"T3","type":"MINION","cost":1,"set":"BIG"},
          {"id":"BIG_1t4","dbfId":64,"name":"T4","type":"MINION","cost":1,"set":"BIG"},
          {"id":"BIG_1t5","dbfId":65,"name":"T5","type":"MINION","cost":1,"set":"BIG"},
          {"id":"BIG_1t6","dbfId":66,"name":"T6","type":"MINION","cost":1,"set":"BIG"},
          {"id":"BIG_1t7","dbfId":67,"name":"T7","type":"MINION","cost":1,"set":"BIG"},
          {"id":"BIG_1t8","dbfId":68,"name":"T8","type":"MINION","cost":1,"set":"BIG"},
          {"id":"BIG_1t9","dbfId":69,"name":"T9","type":"MINION","cost":1,"set":"BIG"},
          {"id":"BIG_1t10","dbfId":70,"name":"T10","type":"MINION","cost":1,"set":"BIG"},
          {"id":"BIG_1t11","dbfId":71,"name":"T11","type":"MINION","cost":1,"set":"BIG"},
          {"id":"BIG_1t12","dbfId":72,"name":"T12","type":"MINION","cost":1,"set":"BIG"},
          {"id":"BIG_1t13","dbfId":73,"name":"T13","type":"MINION","cost":1,"set":"BIG"}
        ]
        """u8.ToArray()));

    private static PoolCard Pool(string id, string name, string text) =>
        new(id, 0, name, text, "SPELL", "COMMON", 1, null, null, [], [], null, [], [], "X", true);

    private static readonly CardPoolCatalog Catalog = new(
    [
        Pool("SHUF_1", "Shuffler", "<b>Battlecry:</b> Shuffle a Fireball into your deck. <b>Charge</b>"),
        Pool("FIRE_1", "Fireball", "Deal $6 damage."),
        Pool("CHARGE_1", "Charge", "Give a minion <b>Charge</b>."),
        Pool("PLAIN", "Chillwind Yeti", "A Fireballer's tale."),
        Pool("GEN_1", "Muster for Battle", "Summon three 1/1 Silver Hand Recruits. Equip a 1/4 weapon."),
    ], "test", DateTime.UtcNow);

    [Fact]
    public void A_cards_tokens_by_id_are_its_related_cards_also_for_its_core_reprint()
    {
        var list = RelatedCards.Of("GEN_1", Cards, Catalog)!;

        Assert.Equal(Loc.T("Related.Title"), list.Title);
        Assert.Equal(["GEN_1t", "GEN_1t2"], list.Rows.Select(r => r.CardId).Order(StringComparer.Ordinal));
        Assert.All(list.Rows, r => Assert.Equal(1, r.Count));
        Assert.Equal(["GEN_1t", "GEN_1t2"], RelatedCards.Of("CORE_GEN_1", Cards, Catalog)!.Rows.Select(r => r.CardId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_card_named_in_the_text_is_related_but_not_a_bold_keyword_or_part_of_a_word()
    {
        var list = RelatedCards.Of("SHUF_1", Cards, Catalog)!;

        Assert.Equal(["FIRE_1"], list.Rows.Select(r => r.CardId));
        Assert.Null(RelatedCards.Of("PLAIN", Cards, Catalog));
        Assert.Null(RelatedCards.Of("CHARGE_1", Cards, Catalog));
        Assert.Null(RelatedCards.Of("NOPE", Cards, Catalog));
    }

    /// <summary>
    /// TASK-2026-11-09 AC7: a card that looks back at this game shows that list and, beside it, its related cards
    /// (N'Zoth's four abilities, not the enchantment); a card without a rule has its related cards as its list.
    /// </summary>
    [Fact]
    public void The_this_game_list_and_related_cards_show_together()
    {
        var (game, tracker) = Play([[.. Minion(40, "PLAIN", 1), .. Minion(41, "PLAIN", 1)]]);
        var rule = new MatchRule(MatchEvent.Summoned, MatchSide.Friendly);
        MatchListView? Related(string id) => RelatedCards.Of(id, Cards, Catalog);

        var (list, related) = GameSession.HoverList("BE_017", rule, game, tracker.Counters, Cards, Related);
        Assert.Equal(MatchLists.Title(rule), list!.Title);
        Assert.Equal(Loc.T("Related.Title"), related!.Title);
        Assert.Equal(["BE_017t3", "BE_017t4", "BE_017t5", "BE_017t6"], related.Rows.Select(r => r.CardId).Order(StringComparer.Ordinal));
        Assert.Equal(4, related.Total);

        var (withoutRule, none) = GameSession.HoverList("GEN_1", null, game, tracker.Counters, Cards, Related);
        Assert.Equal(Loc.T("Related.Title"), withoutRule!.Title);
        Assert.Null(none);

        // A rule and no related cards: the rule's list alone.
        var (onlyRule, noRelated) = GameSession.HoverList("PLAIN", rule, game, tracker.Counters, Cards, Related);
        Assert.Equal(MatchLists.Title(rule), onlyRule!.Title);
        Assert.Null(noRelated);
    }

    /// <summary>AC8: Choose One options with capital suffixes and a Core_ reprint in another case are tokens too.</summary>
    [Theory]
    [InlineData("BE_017", "BE_017t3 BE_017t4 BE_017t5 BE_017t6")]
    [InlineData("EDR_872", "EDR_872A EDR_872B")]
    [InlineData("LOE_115", "LOE_115a LOE_115b")]
    [InlineData("Core_LOE_115", "LOE_115a LOE_115b")]
    [InlineData("CORE_GEN_1", "GEN_1t GEN_1t2")]
    public void Each_token_id_scheme_gives_the_related_cards(string cardId, string expected)
    {
        var list = RelatedCards.Of(cardId, Cards, Catalog)!;

        Assert.Equal(expected.Split(' '), list.Rows.Select(r => r.CardId).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Coordinator 2026-11-09 (design.md question 4): related cards with the same name and the same text are one row
    /// (C'Thun's two identical Eyes); the same name with another text, or another name with the same text, stay apart.
    /// </summary>
    [Fact]
    public void Related_cards_with_the_same_name_and_text_are_one_row()
    {
        var list = RelatedCards.Of("BE_013", Cards, Catalog)!;

        Assert.Equal(["BE_013t3", "BE_013t4", "BE_013t5"], list.Rows.Select(r => r.CardId).Order(StringComparer.Ordinal));
        Assert.Equal(3, list.Total);
        Assert.All(list.Rows, r => Assert.Equal(1, r.Count));
    }

    /// <summary>AC9: more than 12 is no list; no catalog still gives the tokens; no card data gives nothing.</summary>
    [Fact]
    public void Too_many_no_catalog_and_no_card_data()
    {
        Assert.Null(RelatedCards.Of("BIG_1", Cards, Catalog));
        Assert.Equal(["GEN_1t", "GEN_1t2"], RelatedCards.Of("GEN_1", Cards, CardPoolCatalog.Empty)!.Rows.Select(r => r.CardId).Order(StringComparer.Ordinal));
        // The named Fireball needs the catalog's English text.
        Assert.Null(RelatedCards.Of("SHUF_1", Cards, CardPoolCatalog.Empty));
        Assert.Null(RelatedCards.Of("BE_017", CardDb.Empty, Catalog));
    }

    /// <summary>
    /// AC8/AC10: the session gives any card's related cards by id alone (no game state), for card previews; empty for a
    /// card without them or with more than 12.
    /// </summary>
    [Fact]
    public void The_session_gives_any_cards_related_cards_by_id_alone()
    {
        using var memory = new HearthstoneMemory();
        var session = new GameSession(HistoryStore.InMemory(), memory) { Cards = Cards };
        session.SetCardPoolCatalog(Catalog);

        Assert.Equal(["BE_017t3", "BE_017t4", "BE_017t5", "BE_017t6"], session.RelatedCardsOf("BE_017").Select(r => r.CardId).Order(StringComparer.Ordinal));
        Assert.Equal(["Ability Five", "Ability Four", "Ability Six", "Ability Three"], session.RelatedCardsOf("BE_017").Select(r => r.Name));
        Assert.Equal(["FIRE_1"], session.RelatedCardsOf("SHUF_1").Select(r => r.CardId));
        Assert.Empty(session.RelatedCardsOf("BIG_1"));
        Assert.Empty(session.RelatedCardsOf("PLAIN"));
        Assert.Empty(session.RelatedCardsOf(""));
        Assert.DoesNotContain(typeof(GameSession).GetMethod(nameof(GameSession.RelatedCardsOf))!.GetParameters(), p => p.ParameterType == typeof(GameState));

        // Other card data (a language switch loads it): the cache is built again from it.
        session.Cards = CardDb.Empty;
        Assert.Empty(session.RelatedCardsOf("BE_017"));
    }

    /// <summary>AC4: a list only for a card in your own hand; the opponent's hand and the board get none.</summary>
    [Fact]
    public void Related_cards_show_only_for_a_card_in_your_own_hand()
    {
        var (game, _) = Play([[.. Card(41, "GEN_1", "HAND", 1, "SPELL"), .. Card(60, "SHUF_1", "HAND", 2, "MINION"), .. Minion(50, "PLAIN", 1)]]);
        var hover = new CardPoolTracker();

        hover.SetHovered("GEN_1", DateTime.UtcNow);
        Assert.Equal("GEN_1", hover.HoveredInHand(game));

        // The opponent's Shuffler has related cards, but it is not in your hand.
        Assert.NotNull(RelatedCards.Of("SHUF_1", Cards, Catalog));
        foreach (var notMine in new[] { "SHUF_1", "PLAIN" })
        {
            hover.SetHovered(notMine, DateTime.UtcNow);
            Assert.Null(hover.HoveredInHand(game));
        }
    }

    /// <summary>AC4: a minion that never stood on the board (from the deck or set aside straight to the graveyard) is no death.</summary>
    [Fact]
    public void A_minion_that_goes_to_the_graveyard_from_the_deck_or_set_aside_is_not_listed()
    {
        var (game, tracker) = Play(
        [
            [.. Card(60, "PLAIN", "DECK", 2, "MINION"), .. Card(61, "SHUF_1", "SETASIDE", 2, "MINION"), .. Minion(62, "PLAIN", 2)],
            ["DebugPrintPower() - TAG_CHANGE Entity=[entityName=X id=60 zone=DECK zonePos=0 cardId=PLAIN player=2] tag=ZONE value=GRAVEYARD "],
            ["DebugPrintPower() - TAG_CHANGE Entity=[entityName=X id=61 zone=SETASIDE zonePos=0 cardId=SHUF_1 player=2] tag=ZONE value=GRAVEYARD "],
        ]);
        Assert.Null(GraveyardView.Of(game, tracker.Counters, Cards));

        var (died, diedTracker) = Play([[.. Minion(62, "PLAIN", 2)], Die(62, "PLAIN", 2)]);
        Assert.Equal([("PLAIN", 1)], GraveyardView.Of(died, diedTracker.Counters, Cards)!.Opponent.Select(r => (r.CardId, r.Count)));
    }

    /// <summary>m3: a dead minion missing from the card data shows as "Unknown card", never its id.</summary>
    [Fact]
    public void An_unknown_dead_minion_is_named_unknown_card()
    {
        var row = Assert.Single(GraveyardView.Rows(["NOT_IN_DB_1"], Cards));
        Assert.Equal(Loc.T("Related.Unknown"), row.Name);
        Assert.NotEqual("Related.Unknown", row.Name);
        Assert.Equal("Неизвестная карта", Loc.Table(Loc.Russian)["Related.Unknown"]);
    }

    /// <summary>m4: the view is built again only when a side's death count changes.</summary>
    [Fact]
    public void The_graveyard_view_is_rebuilt_only_on_a_new_death()
    {
        var (game, tracker) = Play([[.. Minion(40, "PLAIN", 1), .. Minion(41, "SHUF_1", 2)], Die(40, "PLAIN", 1)]);
        var cache = new GraveyardView.Cache();

        var first = cache.Of(game, tracker.Counters, Cards);
        Assert.Same(first, cache.Of(game, tracker.Counters, Cards));

        tracker.Counters.For(2).Died.Add("SHUF_1");
        var second = cache.Of(game, tracker.Counters, Cards)!;
        Assert.NotSame(first, second);
        Assert.Equal(1, second.OpponentTotal);
    }

    [Fact]
    public void The_graveyard_lists_each_sides_dead_minions_and_nothing_else()
    {
        var (before, beforeTracker) = Play([[.. Minion(40, "PLAIN", 1)]]);
        Assert.Null(GraveyardView.Of(before, beforeTracker.Counters, Cards));

        var (game, tracker) = Play(
        [
            [.. Minion(40, "PLAIN", 1), .. Minion(41, "PLAIN", 1), .. Minion(42, "SHUF_1", 1), .. Minion(50, "SHUF_1", 2)],
            // A card in the opponent's hand that goes to the graveyard (discarded) is no death on the board.
            [.. Card(60, "PLAIN", "HAND", 2, "MINION")],
            [.. Die(40, "PLAIN", 1), .. Die(41, "PLAIN", 1), .. Die(50, "SHUF_1", 2)],
            ["DebugPrintPower() - TAG_CHANGE Entity=[entityName=X id=60 zone=HAND zonePos=1 cardId=PLAIN player=2] tag=ZONE value=GRAVEYARD "],
        ]);

        var view = GraveyardView.Of(game, tracker.Counters, Cards)!;

        Assert.Equal(2, view.LocalTotal);
        Assert.Equal([("PLAIN", 2)], view.Local.Select(r => (r.CardId, r.Count)));
        Assert.Equal(1, view.OpponentTotal);
        Assert.Equal([("SHUF_1", 1)], view.Opponent.Select(r => (r.CardId, r.Count)));
    }
}
