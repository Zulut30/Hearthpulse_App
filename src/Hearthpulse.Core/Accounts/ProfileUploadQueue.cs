using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hearthpulse.Core.Accounts;

/// <summary>An event taken from the queue for sending, with the version of the queue entry it came from.</summary>
public sealed record QueuedEvent(ProfileEvent Event, long Version);

/// <summary>
/// Events waiting for the site, kept in <c>profile-upload.json</c> in the data folder so games played offline, or left
/// when the app closed, go out later. The ids the site has already settled (accepted or rejected) are appended to
/// <c>profile-upload.json.settled</c>, so a backfill does not send them again; the backfill's cursor (the last history row
/// it has queued) lives in the queue file too. Everything belongs to one site account: another account starts empty.
/// </summary>
/// <remarks>
/// Thread-safe. Lookups go through an index by event id. Changes are written in batches: <see cref="Flush"/> writes the
/// waiting events when something changed (atomically: temp file, then move), and settled ids are appended as they come.
/// Losing unflushed changes in a crash only means sending an event again, which the site acknowledges.
/// </remarks>
public sealed class ProfileUploadQueue
{
    /// <summary>Events kept waiting at most; past it the oldest are dropped (and logged).</summary>
    public const int MaxPending = 5_000;

    /// <summary>Settled ids remembered at most (oldest forgotten first; the site would only acknowledge them again).</summary>
    public const int MaxSettled = 50_000;

    private const int FormatVersion = 2;

    private readonly string _path;
    private readonly string _settledPath;
    private readonly object _gate = new();
    private readonly LinkedList<Item> _pending = new();
    private readonly Dictionary<string, LinkedListNode<Item>> _index = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _settledOrder = new();
    private readonly HashSet<string> _settled = new(StringComparer.OrdinalIgnoreCase);
    private string? _owner;
    private long? _backfillAfter;
    private long _version;
    private bool _dirty;

    public ProfileUploadQueue(string path)
    {
        _path = path;
        _settledPath = path + ".settled";
        Load();
    }

    /// <summary>The site account the queue belongs to (its user id), or null before the first sign-in.</summary>
    public string? Owner
    {
        get
        {
            lock (_gate)
            {
                return _owner;
            }
        }
    }

    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>
    /// The backfill's cursor: the id of the last history row it has queued (0 before the first), or null when no backfill
    /// is under way.
    /// </summary>
    public long? BackfillAfter
    {
        get
        {
            lock (_gate)
            {
                return _backfillAfter;
            }
        }
        set
        {
            lock (_gate)
            {
                if (_backfillAfter != value)
                {
                    _backfillAfter = value;
                    _dirty = true;
                }
            }
        }
    }

    /// <summary>
    /// Makes the queue <paramref name="userId"/>'s. A different account than before starts empty: idempotency on the
    /// site is per account, and one player's games must not go to another's profile. Written at once.
    /// </summary>
    public void Adopt(string userId)
    {
        lock (_gate)
        {
            if (_owner == userId)
            {
                return;
            }

            if (_owner is not null && (_pending.Count > 0 || _settled.Count > 0))
            {
                Diag.Info($"profile upload: another account signed in, {_pending.Count.ToString(CultureInfo.InvariantCulture)} queued games dropped");
            }

            _owner = userId;
            _pending.Clear();
            _index.Clear();
            _settled.Clear();
            _settledOrder.Clear();
            _backfillAfter = null;
            DeleteSettledFile();
            _dirty = true;
            FlushLocked();
        }
    }

    /// <summary>Whether the site has settled the event or it is already waiting.</summary>
    public bool Knows(string eventId)
    {
        lock (_gate)
        {
            return _settled.Contains(eventId) || _index.ContainsKey(eventId);
        }
    }

    /// <summary>
    /// Queues events, not before <paramref name="notBefore"/>. An event already waiting is replaced in place (a game whose
    /// rating arrived later); one the site has settled is skipped. Returns how many are waiting now that were not before
    /// or were replaced (events dropped for the size bound are not counted). Call <see cref="Flush"/> afterwards.
    /// </summary>
    public int Enqueue(IEnumerable<ProfileEvent> events, DateTime notBefore, string? owner = null)
    {
        lock (_gate)
        {
            if (owner is not null && owner != _owner) return 0;
            var queued = new List<string>();
            foreach (var e in events)
            {
                if (_settled.Contains(e.EventId))
                {
                    continue;
                }

                var item = new Item(e, notBefore, Encoding.UTF8.GetByteCount(e.ToJson().ToJsonString()) + 1, ++_version);
                if (_index.TryGetValue(e.EventId, out var node))
                {
                    node.Value = item;
                }
                else
                {
                    _index[e.EventId] = _pending.AddLast(item);
                }

                queued.Add(e.EventId);
                _dirty = true;
            }

            var dropped = 0;
            while (_pending.Count > MaxPending && _pending.First is { } oldest)
            {
                _index.Remove(oldest.Value.Event.EventId);
                _pending.RemoveFirst();
                dropped++;
            }

            if (dropped > 0)
            {
                Diag.Info($"profile upload: queue full, {dropped.ToString(CultureInfo.InvariantCulture)} oldest games dropped");
            }

            return queued.Count(_index.ContainsKey);
        }
    }

    /// <summary>Drops every waiting event and any backfill under way (the player turned sending off); written at once.</summary>
    public void ClearPending()
    {
        lock (_gate)
        {
            if (_pending.Count > 0)
            {
                Diag.Info($"profile upload: sending turned off, {_pending.Count.ToString(CultureInfo.InvariantCulture)} queued games dropped");
            }

            if (_pending.Count > 0 || _backfillAfter is not null)
            {
                _pending.Clear();
                _index.Clear();
                _backfillAfter = null;
                _dirty = true;
                FlushLocked();
            }
        }
    }

    /// <summary>
    /// The next batch to send: due events in queue order, at most <paramref name="maxEvents"/> and
    /// <paramref name="maxBytes"/> of JSON (always at least one event when any is due).
    /// </summary>
    public IReadOnlyList<QueuedEvent> NextBatch(DateTime now, int maxEvents, int maxBytes, string? owner = null)
    {
        lock (_gate)
        {
            if (owner is not null && owner != _owner) return [];
            var batch = new List<QueuedEvent>();
            var bytes = 16L;
            foreach (var item in _pending)
            {
                if (item.NotBefore > now)
                {
                    continue;
                }

                if (batch.Count > 0 && (batch.Count >= maxEvents || bytes + item.Bytes > maxBytes))
                {
                    break;
                }

                batch.Add(new QueuedEvent(item.Event, item.Version));
                bytes += item.Bytes;
            }

            return batch;
        }
    }

    /// <summary>When the earliest held-back event becomes due, or null when nothing waits.</summary>
    public DateTime? NextDue()
    {
        lock (_gate)
        {
            return _pending.Count == 0 ? null : _pending.Min(p => p.NotBefore);
        }
    }

    /// <summary>
    /// Records that the site settled these ids (accepted, rejected or dropped) and removes the waiting entries they were
    /// sent from. An entry replaced while it was being sent (a newer version) stays: the site keeps the first copy of an
    /// id, so it goes out only to be acknowledged, and that is logged.
    /// </summary>
    public void Settle(IEnumerable<(string EventId, long Version)> sent, string? owner = null)
    {
        lock (_gate)
        {
            // A reply can arrive after another account adopted the queue. Its acknowledgements belong to the old owner.
            if (owner is not null && owner != _owner) return;
            var appended = new List<string>();
            foreach (var (id, version) in sent)
            {
                if (_index.TryGetValue(id, out var node))
                {
                    if (node.Value.Version == version)
                    {
                        _pending.Remove(node);
                        _index.Remove(id);
                    }
                    else
                    {
                        Diag.Info($"profile upload: {id} changed while it was being sent; the site keeps the copy it got first");
                    }

                    _dirty = true;
                }

                if (_settled.Add(id))
                {
                    _settledOrder.AddLast(id);
                    appended.Add(id);
                }
            }

            AppendSettled(appended);
        }
    }

    /// <summary>Writes the waiting events and the backfill cursor if anything changed since the last write.</summary>
    public void Flush()
    {
        lock (_gate)
        {
            FlushLocked();
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                if (JsonNode.Parse(File.ReadAllText(_path)) is not JsonObject root || (int?)root["version"] != FormatVersion)
                {
                    Diag.Info("profile upload: queue file in an unknown format, starting empty");
                    DeleteSettledFile();
                    return;
                }

                _owner = (string?)root["owner"];
                _backfillAfter = (long?)root["backfillAfter"];
                foreach (var node in root["pending"] as JsonArray ?? [])
                {
                    if (node is JsonObject item && (string?)item["eventId"] is { Length: > 0 } id && (string?)item["type"] is { Length: > 0 } type
                        && (string?)item["occurredAt"] is { Length: > 0 } at && item["payload"] is JsonObject payload && !_index.ContainsKey(id))
                    {
                        var notBefore = DateTime.TryParse((string?)item["notBefore"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var nb)
                            ? nb
                            : DateTime.MinValue;
                        var e = new ProfileEvent(id, type, at, (JsonObject)payload.DeepClone());
                        _index[id] = _pending.AddLast(new Item(e, notBefore, Encoding.UTF8.GetByteCount(e.ToJson().ToJsonString()) + 1, ++_version));
                    }
                }
            }

            if (File.Exists(_settledPath))
            {
                foreach (var id in File.ReadLines(_settledPath))
                {
                    if (id.Length > 0 && _settled.Add(id))
                    {
                        _settledOrder.AddLast(id);
                    }
                }

                // The append-only file grows; past the bound it is rewritten with the newest ids.
                if (_settled.Count > MaxSettled)
                {
                    while (_settled.Count > MaxSettled && _settledOrder.First is { } oldest)
                    {
                        _settled.Remove(oldest.Value);
                        _settledOrder.RemoveFirst();
                    }

                    WriteAtomically(_settledPath, string.Join('\n', _settledOrder) + "\n");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            Diag.Error("profile upload: queue file unreadable, starting empty", ex);
        }
    }

    // Under the lock.
    private void FlushLocked()
    {
        if (!_dirty)
        {
            return;
        }

        var root = new JsonObject
        {
            ["version"] = FormatVersion,
            ["owner"] = _owner,
            ["backfillAfter"] = _backfillAfter,
            ["pending"] = new JsonArray([.. _pending.Select(p =>
            {
                var json = p.Event.ToJson();
                json.Remove("schemaVersion");
                json["notBefore"] = p.NotBefore.ToString("O", CultureInfo.InvariantCulture);
                return (JsonNode)json;
            })]),
        };

        if (WriteAtomically(_path, root.ToJsonString()))
        {
            _dirty = false;
        }
    }

    // Under the lock.
    private void AppendSettled(List<string> ids)
    {
        if (ids.Count == 0)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_settledPath))!);
            File.AppendAllText(_settledPath, string.Join('\n', ids) + "\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diag.Error("profile upload: saving settled ids failed", ex);
        }
    }

    private void DeleteSettledFile()
    {
        try
        {
            File.Delete(_settledPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diag.Error("profile upload: clearing settled ids failed", ex);
        }
    }

    private static bool WriteAtomically(string path, string text)
    {
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(temp, text);
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diag.Error("profile upload: saving the queue failed", ex);
            try
            {
                File.Delete(temp);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
            }

            return false;
        }
    }

    private sealed record Item(ProfileEvent Event, DateTime NotBefore, int Bytes, long Version);
}
