using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hearthpulse.Core.Accounts;

/// <summary>
/// Events waiting for the site, kept in <c>profile-upload.json</c> in the data folder so games played offline, or left
/// when the app closed, go out later. It also remembers which event ids the site has already settled (accepted or
/// rejected), so a backfill does not send them again. Both belong to one site account: another account starts empty.
/// Thread-safe; every change is written atomically (temp file, then move).
/// </summary>
public sealed class ProfileUploadQueue
{
    /// <summary>Events kept waiting at most; past it the oldest are dropped (and logged).</summary>
    public const int MaxPending = 5_000;

    /// <summary>Settled ids remembered at most (oldest forgotten first; the site would only acknowledge them again).</summary>
    public const int MaxSettled = 50_000;

    private const int FormatVersion = 1;

    private readonly string _path;
    private readonly object _gate = new();
    private readonly List<Item> _pending = [];
    private readonly LinkedList<string> _settledOrder = new();
    private readonly HashSet<string> _settled = new(StringComparer.OrdinalIgnoreCase);
    private string? _owner;

    public ProfileUploadQueue(string path)
    {
        _path = path;
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
    /// Makes the queue <paramref name="userId"/>'s. A different account than before starts empty: idempotency on the
    /// site is per account, and one player's games must not go to another's profile.
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
            _settled.Clear();
            _settledOrder.Clear();
            Save();
        }
    }

    /// <summary>Whether the site has settled the event or it is already waiting.</summary>
    public bool Knows(string eventId)
    {
        lock (_gate)
        {
            return _settled.Contains(eventId) || _pending.Exists(p => p.Event.EventId.Equals(eventId, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Queues events, not before <paramref name="notBefore"/>. An event already waiting is replaced (a game whose rating
    /// arrived later); one the site has settled is skipped. Returns how many were queued or replaced.
    /// </summary>
    public int Enqueue(IEnumerable<ProfileEvent> events, DateTime notBefore)
    {
        lock (_gate)
        {
            var count = 0;
            foreach (var e in events)
            {
                if (_settled.Contains(e.EventId))
                {
                    continue;
                }

                var index = _pending.FindIndex(p => p.Event.EventId.Equals(e.EventId, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    _pending[index] = new Item(e, notBefore);
                }
                else
                {
                    _pending.Add(new Item(e, notBefore));
                }

                count++;
            }

            if (_pending.Count > MaxPending)
            {
                var drop = _pending.Count - MaxPending;
                _pending.RemoveRange(0, drop);
                Diag.Info($"profile upload: queue full, {drop.ToString(CultureInfo.InvariantCulture)} oldest games dropped");
            }

            if (count > 0)
            {
                Save();
            }

            return count;
        }
    }

    /// <summary>Drops every waiting event (the player turned sending off); settled ids are kept.</summary>
    public void ClearPending()
    {
        lock (_gate)
        {
            if (_pending.Count > 0)
            {
                Diag.Info($"profile upload: sending turned off, {_pending.Count.ToString(CultureInfo.InvariantCulture)} queued games dropped");
                _pending.Clear();
                Save();
            }
        }
    }

    /// <summary>Lets every waiting event go now (the rating it waited for came, or will not).</summary>
    public void Release(string eventId, DateTime now)
    {
        lock (_gate)
        {
            var index = _pending.FindIndex(p => p.Event.EventId.Equals(eventId, StringComparison.OrdinalIgnoreCase));
            if (index >= 0 && _pending[index].NotBefore > now)
            {
                _pending[index] = _pending[index] with { NotBefore = now };
                Save();
            }
        }
    }

    /// <summary>
    /// The next batch to send: due events in queue order, at most <paramref name="maxEvents"/> and
    /// <paramref name="maxBytes"/> of JSON (always at least one event when any is due).
    /// </summary>
    public IReadOnlyList<ProfileEvent> NextBatch(DateTime now, int maxEvents, int maxBytes)
    {
        lock (_gate)
        {
            var batch = new List<ProfileEvent>();
            var bytes = 16;
            foreach (var item in _pending)
            {
                if (item.NotBefore > now)
                {
                    continue;
                }

                var size = Encoding.UTF8.GetByteCount(item.Event.ToJson().ToJsonString()) + 1;
                if (batch.Count > 0 && (batch.Count >= maxEvents || bytes + size > maxBytes))
                {
                    break;
                }

                batch.Add(item.Event);
                bytes += size;
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

    /// <summary>Removes settled events (accepted, rejected or dropped) and remembers their ids.</summary>
    public void Settle(IEnumerable<string> eventIds)
    {
        lock (_gate)
        {
            var any = false;
            foreach (var id in eventIds)
            {
                any |= _pending.RemoveAll(p => p.Event.EventId.Equals(id, StringComparison.OrdinalIgnoreCase)) > 0;
                if (_settled.Add(id))
                {
                    _settledOrder.AddLast(id);
                    any = true;
                }
            }

            while (_settled.Count > MaxSettled && _settledOrder.First is { } oldest)
            {
                _settled.Remove(oldest.Value);
                _settledOrder.RemoveFirst();
            }

            if (any)
            {
                Save();
            }
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            if (JsonNode.Parse(File.ReadAllText(_path)) is not JsonObject root || (int?)root["version"] != FormatVersion)
            {
                Diag.Info("profile upload: queue file in an unknown format, starting empty");
                return;
            }

            _owner = (string?)root["owner"];
            foreach (var node in root["pending"] as JsonArray ?? [])
            {
                if (node is JsonObject item && (string?)item["eventId"] is { Length: > 0 } id && (string?)item["type"] is { Length: > 0 } type
                    && (string?)item["occurredAt"] is { Length: > 0 } at && item["payload"] is JsonObject payload)
                {
                    var notBefore = DateTime.TryParse((string?)item["notBefore"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var nb)
                        ? nb
                        : DateTime.MinValue;
                    _pending.Add(new Item(new ProfileEvent(id, type, at, (JsonObject)payload.DeepClone()), notBefore));
                }
            }

            foreach (var node in root["settled"] as JsonArray ?? [])
            {
                if ((string?)node is { Length: > 0 } id && _settled.Add(id))
                {
                    _settledOrder.AddLast(id);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            Diag.Error("profile upload: queue file unreadable, starting empty", ex);
        }
    }

    // Called under the lock.
    private void Save()
    {
        var root = new JsonObject
        {
            ["version"] = FormatVersion,
            ["owner"] = _owner,
            ["pending"] = new JsonArray([.. _pending.Select(p =>
            {
                var json = p.Event.ToJson();
                json.Remove("schemaVersion");
                json["notBefore"] = p.NotBefore.ToString("O", CultureInfo.InvariantCulture);
                return (JsonNode)json;
            })]),
            ["settled"] = new JsonArray([.. _settledOrder.Select(id => (JsonNode)JsonValue.Create(id))]),
        };

        var temp = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            File.WriteAllText(temp, root.ToJsonString());
            File.Move(temp, _path, overwrite: true);
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
        }
    }

    private sealed record Item(ProfileEvent Event, DateTime NotBefore);
}
