namespace Hearthpulse.Core;

/// <summary>
/// A thread-safe in-memory cache limited by the total size of its values (bytes of decoded pixels for card art). The
/// least recently used values go first. Evicted values are not disposed: a control may still show one, and the GC
/// frees it once nothing does.
/// </summary>
public sealed class SizedCache<TValue>(long budget, Func<TValue, long> sizeOf)
    where TValue : class
{
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _entries = new(StringComparer.Ordinal);

    // Most recently used first.
    private readonly LinkedList<Entry> _order = new();
    private long _size;

    private sealed record Entry(string Key, TValue Value, long Size);

    /// <summary>The size limit; a single value larger than this is still kept until the next one comes in.</summary>
    public long Budget { get; } = budget;

    /// <summary>Total size of the cached values.</summary>
    public long Size
    {
        get
        {
            lock (_gate)
            {
                return _size;
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>The cached value, marked as just used.</summary>
    public bool TryGetValue(string key, out TValue value)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
        }

        value = null!;
        return false;
    }

    /// <summary>Adds or replaces the value, then drops the least recently used ones until the cache fits its budget.</summary>
    public void Set(string key, TValue value)
    {
        var size = Math.Max(0, sizeOf(value));
        lock (_gate)
        {
            if (_entries.Remove(key, out var old))
            {
                _order.Remove(old);
                _size -= old.Value.Size;
            }

            _entries[key] = _order.AddFirst(new Entry(key, value, size));
            _size += size;
            while (_size > Budget && _order.Last is { } last && last != _order.First)
            {
                _order.RemoveLast();
                _entries.Remove(last.Value.Key);
                _size -= last.Value.Size;
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _order.Clear();
            _size = 0;
        }
    }

    /// <summary>The cached keys, values and sizes, most recently used first (diagnostics).</summary>
    public IReadOnlyList<(string Key, TValue Value, long Size)> Snapshot()
    {
        lock (_gate)
        {
            return _order.Select(e => (e.Key, e.Value, e.Size)).ToList();
        }
    }
}
