using System.Text.RegularExpressions;
using Hearthpulse.Core.Power;

namespace Hearthpulse.Core.CardPool;

/// <summary>Where the player's Excavates stand: the tier the next one gives and how many tiers the cycle has.</summary>
/// <param name="Next">1-based tier of the next Excavate.</param>
/// <param name="Tiers">3, or 4 for a class with a legendary treasure.</param>
public sealed record ExcavateState(int Next, int Tiers);

/// <summary>
/// Excavate treasures by tier. Each Excavate gives a random treasure of the next tier: common, rare, epic, then the
/// class's legendary for Death Knight, Mage, Paladin, Rogue, Shaman, Warlock and Warrior; other classes go back to
/// common after the epic, and the seven start over after the legendary. Treasures are not collectible, so they are not
/// in <see cref="CardPoolCatalog"/>; the list is the Hearthstone Wiki's (checked 2026-10-28, with the 28.4 additions).
/// Power.log keeps the cycle on the player entity: <c>MAX_EXCAVATE_TIER</c> (0-based, 3 = four tiers) from the start,
/// <c>CURRENT_EXCAVATE_TIER</c> once the player has Excavated.
/// </summary>
public static partial class Excavate
{
    public const string Mechanic = "EXCAVATE";

    public const string CurrentTierTag = "CURRENT_EXCAVATE_TIER";

    public const string MaxTierTag = "MAX_EXCAVATE_TIER";

    /// <summary><see cref="CardPoolView.Source"/> of an Excavate pool.</summary>
    public const string Source = "Excavate treasures";

    /// <summary>A treasure; <paramref name="CardClass"/> is set for the tier-4 legendaries only.</summary>
    public sealed record Treasure(string Id, int Tier, string Name, string Type, string? CardClass = null)
    {
        /// <summary>The treasure's mana cost, which is its tier for every treasure.</summary>
        public int Cost => Tier;
    }

    public static readonly IReadOnlyList<Treasure> Treasures =
    [
        new("WW_001t", 1, "Rock", "SPELL"),
        new("WW_001t2", 1, "Water Source", "SPELL"),
        new("WW_001t3", 1, "Fool's Azerite", "SPELL"),
        new("WW_001t4", 1, "Escaping Trogg", "MINION"),
        new("WW_001t18", 1, "Pouch of Coins", "SPELL"),
        new("DEEP_999t1", 1, "Heartblossom", "SPELL"),
        new("WW_001t5", 2, "Falling Stalactite", "SPELL"),
        new("WW_001t7", 2, "Canary", "MINION"),
        new("WW_001t8", 2, "Glowing Glyph", "SPELL"),
        new("WW_001t9", 2, "Azerite Chunk", "SPELL"),
        new("WW_001t16", 2, "Living Stone", "MINION"),
        new("DEEP_999t2", 2, "Deepholm Geode", "MINION"),
        new("WW_001t11", 3, "Ogrefist Boulder", "LOCATION"),
        new("WW_001t12", 3, "Collapse!", "SPELL"),
        new("WW_001t13", 3, "Steelhide Mole", "MINION"),
        new("WW_001t14", 3, "Azerite Gem", "SPELL"),
        new("WW_001t17", 3, "Motherlode Drake", "MINION"),
        new("DEEP_999t3", 3, "World Pillar Fragment", "SPELL"),
        new("WW_001t26", 4, "The Azerite Rat", "MINION", "DEATHKNIGHT"),
        new("WW_001t24", 4, "The Azerite Hawk", "MINION", "MAGE"),
        new("DEEP_999t4", 4, "The Azerite Dragon", "MINION", "PALADIN"),
        new("WW_001t23", 4, "The Azerite Scorpion", "MINION", "ROGUE"),
        new("DEEP_999t5", 4, "The Azerite Murloc", "MINION", "SHAMAN"),
        new("WW_001t25", 4, "The Azerite Snake", "MINION", "WARLOCK"),
        new("WW_001t27", 4, "The Azerite Ox", "MINION", "WARRIOR"),
    ];

    /// <summary>Rarity of each tier's treasures, tier 1 first.</summary>
    public static readonly IReadOnlyList<string> TierRarities = ["COMMON", "RARE", "EPIC", "LEGENDARY"];

    private static readonly Dictionary<string, Treasure> ById = Treasures.ToDictionary(t => t.Id, StringComparer.Ordinal);

    /// <summary>The treasure with this card id, or null.</summary>
    public static Treasure? TreasureOf(string? cardId) => cardId is not null && ById.TryGetValue(cardId, out var t) ? t : null;

    /// <summary>Whether the class has a tier-4 treasure (and so a four-tier cycle).</summary>
    public static bool HasLegendary(string? cardClass) => cardClass is not null && Treasures.Any(t => t.CardClass == cardClass);

    /// <summary>Tiers of a class's cycle; four when the class is not known, so nothing is left out.</summary>
    public static int TierCount(string? cardClass) => cardClass is null || HasLegendary(cardClass) ? 4 : 3;

    /// <summary>
    /// The treasures one tier can give a class: tiers 1-3 the same for everyone, tier 4 the class's legendary (all seven
    /// when the class is not known or has none).
    /// </summary>
    public static IReadOnlyList<Treasure> TierTreasures(int tier, string? cardClass)
    {
        var all = Treasures.Where(t => t.Tier == tier).ToList();
        return tier == 4 && all.Where(t => t.CardClass == cardClass).ToList() is { Count: > 0 } own ? own : all;
    }

    /// <summary>English text that Excavates or counts Excavates ("Excavate a treasure", "If you've Excavated twice").</summary>
    public static bool Mentions(string? englishText) => englishText is { Length: > 0 } && MentionRegex().IsMatch(englishText);

    /// <summary>
    /// The local player's Excavate cycle in a game, or null before the player entity is known. The tier comes from
    /// <c>CURRENT_EXCAVATE_TIER</c>; until the log has it, from the treasures the player got this game. Either counts
    /// past the cycle's end on some builds, so it is taken modulo the cycle.
    /// </summary>
    public static ExcavateState? StateOf(GameState game, string? ownClass)
    {
        if (game.LocalPlayerEntity is not { } player)
        {
            return null;
        }

        var tiers = player.Tags.ContainsKey(MaxTierTag) ? Math.Clamp(player.GetInt(MaxTierTag) + 1, 1, 4) : TierCount(ownClass);
        var done = player.Tags.ContainsKey(CurrentTierTag)
            ? player.GetInt(CurrentTierTag)
            : game.Entities.Values.Count(e => e.Controller == game.LocalPlayerId && TreasureOf(e.CardId) is not null);
        return new ExcavateState(Math.Max(0, done) % tiers + 1, tiers);
    }

    [GeneratedRegex(@"\bExcavat", RegexOptions.CultureInvariant)]
    private static partial Regex MentionRegex();
}
