using System.Collections.Concurrent;

namespace Hearthpulse.App.Infrastructure;

/// <summary>
/// Card art that had no file and no download, by key, until the time it may be asked for again. A key leaves as soon as
/// its art is loaded, and every key leaves when downloads become possible (<see cref="Clear"/>); past
/// <paramref name="capacity"/> keys the list starts again.
/// </summary>
internal sealed class ArtMisses(long retryMs, int capacity, Func<long> clock)
{
    private readonly ConcurrentDictionary<string, long> _until = new(StringComparer.Ordinal);

    public int Count => _until.Count;

    /// <summary>True while <paramref name="key"/> is remembered as missing and its window has not passed.</summary>
    public bool Skips(string key)
    {
        if (!_until.TryGetValue(key, out var retryAt))
        {
            return false;
        }

        if (clock() < retryAt)
        {
            return true;
        }

        _until.TryRemove(key, out _);
        return false;
    }

    public void Miss(string key)
    {
        if (_until.Count >= capacity)
        {
            _until.Clear();
        }

        _until[key] = clock() + retryMs;
    }

    /// <summary>The art of <paramref name="key"/> is loaded: it is no longer missing.</summary>
    public void Found(string key) => _until.TryRemove(key, out _);

    public void Clear() => _until.Clear();
}
