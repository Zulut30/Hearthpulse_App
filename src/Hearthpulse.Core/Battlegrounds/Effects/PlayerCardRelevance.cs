namespace Hearthpulse.Core.Battlegrounds.Effects;

/// <summary>
/// Player-level cards the generated minion table does not cover, labelled by hand from their English card text (patch
/// 37.0, HearthstoneJSON): hero powers, trinkets and Dark Gift enchantments whose effect acts in a fight after the
/// boards are captured. A row marked <see cref="Relevance.Relevant"/> without a registered effect makes the fight
/// "not simulated" (hero powers and trinkets used not to count at all). Dark Gifts whose granted Deathrattle or Rally
/// only acts in the Tavern are <see cref="Relevance.None"/>: they explain the granted marker on the minion, so it is not
/// flagged. Start of Combat cards resolved before capture and Tavern-only cards are left out (unknown extras are not
/// counted). Hero powers: those of heroes in the API's hero list; trinkets: every one with a combat trigger (the API
/// has no trinket popularity). See docs/bg-combat-sim.md.
/// </summary>
internal static class PlayerCardTable
{
    private static CardRelevance Relevant(string id, string category, string name) => new(id, string.Empty, Relevance.Relevant, category, name, Origin.Player);

    private static CardRelevance Tavern(string id, string category, string name) => new(id, string.Empty, Relevance.None, category, name, Origin.Player);

    internal static readonly CardRelevance[] Entries =
    [
        // ---- Dark Gifts (BG36_MidGameEffect_000*): the enchantment a gift leaves on the minion.
        Relevant("BG36_MidGameEffect_000te2", "GIFT_DR_BUFF", "Offensive Sacrifice"),
        Relevant("BG36_MidGameEffect_000t2e2", "GIFT_DR_BUFF", "Defensive Sacrifice"),
        Relevant("BG36_MidGameEffect_000t12e", "GIFT_REBORN", "Persisting Horror"),
        Relevant("BG36_MidGameEffect_000t15e", "GIFT_SHIELD", "Toreth's Blessing"),
        Relevant("BG36_MidGameEffect_000t60e", "GIFT_IMMUNE_ATTACKING", "Invulnerability"),
        Relevant("BG36_MidGameEffect_000t61e", "GIFT_DR_SUMMON", "Golemancy"),
        Tavern("BG36_MidGameEffect_000t3e2", "GIFT_RALLY_TAVERN", "Charisma"),
        Tavern("BG36_MidGameEffect_000t5e2", "GIFT_DR_TAVERN", "Mystic Essence"),
        Tavern("BG36_MidGameEffect_000t52e", "GIFT_DR_TAVERN", "Fresh Perspective"),
        Tavern("BG36_MidGameEffect_000t66e", "GIFT_RALLY_TAVERN", "Demonology"),
        Tavern("BG36_MidGameEffect_000t80e", "GIFT_RALLY_TAVERN", "Consanguinity"),

        // ---- Hero powers acting in combat.
        Relevant("BG22_HERO_003p", "HP_SPACE_SUMMON", "Stormpike Strength"),
        Relevant("BG22_HERO_002p", "HP_SPACE_SUMMON", "Frostwolf Fervor"),
        Relevant("TB_BaconShop_HP_107", "HP_SUMMON_BUFF", "Sprout It Out!"),
        Relevant("BG22_HERO_305p", "HP_AVENGE_SUMMON", "Broodmother"),
        Relevant("BG23_HERO_201p", "HP_SPACE_SUMMON", "Tentacular"),
        Relevant("BG20_HERO_100p", "HP_KILL_BUFF", "Glory of Combat"),

        // ---- Trinkets with a combat trigger or an aura that reaches minions summoned in combat.
        Relevant("BG30_MagicItem_427", "TR_DAMAGE_BUFF", "Tiger Carving"),
        Relevant("BG30_MagicItem_427t", "TR_DAMAGE_BUFF", "Tiger Carving"),
        Relevant("BG30_MagicItem_432", "TR_VENOM_BUFF", "Belcher Portrait"),
        Relevant("BG30_MagicItem_432t", "TR_VENOM_BUFF", "Belcher Portrait"),
        Relevant("BG30_MagicItem_433", "TR_DEATH_BUFF", "Alliance Keychain"),
        Relevant("BG30_MagicItem_433t", "TR_DEATH_BUFF", "Alliance Keychain"),
        Relevant("BG30_MagicItem_437", "TR_AVENGE_KEYWORD", "Staff of the Scourge"),
        Relevant("BG30_MagicItem_442", "TR_DEATH_SUMMON", "Blood Golem Sticker"),
        Relevant("BG30_MagicItem_540", "TR_SUMMON_BUFF", "Slamma Sticker"),
        Relevant("BG30_MagicItem_700", "TR_DEATHRATTLE", "Deathly Phylactery"),
        Relevant("BG30_MagicItem_864", "TR_AVENGE_BUFF", "Gilnean Thorned Rose"),
        Relevant("BG30_MagicItem_880", "TR_AURA", "Feral Talisman"),
        Relevant("BG30_MagicItem_880t", "TR_AURA", "Feral Talisman"),
        Relevant("BG30_MagicItem_886", "TR_SUMMON_KEYWORD", "Reinforced Shield"),
        Relevant("BG30_MagicItem_910", "TR_SHIELD", "Mechagon Adapter"),
        Relevant("BG30_MagicItem_925", "TR_ATTACK_BUFF", "Ceremonial Sword"),
        Relevant("BG30_MagicItem_978", "TR_SUMMON_KEYWORD", "Blingtron's Sunglasses"),
        Relevant("BG32_MagicItem_200", "TR_ATTACK_BUFF", "All-Purpose Kibble"),
        Relevant("BG32_MagicItem_301", "TR_SUMMON_KEYWORD", "Bassgill Portrait"),
        Relevant("BG32_MagicItem_363", "TR_ATTACK_KEYWORD", "Faerie Dragon Scale"),
        Relevant("BG32_MagicItem_415", "TR_AVENGE_BATTLECRY", "Battle Horn"),
        Relevant("BG32_MagicItem_860", "TR_AVENGE_SUMMON", "Beetle Band"),
        Relevant("BG32_MagicItem_860t", "TR_AVENGE_SUMMON", "Beetle Band"),
        Relevant("BG32_MagicItem_862", "TR_DEATHRATTLE_BUFF", "Unholy Sanctum"),
        Relevant("BG32_MagicItem_862t", "TR_DEATHRATTLE_BUFF", "Unholy Sanctum"),
        Relevant("BG32_MagicItem_864", "TR_AVENGE_BUFF", "Bird Feeder"),
        Relevant("BG32_MagicItem_864t", "TR_AVENGE_BUFF", "Bird Feeder"),
        Relevant("BG32_MagicItem_907", "TR_LAST_DEATH_SUMMON", "S'Thara Sticker"),
        Relevant("BG32_MagicItem_934", "TR_AURA", "Dazzling Dagger"),
        Relevant("BG35_MagicItem_430", "TR_DEATH_GEM", "Scrapsmith Portrait"),
        Relevant("BG35_MagicItem_432", "TR_DEATHRATTLE_GEM", "Blood Amulet"),
        Relevant("BG35_MagicItem_731", "TR_REBORN_KEYWORD", "Deathtouch Apple"),
        Relevant("BG35_MagicItem_732", "TR_LAST_DEATH_SUMMON", "Soul Fermenter"),
        Relevant("BG35_MagicItem_849", "TR_AVENGE_BUFF", "Cloud Serpent Horn"),
        Relevant("BG36_MagicItem_201", "TR_ATTACK_BUFF", "Lionfish Portrait"),
        Relevant("BG36_MagicItem_205", "TR_REBORN_BUFF", "Deathwhisper Sticker"),
        Relevant("BG36_MagicItem_216", "TR_DEATH_BUFF", "Eternal Portrait"),
        Relevant("BG36_MagicItem_403", "TR_AURA", "Hammer of Twilight"),
        Relevant("BG36_MagicItem_403t", "TR_AURA", "Hammer of Twilight"),
        Relevant("BG36_MagicItem_416", "TR_DEITY_KEYWORD", "Evil Experiment"),
    ];
}
