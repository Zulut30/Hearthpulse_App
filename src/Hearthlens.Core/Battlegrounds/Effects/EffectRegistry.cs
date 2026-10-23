using System.Reflection;

namespace Hearthlens.Core.Battlegrounds.Effects;

/// <summary>
/// Card id → <see cref="CombatEffect"/>, found once by reflection from <see cref="CardEffectAttribute"/>s, so adding an
/// effect means adding a class file and nothing else. Immutable and thread-safe after construction.
/// </summary>
public sealed class EffectRegistry
{
    private static readonly Lazy<EffectRegistry> DefaultRegistry = new(() => FromAssembly(typeof(CombatEffect).Assembly));
    private static readonly CombatEffect[] NoEffects = [];

    // Key: normal card id. Golden-specific registrations win over "both" ones for golden minions, and vice versa.
    private readonly Dictionary<string, CombatEffect[]> _normal = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CombatEffect[]> _golden = new(StringComparer.Ordinal);
    private readonly List<string> _problems = new();

    private EffectRegistry(IEnumerable<Type> types)
    {
        var normalExplicit = new HashSet<string>(StringComparer.Ordinal);
        var goldenExplicit = new HashSet<string>(StringComparer.Ordinal);
        // Sorted so that the outcome (including which duplicate wins) does not depend on reflection order.
        foreach (var type in types.Distinct().OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            var attributes = type.GetCustomAttributes<CardEffectAttribute>(inherit: false).ToList();
            if (attributes.Count == 0)
            {
                continue;
            }

            if (!typeof(CombatEffect).IsAssignableFrom(type) || type.IsAbstract)
            {
                _problems.Add($"{type.FullName}: [CardEffect] on a type that is not a concrete CombatEffect");
                continue;
            }

            CombatEffect effect;
            try
            {
                effect = (CombatEffect)Activator.CreateInstance(type, nonPublic: true)!;
            }
            catch (Exception ex) when (ex is MissingMethodException or TargetInvocationException or MemberAccessException)
            {
                _problems.Add($"{type.FullName}: cannot create ({ex.GetBaseException().Message})");
                continue;
            }

            foreach (var attribute in attributes)
            {
                var (cardId, goldenId) = CombatRelevance.Normalize(attribute.CardId);
                var filter = goldenId ? GoldenFilter.GoldenOnly : attribute.Golden;
                var isExplicit = filter != GoldenFilter.Both;
                if (filter != GoldenFilter.GoldenOnly)
                {
                    Register(_normal, normalExplicit, cardId, effect, type, "normal", isExplicit);
                }

                if (filter != GoldenFilter.NormalOnly)
                {
                    Register(_golden, goldenExplicit, cardId, effect, type, "golden", isExplicit);
                }
            }
        }
    }

    /// <summary>Every effect class in Hearthlens.Core.</summary>
    public static EffectRegistry Default => DefaultRegistry.Value;

    public static EffectRegistry Empty { get; } = new(Array.Empty<Type>());

    public static EffectRegistry FromAssembly(Assembly assembly)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t is not null).ToArray()!;
        }

        return new EffectRegistry(types);
    }

    /// <summary>A registry of just these effect classes (tests).</summary>
    public static EffectRegistry FromTypes(params Type[] types) => new(types);

    /// <summary>Registration problems: duplicate card ids, attributes on non-effect types, classes that cannot be created.</summary>
    public IReadOnlyList<string> Problems => _problems;

    /// <summary>Normal card ids with an effect for the normal version.</summary>
    public IReadOnlyCollection<string> NormalCardIds => _normal.Keys;

    /// <summary>Normal card ids with an effect for the golden version.</summary>
    public IReadOnlyCollection<string> GoldenCardIds => _golden.Keys;

    /// <summary>The effect for a card (normal or golden id), or null when none is registered.</summary>
    public CombatEffect? Get(string cardId, bool golden)
    {
        var effects = Lookup(cardId, golden);
        return effects.Length > 0 ? effects[0] : null;
    }

    /// <summary>
    /// True when the card's effect is simulated for this side. Effects that need a hand are not simulated for the
    /// opponent, whose hand is hidden.
    /// </summary>
    public bool Implements(string cardId, bool golden, bool isOpponent = false) =>
        Get(cardId, golden) is { } effect && !(isOpponent && effect.NeedsHand);

    /// <summary>The card's effect followed by its enchantments' effects.</summary>
    internal CombatEffect[] EffectsFor(SimMinion minion)
    {
        var card = minion.CardId.Length > 0 ? Lookup(minion.CardId, minion.Golden) : NoEffects;
        if (minion.Enchantments.Count == 0)
        {
            return card;
        }

        List<CombatEffect>? all = null;
        foreach (var enchantment in minion.Enchantments)
        {
            var effects = Lookup(enchantment, golden: false);
            if (effects.Length > 0)
            {
                (all ??= new List<CombatEffect>(card)).Add(effects[0]);
            }
        }

        return all?.ToArray() ?? card;
    }

    private CombatEffect[] Lookup(string cardId, bool golden)
    {
        if (cardId.Length == 0)
        {
            return NoEffects;
        }

        if (_normal.Count == 0 && _golden.Count == 0)
        {
            return NoEffects;
        }

        if ((golden ? _golden : _normal).TryGetValue(cardId, out var direct))
        {
            return direct;
        }

        // A golden card id passed directly.
        var (normalId, isGoldenId) = CombatRelevance.Normalize(cardId);
        return isGoldenId && _golden.TryGetValue(normalId, out var byGoldenId) ? byGoldenId : NoEffects;
    }

    /// <summary>
    /// A NormalOnly/GoldenOnly registration overrides a "both" one for that version; two registrations of the same
    /// kind for one card are a mistake (reported in <see cref="Problems"/>; the first by type name is kept).
    /// </summary>
    private void Register(Dictionary<string, CombatEffect[]> map, HashSet<string> explicitIds, string cardId, CombatEffect effect,
                          Type type, string version, bool isExplicit)
    {
        if (map.TryGetValue(cardId, out var existing))
        {
            var existingExplicit = explicitIds.Contains(cardId);
            if (existingExplicit == isExplicit)
            {
                _problems.Add($"{cardId} ({version}): registered by both {existing[0].GetType().FullName} and {type.FullName}");
                return;
            }

            if (existingExplicit)
            {
                return;
            }
        }

        map[cardId] = [effect];
        if (isExplicit)
        {
            explicitIds.Add(cardId);
        }
    }
}
