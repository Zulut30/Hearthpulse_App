namespace Hearthpulse.Core.Battlegrounds.Effects;

/// <summary>Where a card stands for the combat simulator (the six statuses of the coverage matrix).</summary>
public enum CoverageStatus
{
    /// <summary>The text does not change a fight after capture (Tavern text, keywords the engine applies itself).</summary>
    NoEffect,

    /// <summary>A Start of Combat effect: it resolved before the boards were captured, the captured stats show it.</summary>
    Resolved,

    /// <summary>A registered effect simulates it.</summary>
    Implemented,

    /// <summary>It changes a fight and no effect is registered.</summary>
    MissingEffect,

    /// <summary>An effect would need a value the capture does not hold.</summary>
    MissingInput,

    /// <summary>No relevance table knows the card: its effect cannot be classified.</summary>
    Unknown,
}

/// <summary>What kind of card a matrix row is; player-level cards are judged like the simulator judges extras.</summary>
public enum CoverageKind
{
    Minion,
    HeroPower,
    Trinket,
    DarkGift,
    Enchantment,
}

/// <summary>A card's status and a short note (Russian, for docs/bg-sim-matrix.md).</summary>
public sealed record Coverage(CoverageStatus Status, string Note);

/// <summary>
/// Classifies cards into the coverage statuses with the same rules the simulator uses to flag a fight "not simulated"
/// (<see cref="CombatRelevance"/>, <see cref="EffectRegistry"/>), so the matrix (Hearthpulse.Bench simmatrix →
/// docs/bg-sim-matrix.md) and the reasons of <see cref="CombatSimulator.Explain"/> say the same thing.
/// </summary>
public static class SimCoverage
{
    /// <summary>The status codes, in the order of <see cref="CoverageStatus"/>.</summary>
    public static IReadOnlyList<string> Codes { get; } = ["no_effect", "resolved", "implemented", "missing_effect", "missing_input", "unknown"];

    /// <summary>
    /// Combat cards whose effect cannot be written yet because a value it needs is not captured, with what is missing.
    /// They make a fight "not simulated" with <see cref="UnsimulatedReason.MissingInput"/>.
    /// </summary>
    private static readonly Dictionary<string, string> InputGaps = new(StringComparer.OrdinalIgnoreCase)
    {
        // Seen in a real log (2026-11-04): the Golem came out 67/51 while the player's Blood Gem tags said +7/+0 and the
        // dying Quilboar was a plain 4/4; 67/51 sat on unnamed enchantments of another Quilboar. Which value "its Blood
        // Gems" means is not established, and the capture keeps no enchantment values.
        ["BG30_MagicItem_442"] = "размер Голема («its Blood Gems») не найден в тегах снимка",
    };

    public static string Code(CoverageStatus status) => Codes[(int)status];

    public static string Code(UnsimulatedReason reason) => reason switch
    {
        UnsimulatedReason.MissingInput => Code(CoverageStatus.MissingInput),
        UnsimulatedReason.Unknown => Code(CoverageStatus.Unknown),
        _ => Code(CoverageStatus.MissingEffect),
    };

    /// <summary>What a combat card without an effect is missing from the capture, or null.</summary>
    public static string? InputGap(string? cardId) =>
        cardId is { Length: > 0 } && InputGaps.TryGetValue(cardId, out var gap) ? gap : null;

    /// <summary>
    /// The card's status. Cards no table knows: an effect makes them implemented; a player-level card (hero power,
    /// trinket) is unknown, as the simulator counts it; a minion or enchantment falls back to its English text as the
    /// simulator does (<see cref="CombatRelevance.TextSuggestsCombatEffect"/>): combat-like text = unknown, a Start of
    /// Combat text = resolved, other text = no effect.
    /// </summary>
    /// <param name="cardId">Normal or golden card id.</param>
    /// <param name="kind">What the card is.</param>
    /// <param name="englishText">The card's English text, used only when no table knows the card.</param>
    /// <param name="effects">Registry to check (default: every effect in Hearthpulse.Core).</param>
    public static Coverage Classify(string cardId, CoverageKind kind, string? englishText = null, EffectRegistry? effects = null)
    {
        effects ??= EffectRegistry.Default;
        var (_, golden) = CombatRelevance.Normalize(cardId);
        var effect = effects.Get(cardId, golden);
        if (effect is null && InputGap(cardId) is { } gap)
        {
            return new Coverage(CoverageStatus.MissingInput, gap);
        }

        if (CombatRelevance.Find(cardId) is not { } row)
        {
            return effect is not null ? Implemented(effect, "нет в таблице релевантности")
                : kind is CoverageKind.HeroPower or CoverageKind.Trinket ? new Coverage(CoverageStatus.Unknown, "нет в таблице: новее данных")
                : FromText(englishText);
        }

        return row.Relevance switch
        {
            Relevance.Resolved => new Coverage(CoverageStatus.Resolved, "Start of Combat: разрешилось до снимка"),
            Relevance.Relevant when effect is null => new Coverage(CoverageStatus.MissingEffect, row.Category),
            Relevance.Conditional when effect is null =>
                new Coverage(CoverageStatus.MissingEffect, $"условный ({row.Category}): бой не помечается"),
            Relevance.None or Relevance.Keywords when effect is null => new Coverage(CoverageStatus.NoEffect, row.Category),
            _ => Implemented(effect!, row.Category),
        };
    }

    private static Coverage Implemented(CombatEffect effect, string note) =>
        effect.UncapturedInput is { } missing ? new Coverage(CoverageStatus.MissingInput, $"{note}; эффект есть, но нет в снимке: {missing}")
        : effect.NeedsHand ? new Coverage(CoverageStatus.Implemented, $"{note}; у соперника missing_input (рука скрыта)")
        : effect.CapturedInput is { } input ? new Coverage(CoverageStatus.Implemented, $"{note}; величина: {input}, без неё missing_input")
        : new Coverage(CoverageStatus.Implemented, note);

    private static Coverage FromText(string? englishText)
    {
        if (string.IsNullOrWhiteSpace(englishText))
        {
            return new Coverage(CoverageStatus.Unknown, "нет в таблице и нет текста");
        }

        if (CombatRelevance.TextSuggestsCombatEffect(englishText))
        {
            return new Coverage(CoverageStatus.Unknown, "нет в таблице; текст похож на боевой");
        }

        return CombatRelevance.NormalizeText(englishText).Contains("Start of Combat", StringComparison.Ordinal)
            ? new Coverage(CoverageStatus.Resolved, "нет в таблице; по тексту Start of Combat")
            : new Coverage(CoverageStatus.NoEffect, "нет в таблице; по тексту");
    }
}
