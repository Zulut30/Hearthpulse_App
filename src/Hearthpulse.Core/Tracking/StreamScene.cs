using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Memory;

namespace Hearthpulse.Core.Tracking;

/// <summary>
/// Made-up games for the render checks of the OBS windows and streamer mode (<c>--render-ui</c>, docs/streaming.md), so the
/// scenes do not need a kept log: a Constructed game with a known deck and an opponent's BattleTag, and a Battlegrounds
/// lobby after a few fights.
/// </summary>
public static class StreamScene
{
    /// <summary>The opponent's BattleTag in the Constructed scene: streamer mode must not show it.</summary>
    public const string OpponentBattleTag = "Opponent#2947";

    /// <summary>Turn 7 of a ranked game with a 15-card list (8 left in the deck), against a Priest with a BattleTag and a rank.</summary>
    public static ConstructedView Constructed(CardDb cards)
    {
        DeckCardRow Row(string id, int cost, int left, int total, string rarity = "COMMON") =>
            new(id, cards.NameOf(id), cost, left, total, rarity, left > 0 ? left / 8.0 : 0, false);

        IReadOnlyList<DeckCardRow> deck =
        [
            Row("EX1_130", 1, 1, 2), Row("EX1_362", 2, 0, 2), Row("EX1_382", 3, 1, 2), Row("EX1_349", 3, 1, 1, "RARE"),
            Row("CS2_093", 4, 2, 2), Row("CS2_092", 4, 1, 2), Row("CS2_097", 4, 0, 2), Row("EX1_619", 2, 1, 2, "RARE"),
            Row("EX1_354", 8, 0, 1, "EPIC"), Row("EX1_383", 8, 1, 1, "LEGENDARY"),
        ];
        return new ConstructedView(7, "Paladin", true, "HERO_04", "PALADIN", "HERO_09", "PRIEST", OpponentBattleTag, deck, 8, 6, [], [], 17, 5, 0,
            new RankInfo("FT_STANDARD", 5, 47, 0, 0), new RankInfo("FT_STANDARD", 5, 45, 0, 0));
    }

    /// <summary>Turn 8 of a Battlegrounds game: seven opponents seen, one dead, the next one marked, between fights.</summary>
    public static BattlegroundsView Lobby(CardDb cards)
    {
        BgOpponent Opponent(int id, string hero, int health, int tier, int place, string? last, int damage, bool next = false, bool dead = false) =>
            new(id, hero, cards.NameOf(hero), health, 0, tier, place, dead, 0, [], 5, last, damage, next);

        IReadOnlyList<BgOpponent> opponents =
        [
            Opponent(2, "TB_BaconShop_HERO_01", 34, 5, 1, "Win", 7),
            Opponent(3, "TB_BaconShop_HERO_02", 28, 4, 2, "Loss", 4, next: true),
            Opponent(4, "TB_BaconShop_HERO_11", 19, 4, 4, "Win", 3),
            Opponent(5, "TB_BaconShop_HERO_14", 15, 3, 5, "Tie", 0),
            Opponent(6, "TB_BaconShop_HERO_18", 11, 4, 6, "Loss", 9),
            Opponent(7, "TB_BaconShop_HERO_22", 6, 3, 7, "Loss", 6),
            Opponent(8, "TB_BaconShop_HERO_28", 0, 3, 8, "Loss", 12, dead: true),
        ];
        return new BattlegroundsView(8, 4, 6, "TB_BaconShop_HERO_72", 24, 0, 3, false, false, false, [], ["BEAST", "DEMON", "MURLOC", "NAGA", "UNDEAD"], true,
            opponents, [], [], [], null, 0, [], []);
    }
}
