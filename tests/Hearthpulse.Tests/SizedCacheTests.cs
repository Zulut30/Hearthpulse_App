using Hearthpulse.Core;

namespace Hearthpulse.Tests;

public sealed class SizedCacheTests
{
    private static SizedCache<string> Cache(long budget) => new(budget, v => v.Length);

    [Fact]
    public void The_least_recently_used_values_go_once_the_budget_is_exceeded()
    {
        var cache = Cache(10);
        cache.Set("a", "aaaa");
        cache.Set("b", "bbbb");
        Assert.True(cache.TryGetValue("a", out _)); // "a" is now newer than "b"
        cache.Set("c", "cccc");

        Assert.False(cache.TryGetValue("b", out _));
        Assert.True(cache.TryGetValue("a", out var a));
        Assert.Equal("aaaa", a);
        Assert.True(cache.TryGetValue("c", out _));
        Assert.Equal(8, cache.Size);
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void Replacing_a_value_counts_only_the_new_size()
    {
        var cache = Cache(10);
        cache.Set("a", "aaaaaaaa");
        cache.Set("a", "aa");
        cache.Set("b", "bbbbbbbb");

        Assert.Equal(10, cache.Size);
        Assert.True(cache.TryGetValue("a", out var a));
        Assert.Equal("aa", a);
    }

    [Fact]
    public void A_value_larger_than_the_budget_stays_until_the_next_one()
    {
        var cache = Cache(4);
        cache.Set("a", "aa");
        cache.Set("big", "bbbbbbbbbb");

        Assert.False(cache.TryGetValue("a", out _));
        Assert.True(cache.TryGetValue("big", out _));
        cache.Set("c", "cc");
        Assert.False(cache.TryGetValue("big", out _));
        Assert.Equal(["c"], cache.Snapshot().Select(e => e.Key));
    }

    [Fact]
    public void Snapshot_lists_the_most_recently_used_first_and_clear_empties()
    {
        var cache = Cache(100);
        cache.Set("a", "1");
        cache.Set("b", "22");
        cache.TryGetValue("a", out _);

        Assert.Equal(["a", "b"], cache.Snapshot().Select(e => e.Key));
        Assert.Equal([1L, 2L], cache.Snapshot().Select(e => e.Size));
        cache.Clear();
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.Size);
    }

    [Fact]
    public void Parallel_use_keeps_the_size_consistent()
    {
        var cache = Cache(1000);
        Parallel.For(0, 10_000, i =>
        {
            cache.Set((i % 300).ToString(System.Globalization.CultureInfo.InvariantCulture), new string('x', 1 + (i % 7)));
            cache.TryGetValue((i % 50).ToString(System.Globalization.CultureInfo.InvariantCulture), out _);
        });

        Assert.Equal(cache.Snapshot().Sum(e => e.Size), cache.Size);
        Assert.True(cache.Size <= 1000);
    }
}
