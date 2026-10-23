using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Power;

namespace Hearthpulse.Core.Tracking;

/// <summary>Community mulligan numbers for one card in the opening hand.</summary>
/// <param name="KeepRate">Share of players who kept the card when offered it.</param>
/// <param name="WinRate">Win rate of games where the card was in hand after the mulligan.</param>
/// <param name="Delta">WinRate minus the deck's (or class's) average win rate: above 0 favours keeping.</param>
/// <param name="Sample">Games the card was offered in.</param>
public sealed record MulliganCardStat(string CardId, string Name, double? KeepRate, double? WinRate, double? Delta, int Sample);

/// <summary>The mulligan panel: whose numbers they are and one row per offered card.</summary>
public sealed record MulliganView(string Context, double BaseWinRate, IReadOnlyList<MulliganCardStat> Cards, string Source);

/// <summary>
/// Builds the mulligan panel (HSReplay style) while the local player chooses their opening hand: keep rate and win rate
/// with the card in hand, for the archetype the deck matches, or for the whole class when the deck is unknown.
/// </summary>
public static class Mulligan
{
    /// <summary>Fewer offers than this and the numbers are too noisy to show.</summary>
    public const int MinSample = 30;

    public static bool InProgress(GameState game) =>
        game.LocalPlayerEntity?.GetString(GameTag.MulliganState) is "INPUT" or "DEALING";

    public static MulliganView? Build(GameState game, CardDb cards, DeckDefinition? deck, ConstructedMeta? meta, string playerClass)
    {
        if (meta is null || !InProgress(game) || !FormatMatches(game.FormatType, meta.Format))
        {
            return null;
        }

        var offered = game.Controlled(game.LocalPlayerId, "HAND")
            .Where(e => e.CardId.Length > 0 && e.CardId != "GAME_005")
            .OrderBy(e => e.ZonePosition)
            .ToList();
        if (offered.Count == 0)
        {
            return null;
        }

        var deckIds = deck?.Cards.Keys.Select(dbf => cards.Get(dbf)?.Id).OfType<string>().ToList() ?? [];
        var archetype = deckIds.Count > 0 ? meta.Match(playerClass, deckIds) : null;
        string context;
        double baseRate;
        Dictionary<string, (int Drawn, double Kept, int InHand, double Wins)> stats;
        if (archetype is not null)
        {
            context = archetype.Name;
            baseRate = archetype.WinRate;
            stats = archetype.Mulligan.ToDictionary(m => m.CardId, m => (m.Drawn, m.KeepRate * m.Drawn, m.InHand, m.InHandWinRate * m.InHand), StringComparer.Ordinal);
        }
        else
        {
            // No matching archetype: pool every archetype of the class, weighted by how often each card was offered.
            var archetypes = meta.ForClass(playerClass).ToList();
            if (archetypes.Count == 0)
            {
                return null;
            }

            context = $"{Title(playerClass)} (all decks)";
            var games = archetypes.Sum(a => a.Games);
            baseRate = games > 0 ? archetypes.Sum(a => a.WinRate * a.Games) / games : 0.5;
            stats = new(StringComparer.Ordinal);
            foreach (var m in archetypes.SelectMany(a => a.Mulligan))
            {
                var sum = stats.GetValueOrDefault(m.CardId);
                stats[m.CardId] = (sum.Drawn + m.Drawn, sum.Kept + m.KeepRate * m.Drawn, sum.InHand + m.InHand, sum.Wins + m.InHandWinRate * m.InHand);
            }
        }

        var rows = offered.Select(e =>
        {
            var name = cards.NameOf(e.CardId);
            if (!stats.TryGetValue(e.CardId, out var s) || s.Drawn < MinSample)
            {
                return new MulliganCardStat(e.CardId, name, null, null, null, s.Drawn);
            }

            double? winRate = s.InHand > 0 ? s.Wins / s.InHand : null;
            return new MulliganCardStat(e.CardId, name, s.Kept / s.Drawn, winRate, winRate - baseRate, s.Drawn);
        }).ToList();
        return new MulliganView(context, baseRate, rows, meta.Source);
    }

    private static bool FormatMatches(string formatType, string metaFormat) => formatType switch
    {
        "FT_STANDARD" => metaFormat == "standard",
        "FT_WILD" => metaFormat == "wild",
        _ => false,
    };

    private static string Title(string upper) => upper.Length == 0 ? upper : upper[0] + upper[1..].ToLowerInvariant();
}
