using System.Globalization;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Power;
using Hearthpulse.Core.Tracking.Counters;

namespace Hearthpulse.Core.Battlegrounds;

/// <summary>
/// The Battlegrounds counters (Blood Gem, Tavern spells, Eternal Knight, free Refreshes...), shown as chips on the BG
/// overlay with the same <see cref="CounterMode"/>s as the Constructed counters (ids start with "bg-"). Specification
/// and sources: <c>docs/counters-battlegrounds.md</c>.
/// <para>
/// Two kinds of counter. <b>Confirmed</b> ones read events or tags seen in real Power.log lines; in Auto mode they show
/// once a related card is in the tavern, hand, board, trinkets or hero power, or once their count is under way.
/// <b>Unconfirmed</b> ones read a player tag named in the GameTag enum of HearthSim's MIT-licensed python-hearthstone,
/// or a player enchantment from HearthstoneJSON, that no real log has shown yet. They are relevant only when that data
/// has arrived, never assume a default value and show <see cref="NoData"/> in Always mode until then.
/// </para>
/// </summary>
public static class BattlegroundsCounters
{
    /// <summary>The value of a counter the game has not sent data for yet.</summary>
    public const string NoData = "—";

    // Enchantment values: the SDN1/SDN2 fields card texts refer to ("Get SDN1 extra Gold next turn").
    private const string ScriptData1 = "TAG_SCRIPT_DATA_NUM_1";
    private const string ScriptData2 = "TAG_SCRIPT_DATA_NUM_2";

    private const string RerollButtonId = "TB_BaconShop_8p_Reroll_Button";

    private static readonly CounterDef[] Defs =
    [
        // Not yet confirmed in a real Power.log. Source: GameTag enum BACON_BLOODGEMBUFFATKVALUE = 1844,
        // BACON_BLOODGEMBUFFHEALTHVALUE = 2827 (also read by the simulator, Effects/attack/BloodGems.cs).
        new("bg-blood-gem", "Blood Gem", "Stats each of your Blood Gems gives.", false,
            ["BG23_017", "BG30_123", "BG33_885", "BG29_708", "BG32_433", "BG31_893", "BG27_Reward_815", "BG30_MagicItem_410", "BG30_MagicItem_410t2",
                "BG30_MagicItem_988", "BG30_MagicItem_988t", "BG35_MagicItem_431t", "BG35_MagicItem_433"],
            c => c.TagStats("BACON_BLOODGEMBUFFATKVALUE", "BACON_BLOODGEMBUFFHEALTHVALUE") is var (found, attack, health) && found
                ? new($"+{1 + attack}/+{1 + health}", Details: [$"+1/+1 printed, plus +{attack}/+{health} from your cards."], Active: true)
                : Missing()),

        // Not yet confirmed in a real Power.log. Source: HearthstoneJSON enchantment BG34_689e2 "Blood Gem Barrage Player Ench".
        new("bg-blood-gem-barrage", "Blood Gem Barrage", "Stats Blood Gem Barrage gives the minions of each Refresh.", false,
            ["BG34_689", "BG34_682", "BG34_683", "BG36_341"],
            c => EnchantStats(c, "BG34_689e2")),

        // Not yet confirmed in a real Power.log. Source: HearthstoneJSON player enchantments "Elementals in the Tavern have
        // increased stats..." (one per source card, summed).
        new("bg-elemental-tavern", "Tavern Elementals", "Extra stats of Elementals in the Tavern.", false,
            ["BG31_815", "BG28_707", "BG30_MagicItem_544", "BG30_MagicItem_544t"],
            c => EnchantStats(c, "BG_ShopBuff_Elemental", "BG31_815pe", "BG28_707e", "BG30_MagicItem_544pe", "BG21_020pe", "BG26_162pe", "BG32_814pe",
                "BG32_843pe", "BG34_855pe", "BGS_104pe")),

        // Not yet confirmed in a real Power.log. Source: GameTag enum BACON_ELEMENTAL_BUFFATKVALUE = 4002,
        // BACON_ELEMENTAL_BUFFHEALTHVALUE = 4001.
        new("bg-elemental-extra", "Elemental bonus", "Extra stats your Elementals give.", false,
            ["BG32_MagicItem_802", "BG32_MagicItem_802t", "BG36_MagicItem_380", "BG32_MagicItem_830"],
            c => TagStats(c, "BACON_ELEMENTAL_BUFFATKVALUE", "BACON_ELEMENTAL_BUFFHEALTHVALUE")),

        // Not yet confirmed in a real Power.log. Source: HearthstoneJSON enchantment BG25_011pe "Undead Bonus Attack Player Enchant".
        new("bg-undead", "Undead Attack", "Attack your Undead have this game.", false,
            ["BG25_011", "BG28_604", "BG34_690", "BG34_692", "BG30_MagicItem_547", "BG30_MagicItem_547t", "BG32_MagicItem_276", "BG30_MagicItem_989",
                "BG30_MagicItem_989t"],
            c => EnchantStats(c, "BG25_011pe")),

        // Not yet confirmed in a real Power.log. Source: GameTag enum TAVERN_SPELL_ATTACK_INCREASE = 3989,
        // TAVERN_SPELL_HEALTH_INCREASE = 3990 (also read by the simulator, Effects/attack/CrimsonVindicator.cs).
        new("bg-tavern-spells", "Tavern spells", "Extra stats your Tavern spells give.", false,
            ["BG32_237", "BG32_341", "BG32_821", "BG32_835", "BG32_860", "BG32_880", "BG33_830", "BG33_924", "BG35_341", "BG36_104", "BG36_300",
                "TB_BaconShop_HP_085t", "BG33_Reward_020", "BG32_MagicItem_270", "BG32_MagicItem_270t", "BG32_MagicItem_700", "BG32_MagicItem_801",
                "BG32_MagicItem_801t", "BG35_MagicItem_920", "BG36_MagicItem_371", "BG36_MagicItem_373", "BG36_MagicItem_418"],
            c => TagStats(c, "TAVERN_SPELL_ATTACK_INCREASE", "TAVERN_SPELL_HEALTH_INCREASE")),

        // Not yet confirmed in a real Power.log. Source: HearthstoneJSON enchantment BG34_854pe "Random minion Tavern Minion Buff
        // Player Ench"; no current card text names this buff, so the chip has no related cards.
        new("bg-random-tavern", "Random Tavern minion", "Stats a random minion in the Tavern gains.", false, [],
            c => EnchantStats(c, "BG34_854pe")),

        // Not yet confirmed in a real Power.log. Source: HearthstoneJSON enchantment BG34_170pe "Volumizer Player Enchant".
        new("bg-volumizer", "Volumizers", "Extra stats your Volumizers have this game.", false,
            ["BG34_170", "BG34_170t", "BG34_170t2", "BG34_170t3", "BG34_171", "BG36_366", "BG36_367", "BG36_MagicItem_423", "BG36_MagicItem_423t"],
            c => EnchantStats(c, "BG34_170pe")),

        // Not yet confirmed in a real Power.log. Source: HearthstoneJSON enchantment BG34_402pe "Whelp Buff Player Enchant".
        new("bg-whelp", "Whelps", "Extra stats your Whelps have this game.", false,
            ["BG34_402", "BG34_631", "BG22_HERO_305p"],
            c => EnchantStats(c, "BG34_402pe")),

        // Not yet confirmed in a real Power.log. Source: HearthstoneJSON enchantment BG31_808pe "Beetle Army Player Enchant".
        new("bg-beetles", "Beetles", "Extra stats your Beetles have this game (on top of the 2/2 token).", false,
            ["BG31_801", "BG31_803", "BG31_808", "BG31_809", "BG36_209", "BG28_603", "BG32_MagicItem_860", "BG32_MagicItem_860t"],
            c => EnchantStats(c, "BG31_808pe")),

        // Not yet confirmed in a real Power.log. Source: GameTag enum BACON_OLD_GOD_ATTACK = 4914, BACON_OLD_GOD_HEALTH = 4915,
        // read by name only (read as total stats, like the simulator's Effects/ongoing/Deity.cs). The lobby's Deity
        // (BACON_GLOBAL_OLD_GOD_DBID on the game entity) is confirmed and only names the chip.
        new("bg-deity", "Deity", "Stats of your sleeping Deity.", false,
            ["BGFYM_000", "BGFYM_011", "BG36_110", "BG36_099", "BG36_106", "BG36_108", "BG36_109", "BG36_111", "BG36_113", "BG36_318", "BG36_371",
                "BGFYM_005", "BG36_MagicItem_404", "BG36_MagicItem_404t", "BG36_MagicItem_416", "BG36_MagicItem_417", "BG36_MagicItem_602"],
            c =>
            {
                var deity = c.Cards.Get(c.Game.GameEntity?.GetInt("BACON_GLOBAL_OLD_GOD_DBID") ?? 0);
                var (found, attack, health) = c.TagStats("BACON_OLD_GOD_ATTACK", "BACON_OLD_GOD_HEALTH");
                return found
                    ? new($"{attack}/{health}", Active: true, Title: deity?.Name, IconCardId: deity?.Id)
                    : Missing() with { Title = deity?.Name, IconCardId = deity?.Id };
            }),

        new("bg-eternal-knight", "Eternal Knight", "Stats each Eternal Knight has from friendly Eternal Knights that died this game.", true,
            ["BG25_008", "BG25_009", "BG34_403", "BG30_MagicItem_301", "BG36_MagicItem_216"],
            c =>
            {
                var knights = c.Tracker.Died.Where(id => Normal(c.Cards, id) == "BG25_008").ToList();
                var died = knights.Count;
                return new($"+{4 * died}/+{2 * died}",
                    Details: [$"{died} friendly Eternal Knight{(died == 1 ? "" : "s")} died.", $"Golden Eternal Knight: +{8 * died}/+{4 * died}."], Active: died > 0,
                    Counted: knights);
            }),

        new("bg-automaton", "Ancestral Automaton", "Stats your next Ancestral Automaton has from the ones you've summoned this game.", true,
            ["BG_TTN_401", "BG30_MagicItem_303"],
            c =>
            {
                var automatons = c.Tracker.Summoned.Where(id => Normal(c.Cards, id) == "BG_TTN_401").ToList();
                var summoned = automatons.Count;
                return new($"+{3 * summoned}/+{2 * summoned}",
                    Details: [$"{summoned} Ancestral Automaton{(summoned == 1 ? "" : "s")} summoned.", $"Golden: +{6 * summoned}/+{4 * summoned}."],
                    Active: summoned > 0, Counted: automatons);
            }),

        new("bg-naga-spells", "Spells cast", "Spells you've cast this game (Naga that improve every 3 spells).", true,
            ["BG31_924", "BG31_925", "BG31_035", "BG35_921", "BG28_633", "BG32_MagicItem_808", "BG32_MagicItem_808t", "BG32_MagicItem_930",
                "BG32_MagicItem_932", "BG32_MagicItem_934", "BG35_MagicItem_920", "BG36_MagicItem_305"],
            c =>
            {
                var spells = c.Tag("NUM_SPELLS_PLAYED_THIS_GAME");
                var left = 3 - spells % 3;
                return new(Num(spells), spells % 3 / 3.0, [$"Next \"every 3 spells\" improvement after {left} more spell{(left == 1 ? "" : "s")}."], Goal: 3);
            }),

        // The reroll button's COST dropping to 0 is confirmed in a real log. The count is not: GameTag enum
        // BACON_FREE_REFRESH_COUNT = 4536, not yet confirmed in a real Power.log, used only when it arrives.
        new("bg-free-refresh", "Free Refresh", "Free Refreshes you have.", true,
            ["BGS_116", "BG28_827", "BG36_330", "BGDUO_110", "TB_BaconShop_HP_063", "BG32_MagicItem_888", "BG32_MagicItem_901", "BG35_MagicItem_930",
                "BG36_MagicItem_200", "BG33_Reward_004"],
            c =>
            {
                if (c.HasTag("BACON_FREE_REFRESH_COUNT") && c.Tag("BACON_FREE_REFRESH_COUNT") is > 0 and var count)
                {
                    return new(Num(count), Details: [$"{count} free Refresh{(count == 1 ? "" : "es")}."], Active: true);
                }

                var button = c.Game.Controlled(c.Game.LocalPlayerId, "PLAY").FirstOrDefault(e => e.CardId == RerollButtonId);
                if (button is null || button.GetString(GameTag.Cost).Length == 0)
                {
                    return Missing();
                }

                return button.GetInt(GameTag.Cost) == 0 ? new("Free", Details: ["Your next Refresh costs 0 Gold."], Active: true) : new("0");
            }),

        // Not yet confirmed in a real Power.log. Source: GameTag enum BACON_PLAYER_EXTRA_GOLD_NEXT_TURN = 4286,
        // BACON_PLAYER_OVERDRAWN_GOLD_NEXT_TURN = 4287.
        new("bg-gold-next-turn", "Gold next turn", "Extra Gold you get next turn, minus Gold you lose.", false,
            ["BG26_135", "BG28_800", "BG28_513", "TB_BaconShop_HP_008", "BG30_MagicItem_923", "BG27_Anomaly_575", "BG31_Anomaly_116"],
            c =>
            {
                if (!c.HasTag("BACON_PLAYER_EXTRA_GOLD_NEXT_TURN") && !c.HasTag("BACON_PLAYER_OVERDRAWN_GOLD_NEXT_TURN"))
                {
                    return Missing();
                }

                var extra = c.Tag("BACON_PLAYER_EXTRA_GOLD_NEXT_TURN");
                var lost = c.Tag("BACON_PLAYER_OVERDRAWN_GOLD_NEXT_TURN");
                var net = extra - lost;
                return new(net > 0 ? $"+{net}" : Num(net), Details: [$"+{extra} extra, -{lost} overdrawn."], Active: net != 0);
            }),

        // Not yet confirmed in a real Power.log. Source: GameTag enum BACON_FODDERS_IN_REFRESH = 4664 (the enum also names 4664
        // BACON_FODDER_TOOLTIP).
        new("bg-fodder", "Fodder", "Fodders in your next Refresh.", false,
            ["BG35_150", "BG35_155", "BG36_730", "BG35_MagicItem_151", "BG35_MagicItem_151t", "BG36_MagicItem_830"],
            c => c.HasTag("BACON_FODDERS_IN_REFRESH") ? new(Num(c.Tag("BACON_FODDERS_IN_REFRESH")), Active: c.Tag("BACON_FODDERS_IN_REFRESH") > 0) : Missing()),
    ];

    /// <summary>Every Battlegrounds counter, in display order, for the settings page.</summary>
    public static IReadOnlyList<CounterInfo> All { get; } = [.. Defs.Select(d => new CounterInfo(d.Id, d.Title, d.Description))];

    /// <summary>Ids of the counters that read data no real Power.log has shown yet (for tests and the settings page).</summary>
    public static IReadOnlySet<string> Unconfirmed { get; } = Defs.Where(d => !d.Confirmed).Select(d => d.Id).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Every Battlegrounds counter of the local player; the overlay applies each counter's <see cref="CounterMode"/>.
    /// </summary>
    /// <param name="visibleCardIds">Cards the player can see now: tavern, hand, board, trinkets, hero power.</param>
    public static IReadOnlyList<CounterView> Build(GameState game, BattlegroundsCounterTracker tracker, CardDb cards, IEnumerable<string> visibleCardIds)
    {
        var result = new List<CounterView>();
        if (!game.IsBattlegrounds || game.LocalPlayerId == 0)
        {
            return result;
        }

        var player = game.LocalPlayerEntity;
        var seen = visibleCardIds.Select(id => Normal(cards, id)).ToHashSet(StringComparer.Ordinal);
        var enchantments = player is null
            ? []
            : game.Entities.Values.Where(e => e.CardType == "ENCHANTMENT" && e.Zone == "PLAY" && e.GetInt("ATTACHED") == player.Id).ToList();
        var context = new CounterContext(game, player, tracker, cards, enchantments);
        foreach (var def in Defs)
        {
            var value = def.Compute(context);
            var seenCard = def.Related.FirstOrDefault(seen.Contains);
            var relevant = value.Active || (def.Confirmed && seenCard is not null);
            var icon = value.IconCardId ?? seenCard ?? def.Related.FirstOrDefault();
            result.Add(new CounterView(def.Id, value.Title ?? def.Title, value.Value, value.Progress, false, icon, def.Description,
                value.Details ?? (value.Value == NoData ? ["No data from the game yet."] : []), relevant)
            {
                // Copies are not counted in Battlegrounds: a related card is one the player can see now.
                RelatedCards = def.Related.Where(seen.Contains).Select(id => new CounterCard(id, cards.NameOf(id), 0)).ToList(),
                Counted = (value.Counted ?? []).GroupBy(id => Normal(cards, id), StringComparer.Ordinal)
                    .Select(g => new CounterCard(g.Key, cards.NameOf(g.Key), g.Count())).ToList(),
                Goal = value.Goal,
            });
        }

        return result;
    }

    /// <summary>The normal (non-golden) card id of a Battlegrounds card.</summary>
    private static string Normal(CardDb cards, string cardId)
    {
        if (cards.Get(cardId) is { BattlegroundsNormalDbfId: > 0 } golden && cards.Get(golden.BattlegroundsNormalDbfId) is { } normal)
        {
            return normal.Id;
        }

        return cardId.EndsWith("_G", StringComparison.Ordinal) ? cardId[..^2] : cardId;
    }

    private static CounterValue Missing() => new(NoData);

    private static CounterValue TagStats(CounterContext c, string attackTag, string healthTag) =>
        c.TagStats(attackTag, healthTag) is var (found, attack, health) && found ? new(Stats(attack, health), Active: true) : Missing();

    private static CounterValue EnchantStats(CounterContext c, params string[] cardIds)
    {
        var found = c.Enchantments.Where(e => cardIds.Contains(e.CardId, StringComparer.Ordinal)).ToList();
        if (found.Count == 0)
        {
            return Missing();
        }

        var attack = found.Sum(e => e.GetInt(ScriptData1));
        var health = found.Sum(e => e.GetInt(ScriptData2));
        return new(Stats(attack, health), Active: true);
    }

    /// <summary>"+3/+2", or "+3" for an Attack-only bonus.</summary>
    private static string Stats(int attack, int health) => health == 0 ? Signed(attack) : $"{Signed(attack)}/{Signed(health)}";

    private static string Signed(int value) => value >= 0 ? $"+{Num(value)}" : Num(value);

    private static string Num(int value) => value.ToString(CultureInfo.InvariantCulture);

    private sealed record CounterDef(string Id, string Title, string Description, bool Confirmed, string[] Related, Func<CounterContext, CounterValue> Compute);

    /// <param name="Active">Shown in Auto mode without a related card: the data arrived (unconfirmed) or the count is under way.</param>
    /// <param name="Title">Overrides the counter's title (the lobby's Deity).</param>
    /// <param name="Counted">Card ids of what was counted, one per count (golden copies count as the normal card).</param>
    private sealed record CounterValue(string Value, double? Progress = null, IReadOnlyList<string>? Details = null, bool Active = false,
        string? Title = null, string? IconCardId = null, IReadOnlyList<string>? Counted = null, int? Goal = null);

    private sealed record CounterContext(GameState Game, Entity? Player, BattlegroundsCounterTracker Tracker, CardDb Cards, IReadOnlyList<Entity> Enchantments)
    {
        public int Tag(string tag) => Player?.GetInt(tag) ?? 0;

        /// <summary>The tag has arrived on the player entity (any value, 0 included).</summary>
        public bool HasTag(string tag) => Player is not null && Player.GetString(tag).Length > 0;

        public (bool Found, int Attack, int Health) TagStats(string attackTag, string healthTag) =>
            (HasTag(attackTag) || HasTag(healthTag), Tag(attackTag), Tag(healthTag));
    }
}
