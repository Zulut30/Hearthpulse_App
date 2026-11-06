using System.Globalization;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Power;

namespace Hearthpulse.Core.Tracking.Counters;

public static partial class Counters
{
    /// <param name="Title">English title; <see cref="LocalTitle"/> is what the player reads.</param>
    /// <param name="Description">English description; <see cref="LocalDescription"/> is what the player reads.</param>
    internal sealed record CounterDef(string Id, string Title, string Description, Func<CounterContext, CounterValue?> Compute)
    {
        // Keys made once: Build reads them for every counter on every overlay update.
        private readonly string _titleKey = $"Core.Counter.{Id}.Title";
        private readonly string _descriptionKey = $"Core.Counter.{Id}.Description";

        /// <summary>The title in the interface language, read each time so a language switch shows at once.</summary>
        public string LocalTitle => Loc.Or(_titleKey, Title);

        /// <summary>The description in the interface language.</summary>
        public string LocalDescription => Loc.Or(_descriptionKey, Description);
    }

    /// <param name="Counted">Card ids of what was counted, one per count (the tooltip groups them).</param>
    /// <param name="Parts">The parts of the goal and whether each is done.</param>
    internal sealed record CounterValue(string Value, double? Progress = null, IReadOnlyList<string>? Details = null, bool Active = false,
        IReadOnlyList<string>? Counted = null, IReadOnlyList<(string CardId, bool Done)>? Parts = null, int? Goal = null);

    internal sealed record CounterContext(GameState Game, int PlayerId, Entity? Player, CounterTracker Tracker, CounterTracker.PlayerLog Log, CardDb Cards,
        string PlayerClass, int AllDied)
    {
        public int Tag(string tag) => Player?.GetInt(tag) ?? 0;

        public int Played(Func<PlayedCardInfo, bool> match) => Log.Played.Count(match);

        public int Plays(string cardId) => Log.Played.Count(p => p.CardId == cardId);

        public List<string> PlayedIds(Func<PlayedCardInfo, bool> match)
        {
            var ids = new List<string>();
            foreach (var p in Log.Played)
            {
                if (match(p))
                {
                    ids.Add(p.CardId);
                }
            }

            return ids;
        }

        public int Deaths(string cardId) => Log.Died.Count(id => id == cardId);

        /// <summary>Sum of what the player's cards add to a counter when played and when they die (<see cref="CounterCards.Sources"/>).</summary>
        public int Source(string key)
        {
            var table = CounterCards.Sources[key];
            var sum = 0;
            foreach (var p in Log.Played) sum += table.GetValueOrDefault(p.CardId).Play;
            foreach (var id in Log.Died) sum += table.GetValueOrDefault(id).Death;
            foreach (var id in Log.Destroyed) sum += table.GetValueOrDefault(id).Death;
            return sum;
        }

        /// <summary>The played, died and destroyed cards that added to <see cref="Source"/>.</summary>
        public List<string> SourceCards(string key)
        {
            var table = CounterCards.Sources[key];
            return [.. Log.Played.Where(p => table.GetValueOrDefault(p.CardId).Play != 0).Select(p => p.CardId),
                .. Log.Died.Concat(Log.Destroyed).Where(id => table.GetValueOrDefault(id).Death != 0)];
        }

        /// <summary>Turns this player has started, the current one included.</summary>
        public int OwnTurn => Game.Turn <= 0 ? 0 : Tag("FIRST_PLAYER") == 1 ? (Game.Turn + 1) / 2 : Game.Turn / 2;
    }
    /// <summary>
    /// The counters of the last update, by side and id, with the value each was built from. A counter whose value,
    /// title, description and related cards read the same is handed out again: the view is immutable, and every list in
    /// a <see cref="CounterValue"/> is made for its update (a tracker's list is copied), so equal content means an equal view.
    /// </summary>
    internal sealed class CounterViews
    {
        private readonly Dictionary<(bool Opponent, string Id), (CounterValue Value, CounterView View)> _last = [];
        private CardDb? _cards;

        /// <summary>Off: every counter is built (tests compare both ways).</summary>
        public bool Reuse { get; set; } = true;

        /// <summary>Card names come from <paramref name="cards"/>: another card set (a language switch) builds everything again.</summary>
        public void Use(CardDb cards)
        {
            if (!ReferenceEquals(cards, _cards))
            {
                _last.Clear();
                _cards = cards;
            }
        }

        public CounterView? Same(bool opponent, CounterDef def, CounterValue value, string[] related, Dictionary<string, int> seen) =>
            Reuse && _last.TryGetValue((opponent, def.Id), out var last)
                  && last.View.Title == def.LocalTitle
                  && last.View.Description == def.LocalDescription
                  && SameValue(last.Value, value)
                  && SameRelated(last.View.RelatedCards, related, seen)
                ? last.View
                : null;

        public void Keep(bool opponent, string id, CounterValue value, CounterView view)
        {
            if (Reuse)
            {
                _last[(opponent, id)] = (value, view);
            }
        }

        private static bool SameValue(CounterValue a, CounterValue b) =>
            a.Value == b.Value && a.Progress == b.Progress && a.Active == b.Active && a.Goal == b.Goal
            && SameList(a.Details, b.Details) && SameList(a.Counted, b.Counted) && SameParts(a.Parts, b.Parts);

        // Null and empty build the same view.
        private static bool SameList(IReadOnlyList<string>? a, IReadOnlyList<string>? b)
        {
            if (ReferenceEquals(a, b))
            {
                return true;
            }

            if ((a?.Count ?? 0) != (b?.Count ?? 0))
            {
                return false;
            }

            for (var i = 0; i < (a?.Count ?? 0); i++)
            {
                if (!string.Equals(a![i], b![i], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool SameParts(IReadOnlyList<(string CardId, bool Done)>? a, IReadOnlyList<(string CardId, bool Done)>? b)
        {
            if ((a?.Count ?? 0) != (b?.Count ?? 0))
            {
                return false;
            }

            for (var i = 0; i < (a?.Count ?? 0); i++)
            {
                if (a![i].Done != b![i].Done || !string.Equals(a[i].CardId, b[i].CardId, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Whether <see cref="RelatedCards"/> would list what <paramref name="built"/> lists, without building it.</summary>
        private static bool SameRelated(IReadOnlyList<CounterCard> built, string[] related, Dictionary<string, int> seen)
        {
            var at = 0;
            for (var i = 0; i < related.Length; i++)
            {
                var id = related[i];
                if (!seen.TryGetValue(id, out var copies) || Array.IndexOf(related, id, 0, i) >= 0)
                {
                    continue;
                }

                if (at >= built.Count || built[at].CardId != id || built[at].Count != copies)
                {
                    return false;
                }

                at++;
            }

            return at == built.Count;
        }
    }
}
