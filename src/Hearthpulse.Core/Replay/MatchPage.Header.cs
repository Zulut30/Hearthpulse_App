using System.Globalization;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Memory;
using Hearthpulse.Core.Stats;

namespace Hearthpulse.Core.Replay;

public sealed partial class MatchPage
{
    private static MatchHeader BuildHeader(GameRecord game, CardDb cards, ReplayHeader? replay, ArchetypeTranslations names)
    {
        var bg = game.IsBattlegrounds;
        var local = new MatchPlayerView(
            StripTag(game.PlayerName),
            game.HeroCardId,
            cards.NameOf(game.HeroCardId),
            bg ? ModeText(game.Mode) : ConstructedMeta.ClassName(ClassOf(game, cards)),
            bg ? Rating(game) : RankInfo.Localize(replay?.LocalRank ?? game.Rank) ?? string.Empty);
        var opponent = new MatchPlayerView(
            bg ? Loc.T("Match.Lobby") : StripTag(game.OpponentName) is { Length: > 0 } name && !name.StartsWith("UNKNOWN", StringComparison.Ordinal) ? name : Loc.T("Match.Opponent"),
            game.OpponentHeroCardId,
            cards.NameOf(game.OpponentHeroCardId),
            ConstructedMeta.ClassName(game.OpponentClass),
            RankInfo.Localize(replay?.OpponentRank) ?? string.Empty);
        var top = game.Mode == "Duos" ? 2 : 4;
        var (result, key) = game.IsAbandoned ? (Loc.T("Main.Result.Abandoned"), game.Result)
            : bg ? (Loc.F("Match.Header.Place", Ordinal(game.Placement)), game.Placement is > 0 && game.Placement <= top ? "Win" : "Loss")
            : (ResultText(game.Result), game.Result);
        var format = game.Format is { Length: > 0 } f ? Loc.Or("Match.Format." + f, string.Empty) : string.Empty;
        var title = bg ? Loc.F("Match.Header.Title", cards.NameOf(game.HeroCardId), ModeText(game.Mode))
            : Loc.F("Match.Header.Versus", local.Class.Length > 0 ? local.Class : game.DeckName ?? Loc.T("Match.You"),
                opponent.Class.Length > 0 ? opponent.Class : Loc.T("Match.Opponent"));
        var subtitle = string.Join(" · ", new[]
        {
            ModeText(game.Mode),
            bg ? null : format,
            game.StartedAt.ToString("d MMM yyyy, HH:mm", Loc.DateCulture),
        }.Where(x => !string.IsNullOrEmpty(x)));
        var duration = game.Duration > TimeSpan.Zero ? $"{(int)game.Duration.TotalMinutes}:{game.Duration.Seconds:00}" : "—";
        var facts = new List<MatchFact>
        {
            new(Loc.T("Match.Fact.Duration"), duration),
            new(Loc.T(bg ? "Match.Fact.Rounds" : "Match.Fact.Turns"), game.Turns.ToString(CultureInfo.CurrentCulture)),
        };
        if (!bg)
        {
            facts.Add(new MatchFact(Loc.T("Match.Fact.Coin"), game.WentFirst switch
            {
                true => Loc.T("Match.Fact.WentFirst"),
                false => Loc.T("Match.Fact.HadCoin"),
                _ => "—",
            }));
            if (game.DeckName is { Length: > 0 } deck)
            {
                facts.Add(new MatchFact(Loc.T("Match.Fact.Deck"), deck));
            }

            if (game.OpponentArchetype is { Length: > 0 } archetype)
            {
                facts.Add(new MatchFact(Loc.T("Match.Fact.OpponentDeck"), names.Display(archetype)));
            }
        }
        else
        {
            facts.Add(new MatchFact(Loc.T("Match.Fact.TavernTier"), game.TavernTier.ToString(CultureInfo.CurrentCulture)));
            if (game.RatingDelta is { } delta)
            {
                facts.Add(new MatchFact(Loc.T("Match.Fact.Rating"), delta >= 0 ? $"+{delta}" : $"−{-delta}"));
            }
        }

        return new MatchHeader(title, subtitle, result, key, local, opponent) { Facts = facts };
    }

    /// <summary>
    /// The player's class: the row's, else the hero's, else the deck's (a hero card played mid-game, like a neutral
    /// Deathwing, replaces the class hero the row names).
    /// </summary>
    private static string ClassOf(GameRecord game, CardDb cards)
    {
        static bool Known(string? c) => !string.IsNullOrEmpty(c) && c != "NEUTRAL";
        if (Known(game.PlayerClass))
        {
            return game.PlayerClass;
        }

        if (cards.Get(game.HeroCardId)?.CardClass is { } heroClass && Known(heroClass))
        {
            return heroClass;
        }

        var deck = game.DeckCode is { Length: > 0 } code ? DeckString.TryDecode(code) : null;
        return deck?.HeroDbfIds.Select(cards.Get).FirstOrDefault(c => Known(c?.CardClass))?.CardClass ?? string.Empty;
    }

    private static string Rating(GameRecord game) =>
        game.RatingBefore is { } before
            ? game.RatingAfter is { } after ? $"{before:N0} → {after:N0}" : $"{before:N0}"
            : string.Empty;

    private static string StripTag(string name) => name.Split('#')[0];

    private static string Ordinal(int n) => n switch
    {
        <= 0 => "—",
        1 => Loc.T("Main.Place.1"),
        2 => Loc.T("Main.Place.2"),
        3 => Loc.T("Main.Place.3"),
        _ => Loc.F("Main.Place.N", n),
    };

    /// <summary>A history result ("Win", "Loss", "Tie", "Unknown") as the player reads it.</summary>
    private static string ResultText(string result) => result switch
    {
        "Win" => Loc.T("Main.Result.Win"),
        "Loss" => Loc.T("Main.Result.Loss"),
        "Tie" => Loc.T("Main.Result.Tie"),
        "Unknown" => Loc.T("Main.Result.Unknown"),
        _ => result,
    };

    /// <summary>A history mode ("Ranked", "Battlegrounds"...) as the player reads it.</summary>
    private static string ModeText(string mode) => Loc.Or("Main.Mode." + mode, mode);
}
