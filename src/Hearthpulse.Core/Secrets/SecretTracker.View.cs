using System.Globalization;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.CardPool;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Power;

namespace Hearthpulse.Core.Secrets;

public sealed partial class SecretTracker
{
    /// <summary>The opponent's Secrets for the overlay; null when they have none in play.</summary>
    public SecretsView? BuildView(GameState game, CardDb cards)
    {
        if (game.OpponentPlayerId == 0 || !game.IsConstructedLike)
        {
            return null;
        }

        Refresh(game);
        if (_active.Count == 0)
        {
            return null;
        }

        if (_pool is null || !ReferenceEquals(_pool.Cards, cards))
        {
            _pool = new SecretPool(cards);
        }

        HashSet<string> crossed;
        lock (_crossedGate)
        {
            crossed = new HashSet<string>(_crossed, StringComparer.Ordinal);
        }

        var format = SecretPool.FormatOf(game);
        var opponentClass = cards.Get(game.OpponentHero?.CardId)?.CardClass ?? string.Empty;
        var limitCopies = format != SecretFormat.Arena && PoolContext.SupportedGameTypes.Contains(game.GameType);
        var shown = ShownCopies(game);
        var known = _active.Values.Where(s => s.KnownCardId is not null).Select(s => s.KnownCardId!).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var slots = new List<SecretSlot>();
        var rows = new Dictionary<string, (SecretOption Option, int Possible, bool Known)>(StringComparer.Ordinal);
        var classes = new List<string>();
        foreach (var secret in _active.Values.OrderBy(s => s.EntityId))
        {
            var cardClass = ClassOf(secret, opponentClass);
            if (!classes.Contains(cardClass))
            {
                classes.Add(cardClass);
            }

            var pool = _pool.For(cardClass, format);
            var paid = _paidCost.TryGetValue(secret.EntityId, out var p) ? p : (int?)null;
            if (secret.KnownCardId is { } knownId)
            {
                var option = pool.FirstOrDefault(o => o.CardIds.Contains(knownId, StringComparer.OrdinalIgnoreCase)) ?? OptionOf(cards, knownId);
                rows[option.Key] = (option, rows.GetValueOrDefault(option.Key).Possible, true);
                slots.Add(new SecretSlot(secret.EntityId, cardClass, secret.Turn, paid, CreatorName(cards, secret), knownId, 1));
                continue;
            }

            var possible = pool
                .Where(o => o.Rule is null || !secret.Seen.Exists(s => RulesOut(o.Rule, s.Trigger, s.Held)))
                // The same Secret cannot be in play twice.
                .Where(o => !o.CardIds.Any(known.Contains))
                .Where(o => !limitCopies || !secret.FromDeck || o.CardIds.Sum(id => shown.GetValueOrDefault(id)) < (o.Legendary ? 1 : 2))
                .ToList();
            // A Secret cheaper than what they paid would have cost less; a cost increase can still explain it, so a
            // filter that leaves nothing is dropped rather than emptying the list.
            if (paid is > 0 and var cost && possible.Where(o => o.Cost >= cost).ToList() is { Count: > 0 } byCost)
            {
                possible = byCost;
            }

            foreach (var option in pool)
            {
                var entry = rows.GetValueOrDefault(option.Key);
                rows[option.Key] = (option, entry.Possible + (possible.Contains(option) ? 1 : 0), entry.Known);
            }

            slots.Add(new SecretSlot(secret.EntityId, cardClass, secret.Turn, paid, CreatorName(cards, secret), null,
                possible.Count(o => !crossed.Contains(o.Key))));
        }

        var list = rows.Values
            .Select(r => new SecretRow(r.Option.Key, r.Option.CardId, r.Option.Name, r.Option.Cost, r.Option.CardClass, r.Possible,
                crossed.Contains(r.Option.Key), r.Known))
            .OrderBy(r => r.IsOpen ? 0 : r.Crossed ? 1 : 2)
            .ThenBy(r => r.Known ? 0 : 1)
            .ThenBy(r => classes.IndexOf(r.CardClass))
            .ThenBy(r => r.Cost)
            .ThenBy(r => r.Name, StringComparer.CurrentCulture)
            .ToList();
        return new SecretsView(slots, list)
        {
            Pool = string.Join(", ", classes.Select(ConstructedMeta.ClassName)) + " · " + FormatName(format),
        };
    }

    private static string FormatName(SecretFormat format) => format switch
    {
        SecretFormat.Standard => Loc.T("Secrets.Format.Standard"),
        SecretFormat.Classic => Loc.T("Secrets.Format.Classic"),
        SecretFormat.Arena => Loc.T("Secrets.Format.Arena"),
        _ => Loc.T("Secrets.Format.Wild"),
    };

    private static SecretOption OptionOf(CardDb cards, string cardId)
    {
        var card = cards.Get(cardId);
        return new SecretOption(SecretRules.BaseId(cardId), cardId, card?.Name ?? cardId, card?.Cost ?? 0, card?.CardClass ?? string.Empty,
            card?.IsLegendary ?? false, SecretRules.For(cardId), [cardId]);
    }

    private static string ClassOf(ActiveSecret secret, string opponentClass) =>
        secret.ClassTag is { Length: > 0 } tag && tag is not ("INVALID" or "NEUTRAL") ? tag : opponentClass;

    private static string? CreatorName(CardDb cards, ActiveSecret secret) =>
        secret.CreatorId is { } id ? cards.NameOf(id) : null;

    /// <summary>Copies of each card from the opponent's starting deck the player has seen face up (played or revealed).</summary>
    private Dictionary<string, int> ShownCopies(GameState game)
    {
        var opponent = game.OpponentPlayerId;
        var shown = game.Entities.Values
            .Where(e => e.InitialController == opponent && e.IsOriginalDeckCard && e.CardId.Length > 0 && e.Zone == "GRAVEYARD")
            .GroupBy(e => e.CardId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        foreach (var secret in _active.Values)
        {
            if (secret is { KnownCardId: { } id, FromDeck: true })
            {
                shown[id] = shown.GetValueOrDefault(id) + 1;
            }
        }

        return shown;
    }
}
