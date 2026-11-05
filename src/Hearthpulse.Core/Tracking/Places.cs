namespace Hearthpulse.Core.Tracking;

/// <summary>Battlegrounds places in a list: players without a place yet (0) go after everyone who has one.</summary>
public static class Places
{
    /// <summary>The sort key of a place: the place itself, or one past any real place when there is none yet.</summary>
    public static int SortKey(int place) => place <= 0 ? 99 : place;
}
