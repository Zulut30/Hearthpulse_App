#if HEARTHPULSE_WPF
using Hearthpulse.App.Infrastructure;
#else
using Hearthpulse.UI.Infrastructure;
#endif
using System.Collections.ObjectModel;
using Hearthpulse.Core.Replay;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main.Match;
#else
namespace Hearthpulse.UI.Main.Match;
#endif

/// <summary>
/// A row or card of the match page that stays the same object from step to step: a step hands it the new view
/// (<see cref="Update"/>) and it tells the bindings only about the properties that differ. Replacing the item in its
/// list instead made the items control build the row's template again: on the Constructed table that and the rebinding
/// of the whole table were about a quarter of a step's UI time, and the new rows' text most of its layout.
/// </summary>
public abstract class MatchItem<TView> : Observable
    where TView : class
{
    protected MatchItem(TView view) => View = view;

    /// <summary>What the item shows now (the step's record, as Core built it).</summary>
    public TView View { get; private set; }

    /// <summary>Shows another view in place; false when nothing differs.</summary>
    internal bool Update(TView view)
    {
        var old = View;
        if (ReferenceEquals(old, view) || old.Equals(view))
        {
            return false;
        }

        View = view;
        RaiseChanges(old, view);
        return true;
    }

    /// <summary>Raises the properties whose values differ between the two views.</summary>
    protected abstract void RaiseChanges(TView old, TView now);

    protected void Changed<T>(T old, T now, string name)
    {
        if (!EqualityComparer<T>.Default.Equals(old, now))
        {
            Raise(name);
        }
    }
}

/// <summary>A card of the Constructed table (board, hand, Secrets, weapon, hero power): <see cref="MatchCardView"/> in place.</summary>
public sealed class MatchCardItem(MatchCardView view) : MatchItem<MatchCardView>(view)
{
    public string? CardId => View.CardId;

    public string Name => View.Name;

    public bool CardBack => View.CardBack;

    public bool Revealed => View.Revealed;

    public bool Picked => View.Picked;

    public bool Dimmed => View.Dimmed;

    public string ChoiceName => View.ChoiceName;

    public int Attack => View.Attack;

    public int Health => View.Health;

    public int Cost => View.Cost;

    public bool ShowStats => View.ShowStats;

    public bool Damaged => View.Damaged;

    public bool Golden => View.Golden;

    public bool Taunt => View.Taunt;

    public bool DivineShield => View.DivineShield;

    public string Note => View.Note;

    public string Tooltip => View.Tooltip;

    public int Ref => View.Ref;

    public bool Acting => View.Acting;

    public bool Targeted => View.Targeted;

    public int Change => View.Change;

    public bool Dying => View.Dying;

    public string ChangeText => View.ChangeText;

    public string ChangeTip => View.ChangeTip;

    /// <summary>The same card on the next step: its replay id, or without one (a card back with no id) its card.</summary>
    internal static (int Ref, string? CardId) KeyOf(MatchCardView view) => view.Ref != 0 ? (view.Ref, null) : (0, view.CardId);

    protected override void RaiseChanges(MatchCardView old, MatchCardView now)
    {
        Changed(old.CardId, now.CardId, nameof(CardId));
        Changed(old.Name, now.Name, nameof(Name));
        Changed(old.CardBack, now.CardBack, nameof(CardBack));
        Changed(old.Revealed, now.Revealed, nameof(Revealed));
        Changed(old.Picked, now.Picked, nameof(Picked));
        Changed(old.Dimmed, now.Dimmed, nameof(Dimmed));
        if (old.Picked != now.Picked || old.Name != now.Name)
        {
            Raise(nameof(ChoiceName));
        }

        Changed(old.Attack, now.Attack, nameof(Attack));
        Changed(old.Health, now.Health, nameof(Health));
        Changed(old.Cost, now.Cost, nameof(Cost));
        Changed(old.ShowStats, now.ShowStats, nameof(ShowStats));
        Changed(old.Damaged, now.Damaged, nameof(Damaged));
        Changed(old.Golden, now.Golden, nameof(Golden));
        Changed(old.Taunt, now.Taunt, nameof(Taunt));
        Changed(old.DivineShield, now.DivineShield, nameof(DivineShield));
        Changed(old.Note, now.Note, nameof(Note));
        Changed(old.Tooltip, now.Tooltip, nameof(Tooltip));
        Changed(old.Ref, now.Ref, nameof(Ref));
        Changed(old.Acting, now.Acting, nameof(Acting));
        Changed(old.Targeted, now.Targeted, nameof(Targeted));
        Changed(old.Dying, now.Dying, nameof(Dying));
        if (old.Change != now.Change)
        {
            Raise(nameof(Change));
            Raise(nameof(ChangeText));
            Raise(nameof(ChangeTip));
        }
    }
}

/// <summary>A card of the Battlegrounds table (boards, Bob's offer, hand, hero powers, trinkets): <see cref="MatchBgCardView"/> in place.</summary>
public sealed class MatchBgCardItem(MatchBgCardView view) : MatchItem<MatchBgCardView>(view)
{
    public string CardId => View.CardId;

    public string Name => View.Name;

    public int Attack => View.Attack;

    public int Health => View.Health;

    public bool ShowStats => View.ShowStats;

    public bool Golden => View.Golden;

    public bool Damaged => View.Damaged;

    public bool Taunt => View.Taunt;

    public bool DivineShield => View.DivineShield;

    public bool Reborn => View.Reborn;

    public bool Venomous => View.Venomous;

    public bool Windfury => View.Windfury;

    public bool Stealth => View.Stealth;

    public bool Frozen => View.Frozen;

    public int Tier => View.Tier;

    public bool Attacking => View.Attacking;

    public bool Defending => View.Defending;

    public bool Dying => View.Dying;

    public bool Acting => View.Acting;

    public string Tooltip => View.Tooltip;

    /// <summary>
    /// The Battlegrounds file has no card ids of its own: the same card on the next step is the one with the same card id
    /// (and golden or not) at its place from either end of the row.
    /// </summary>
    internal static (string CardId, bool Golden) KeyOf(MatchBgCardView view) => (view.CardId, view.Golden);

    protected override void RaiseChanges(MatchBgCardView old, MatchBgCardView now)
    {
        Changed(old.CardId, now.CardId, nameof(CardId));
        Changed(old.Name, now.Name, nameof(Name));
        Changed(old.Attack, now.Attack, nameof(Attack));
        Changed(old.Health, now.Health, nameof(Health));
        Changed(old.ShowStats, now.ShowStats, nameof(ShowStats));
        Changed(old.Golden, now.Golden, nameof(Golden));
        Changed(old.Damaged, now.Damaged, nameof(Damaged));
        Changed(old.Taunt, now.Taunt, nameof(Taunt));
        Changed(old.DivineShield, now.DivineShield, nameof(DivineShield));
        Changed(old.Reborn, now.Reborn, nameof(Reborn));
        Changed(old.Venomous, now.Venomous, nameof(Venomous));
        Changed(old.Windfury, now.Windfury, nameof(Windfury));
        Changed(old.Stealth, now.Stealth, nameof(Stealth));
        Changed(old.Frozen, now.Frozen, nameof(Frozen));
        Changed(old.Tier, now.Tier, nameof(Tier));
        Changed(old.Attacking, now.Attacking, nameof(Attacking));
        Changed(old.Defending, now.Defending, nameof(Defending));
        Changed(old.Dying, now.Dying, nameof(Dying));
        Changed(old.Acting, now.Acting, nameof(Acting));
        Changed(old.Tooltip, now.Tooltip, nameof(Tooltip));
    }
}

/// <summary>
/// A row of the Constructed game's event feed, built once per game: a step only moves <see cref="Current"/> (the step's
/// row and the header of its turn), the row and its container stay.
/// </summary>
public sealed class MatchFeedItem(MatchFeedRow row) : Observable
{
    private bool _current = row.Current;

    public int Frame => row.Frame;

    public string Icon => row.Icon;

    public string Text => row.Text;

    public bool Local => row.Local;

    public string? CardId => row.CardId;

    public bool Header => row.Header;

    public bool Key => row.Key;

    public string? KeyReason => row.KeyReason;

    public bool Mulligan => row.Mulligan;

    /// <summary>The step on screen is this action's, or this header's turn holds it.</summary>
    public bool Current { get => _current; internal set => Set(ref _current, value); }

    /// <summary>The row as Core marks it for the step on screen.</summary>
    public MatchFeedRow Row => row.Current == _current ? row : row with { Current = _current };
}

/// <summary>An event of the Battlegrounds stretch on screen ("This turn" / "This combat"): <see cref="MatchActionRow"/> in place.</summary>
public sealed class MatchActionItem(MatchActionRow view) : MatchItem<MatchActionRow>(view)
{
    public int Frame => View.Frame;

    public string Icon => View.Icon;

    public string Text => View.Text;

    public bool Local => View.Local;

    public string? CardId => View.CardId;

    public bool Current => View.Current;

    public bool Key => View.Key;

    public string? KeyReason => View.KeyReason;

    protected override void RaiseChanges(MatchActionRow old, MatchActionRow now)
    {
        Changed(old.Frame, now.Frame, nameof(Frame));
        Changed(old.Icon, now.Icon, nameof(Icon));
        Changed(old.Text, now.Text, nameof(Text));
        Changed(old.Local, now.Local, nameof(Local));
        Changed(old.CardId, now.CardId, nameof(CardId));
        Changed(old.Current, now.Current, nameof(Current));
        Changed(old.Key, now.Key, nameof(Key));
        Changed(old.KeyReason, now.KeyReason, nameof(KeyReason));
    }
}

/// <summary>The lists of the match page's tables: the items stay, a step changes them in place.</summary>
internal static class MatchItems
{
    /// <summary>
    /// Makes <paramref name="list"/> show <paramref name="views"/> with as few new items as possible. The same cards at
    /// both ends (by <paramref name="key"/>) keep their items; between them the items are reused where they stand, then
    /// the surplus is removed or the missing ones are inserted (a card bought into the middle of a board inserts one
    /// item and moves none). False when nothing changed.
    /// </summary>
    public static bool Sync<TItem, TView, TKey>(ObservableCollection<TItem> list, IReadOnlyList<TView> views, Func<TView, TKey> key, Func<TView, TItem> create)
        where TItem : MatchItem<TView>
        where TView : class
    {
        var comparer = EqualityComparer<TKey>.Default;
        var changed = false;
        var start = 0;
        while (start < list.Count && start < views.Count && comparer.Equals(key(list[start].View), key(views[start])))
        {
            changed |= list[start].Update(views[start]);
            start++;
        }

        var oldEnd = list.Count;
        var newEnd = views.Count;
        while (oldEnd > start && newEnd > start && comparer.Equals(key(list[oldEnd - 1].View), key(views[newEnd - 1])))
        {
            changed |= list[oldEnd - 1].Update(views[newEnd - 1]);
            oldEnd--;
            newEnd--;
        }

        var overlap = Math.Min(oldEnd, newEnd) - start;
        for (var i = start; i < start + overlap; i++)
        {
            changed |= list[i].Update(views[i]);
        }

        for (var i = oldEnd - 1; i >= start + overlap; i--)
        {
            list.RemoveAt(i);
            changed = true;
        }

        for (var i = start + overlap; i < newEnd; i++)
        {
            list.Insert(i, create(views[i]));
            changed = true;
        }

        return changed;
    }

    public static bool Sync(ObservableCollection<MatchCardItem> list, IReadOnlyList<MatchCardView> views) =>
        Sync(list, views, MatchCardItem.KeyOf, static view => new MatchCardItem(view));

    public static bool Sync(ObservableCollection<MatchBgCardItem> list, IReadOnlyList<MatchBgCardView> views) =>
        Sync(list, views, MatchBgCardItem.KeyOf, static view => new MatchBgCardItem(view));

    public static bool Sync(ObservableCollection<MatchActionItem> list, IReadOnlyList<MatchActionRow> views) =>
        Sync(list, views, static view => view.Frame, static view => new MatchActionItem(view));

    /// <summary>A single card slot (weapon, hero power): the item stays while there is a card, null without one.</summary>
    public static MatchCardItem? Slot(MatchCardItem? item, MatchCardView? view)
    {
        if (view is null)
        {
            return null;
        }

        if (item is null)
        {
            return new MatchCardItem(view);
        }

        item.Update(view);
        return item;
    }
}
