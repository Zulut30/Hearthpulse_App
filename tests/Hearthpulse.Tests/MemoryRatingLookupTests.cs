using Hearthpulse.Core.Memory;

namespace Hearthpulse.Tests;

/// <summary>
/// When the Battlegrounds rating read falls back to looking for another NetCache class, and which object it may take.
/// </summary>
public sealed class MemoryRatingLookupTests
{
    [Fact]
    public void Unrated_player_does_not_trigger_the_search()
    {
        // NetCacheBaconRatingInfo is there but holds 0/0: nothing is read, and nothing is searched for either.
        var read = HearthstoneMemory.RatingFrom(0, 0);

        Assert.Null(read);
        Assert.False(HearthstoneMemory.ShouldSearchForRating(read, knownClassLoaded: true));
    }

    [Fact]
    public void Search_runs_only_when_the_known_class_is_gone_and_nothing_was_read()
    {
        Assert.True(HearthstoneMemory.ShouldSearchForRating(null, knownClassLoaded: false));
        Assert.False(HearthstoneMemory.ShouldSearchForRating(new BattlegroundsRating(6500, 0), knownClassLoaded: false));
        Assert.False(HearthstoneMemory.ShouldSearchForRating(new BattlegroundsRating(6500, 0), knownClassLoaded: true));
    }

    [Fact]
    public void Several_rating_caches_give_no_candidate()
    {
        string?[] names = ["NetCacheProfileProgress", "NetCacheBaconRatingInfoV2", null, "NetCacheBattlegroundsRating"];

        Assert.Null(HearthstoneMemory.SingleRatingCache(names));
    }

    [Fact]
    public void Two_objects_of_the_same_rating_class_give_no_candidate()
    {
        string?[] names = ["NetCacheBaconRatingInfoV2", "NetCacheBaconRatingInfoV2"];

        Assert.Null(HearthstoneMemory.SingleRatingCache(names));
    }

    [Fact]
    public void The_single_rating_cache_is_found_and_constructed_ladder_caches_are_ignored()
    {
        string?[] names = ["NetCacheMedalInfo", "NetCacheRatingInfo", "NetCacheBaconRatingInfoV2", "NetCacheProfileProgress"];

        Assert.Equal(2, HearthstoneMemory.SingleRatingCache(names));
        Assert.Null(HearthstoneMemory.SingleRatingCache(["NetCacheMedalInfo", "NetCacheRatingInfo"]));
    }
}
