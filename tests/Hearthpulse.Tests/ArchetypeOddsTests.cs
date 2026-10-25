using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Constructed;
using Xunit;

namespace Hearthpulse.Tests;

/// <summary>The live archetype odds: which archetype the opponent's face-up cards point to, and what else it may hold.</summary>
public sealed class ArchetypeOddsTests
{
    private static readonly CardDb Cards = CardDb.Parse("enUS", new MemoryStream("""
        [
          {"id":"HERO_09","dbfId":813,"name":"Anduin","type":"HERO","cardClass":"PRIEST"},
          {"id":"P_A","dbfId":2001,"name":"Alpha","type":"SPELL","cost":1,"cardClass":"PRIEST"},
          {"id":"P_B","dbfId":2002,"name":"Bravo","type":"MINION","cost":2,"cardClass":"PRIEST"},
          {"id":"P_C","dbfId":2003,"name":"Charlie","type":"MINION","cost":3,"cardClass":"PRIEST"},
          {"id":"P_D","dbfId":2004,"name":"Delta","type":"MINION","cost":4,"cardClass":"PRIEST"},
          {"id":"P_E","dbfId":2005,"name":"Echo","type":"SPELL","cost":5,"cardClass":"PRIEST"},
          {"id":"P_F","dbfId":2006,"name":"Foxtrot","type":"SPELL","cost":6,"cardClass":"PRIEST"},
          {"id":"N_T","dbfId":3001,"name":"Tech","type":"MINION","cost":2,"cardClass":"NEUTRAL"}
        ]
        """u8.ToArray()));

    private static MetaArchetype Archetype(int id, string name, int games, params string[] core) =>
        new(id, name, name, "PRIEST", games, games / 2, 0.5, 0.05, 0, 0, 0, 0, core, []);

    private static MetaDeck Deck(int archetype, int games, params int[] dbfs) =>
        new(Encode(dbfs), archetype, "x", "PRIEST", games, games / 2, 0.5, DateTime.UtcNow);

    /// <summary>Quest Priest runs A, B, C (and D in half its lists); Control Priest runs D, E, F (and B in some).</summary>
    private static ConstructedMeta Meta() => new("standard", "all", "past-7", DateTime.UtcNow, 3000,
        [Archetype(1, "Quest Priest", 1000), Archetype(2, "Control Priest", 2000)],
        [
            Deck(1, 500, 2001, 2002, 2003, 2004),
            Deck(1, 500, 2001, 2002, 2003),
            Deck(2, 1500, 2004, 2005, 2006),
            Deck(2, 500, 2002, 2004, 2005, 2006),
        ]);

    [Fact]
    public void Shown_cards_move_the_odds_to_the_archetype_that_runs_them()
    {
        var view = ArchetypeOdds.Build(Meta(), "FT_STANDARD", "PRIEST", ["P_A", "P_C"], Cards);

        Assert.NotNull(view);
        Assert.Equal("Quest Priest", view!.Best.Key);
        Assert.True(view.Best.Chance > 0.95, $"chance {view.Best.Chance}");
        Assert.Single(view.Archetypes); // Control Priest is far below the 3% an alternative needs.
        Assert.Equal(2, view.SeenCards);

        // What else: Bravo is in every Quest list; Delta in half; shown cards are never listed.
        Assert.Contains(view.Cards, c => c.CardId == "P_B" && c.Chance > 0.95);
        Assert.Contains(view.Cards, c => c.CardId == "P_D" && c.Chance is > 0.45 and < 0.6);
        Assert.DoesNotContain(view.Cards, c => c.CardId is "P_A" or "P_C");
        Assert.Equal(view.Cards.OrderBy(c => c.Cost).Select(c => c.CardId), view.Cards.Select(c => c.CardId));
    }

    [Fact]
    public void A_card_both_run_keeps_the_more_played_archetype_ahead_and_a_tech_card_rules_none_out()
    {
        var shared = ArchetypeOdds.Build(Meta(), "FT_STANDARD", "PRIEST", ["P_D"], Cards)!;
        Assert.Equal("Control Priest", shared.Best.Key);

        // A card no list runs is weak evidence for every archetype alike: the order stays the meta's.
        var tech = ArchetypeOdds.Build(Meta(), "FT_STANDARD", "PRIEST", ["N_T"], Cards)!;
        Assert.Equal("Control Priest", tech.Best.Key);
        Assert.Equal(2000.0 / 3000, tech.Best.Chance, 3);
    }

    [Fact]
    public void Nothing_without_a_shown_card_a_fitting_meta_or_archetypes_of_the_class()
    {
        Assert.Null(ArchetypeOdds.Build(Meta(), "FT_STANDARD", "PRIEST", [], Cards));
        Assert.Null(ArchetypeOdds.Build(Meta(), "FT_WILD", "PRIEST", ["P_A"], Cards));
        Assert.Null(ArchetypeOdds.Build(Meta(), "FT_STANDARD", "MAGE", ["P_A"], Cards));
        Assert.Null(ArchetypeOdds.Build(null, "FT_STANDARD", "PRIEST", ["P_A"], Cards));
    }

    [Fact]
    public void An_archetype_without_lists_is_weighed_by_its_core_cards()
    {
        var meta = new ConstructedMeta("standard", "all", "past-7", DateTime.UtcNow, 2000,
            [Archetype(1, "Quest Priest", 1000, "P_A", "P_B"), Archetype(2, "Control Priest", 1000, "P_E", "P_F")], []);

        var view = ArchetypeOdds.Build(meta, "FT_STANDARD", "PRIEST", ["P_E"], Cards)!;

        Assert.Equal("Control Priest", view.Best.Key);
        Assert.Equal("P_F", Assert.Single(view.Cards).CardId);
    }

    /// <summary>A deck code with every card once (version 1, Standard, Anduin).</summary>
    private static string Encode(int[] dbfs)
    {
        var bytes = new List<byte> { 0 };
        void Varint(int value)
        {
            var v = (uint)value;
            while (v >= 0x80)
            {
                bytes.Add((byte)(v | 0x80));
                v >>= 7;
            }

            bytes.Add((byte)v);
        }

        Varint(1);
        Varint(2);
        Varint(1);
        Varint(813);
        Varint(dbfs.Length);
        foreach (var dbf in dbfs)
        {
            Varint(dbf);
        }

        Varint(0);
        Varint(0);
        return Convert.ToBase64String(bytes.ToArray());
    }
}
