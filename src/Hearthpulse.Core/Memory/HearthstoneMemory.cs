using System.Diagnostics;
using System.Globalization;

namespace Hearthpulse.Core.Memory;

/// <summary>
/// Reads Hearthstone client state straight from process memory: Battlegrounds rating, ranked medals,
/// the Battlegrounds tribe pool. All calls are read-only and return null when data is unavailable.
/// </summary>
public sealed partial class HearthstoneMemory : IDisposable
{
    private readonly Lock _gate = new();
    private ProcessMemory? _memory;
    private MonoRuntime? _runtime;
    private DateTime _nextAttachAttempt = DateTime.MinValue;
    private bool _attaching;
    private int _attachFailures;

    // MONO_TYPE_I4 / MONO_TYPE_U4: the element types of the int fields ratings are stored in.
    private const byte MonoInt32 = 0x08;
    private const byte MonoUInt32 = 0x09;

    // One scan reads a few thousand values; attaching walks the whole class table once.
    private const int ScanReadBudget = 50_000;
    private static readonly TimeSpan ScanTimeBudget = TimeSpan.FromSeconds(1.5);
    private const int AttachReadBudget = 3_000_000;
    private static readonly TimeSpan AttachTimeBudget = TimeSpan.FromSeconds(30);
    private DateTime _nextBudgetNote = DateTime.MinValue;
    private readonly Dictionary<string, ulong> _netCacheObjects = new(StringComparer.Ordinal);
    private DateTime _netCacheRefreshAt = DateTime.MinValue;

    // What this client build calls each field the reads below need, keyed by class address and role, plus the class
    // addresses themselves. A game update that renames or moves one costs a single lookup, not one per scan.
    private readonly Dictionary<(ulong Class, string Role), string?> _fieldNames = new();
    private readonly Dictionary<string, ulong> _classes = new(StringComparer.Ordinal);

    public bool IsAttached
    {
        get
        {
            lock (_gate)
            {
                return _runtime is not null && _memory is { HasExited: false };
            }
        }
    }

    /// <summary>
    /// Reading the client's memory (rating, ranks, lobby tribes) uses Windows APIs. On other platforms everything
    /// else keeps working from the logs; these values are simply unavailable.
    /// </summary>
    public static bool IsSupported => OperatingSystem.IsWindows();

    public string Status { get; private set; } = IsSupported ? "Waiting for Hearthstone" : "Memory reading is only available on Windows";

    /// <summary>Attaches to a running Hearthstone process; cheap to call repeatedly.</summary>
    public bool EnsureAttached()
    {
        lock (_gate)
        {
            if (_runtime is not null && _memory is { HasExited: false })
            {
                return true;
            }

            DetachLocked();
            if (!IsSupported)
            {
                Status = "Memory reading is only available on Windows";
                return false;
            }

            if (_attaching || DateTime.UtcNow < _nextAttachAttempt)
            {
                return false;
            }

            _attaching = true;
        }

        // Attaching reads the game's whole class table. It runs outside the lock, so IsAttached (asked on the log
        // thread for every snapshot) never waits for it.
        (ProcessMemory? Memory, MonoRuntime? Runtime, string Status, bool Found) result;
        try
        {
            result = Attach();
        }
        catch (Exception ex)
        {
            result = (null, null, "Attach failed: " + ex.Message, true);
        }

        lock (_gate)
        {
            _attaching = false;
            Status = result.Status;
            if (result.Runtime is null)
            {
                result.Memory?.Dispose();
                // A game that is running but cannot be read (an update moved things, a security tool blocks the
                // reads) is tried less and less often instead of every few seconds.
                _attachFailures = result.Found ? _attachFailures + 1 : 0;
                _nextAttachAttempt = DateTime.UtcNow.AddSeconds(Math.Clamp(5 * _attachFailures, 5, 60));
                return false;
            }

            _attachFailures = 0;
            _memory = result.Memory;
            _runtime = result.Runtime;
            return true;
        }
    }

    /// <summary>
    /// Reads from <paramref name="memory"/> instead of the running game: a recorded image (<see cref="MemoryImage"/>) for
    /// the memory fixtures, or the recorder's wrapped game. False, with <see cref="Status"/> saying why, when its Mono
    /// runtime cannot be found there.
    /// </summary>
    public bool AttachTo(ProcessMemory memory)
    {
        ArgumentNullException.ThrowIfNull(memory);
        MonoRuntime? runtime;
        string error;
        using (ProcessMemory.Budget(AttachReadBudget, AttachTimeBudget))
        {
            runtime = MonoRuntime.Attach(memory, out error);
        }

        lock (_gate)
        {
            DetachLocked();
            Status = runtime is null ? error : $"Attached · {runtime.ClassCount:N0} classes";
            (_memory, _runtime) = runtime is null ? (null, null) : (memory, runtime);
            return runtime is not null;
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Disposed on every failed path (including exceptions); on success it is returned to the caller, which owns it.")]
    private static (ProcessMemory? Memory, MonoRuntime? Runtime, string Status, bool Found) Attach()
    {
        var processes = Process.GetProcessesByName("Hearthstone");
        try
        {
            if (processes.Length == 0)
            {
                return (null, null, "Hearthstone not running", false);
            }

            var memory = ProcessMemory.Open(processes[0].Id);
            if (memory is null)
            {
                return (null, null, "Cannot open Hearthstone process (run as the same user)", true);
            }

            try
            {
                using (ProcessMemory.Budget(AttachReadBudget, AttachTimeBudget))
                {
                    var runtime = MonoRuntime.Attach(memory, out var error);
                    if (runtime is null || ProcessMemory.BudgetExhausted)
                    {
                        memory.Dispose();
                        return (null, null, runtime is null ? error : "Attaching took too long, retrying", true);
                    }

                    return (memory, runtime, $"Attached · {runtime.ClassCount:N0} classes", true);
                }
            }
            catch
            {
                // The process handle has no finalizer: a failed attach (retried every few seconds) leaked one each time.
                memory.Dispose();
                throw;
            }
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    public MonoRuntime? Runtime
    {
        get
        {
            lock (_gate)
            {
                return _runtime;
            }
        }
    }

    private MonoObject GameStateInstance(MonoRuntime rt) => Instance(rt, "GameState", "GameState");

    /// <summary>
    /// The client's singleton for a class (GameState.s_instance, ZoneMgr.s_instance, …). Both the class and the static
    /// field that holds the instance are looked up by name, so a renamed field or a class that gained a namespace is
    /// found without a code change.
    /// </summary>
    private MonoObject Instance(MonoRuntime rt, string role, params string[] classNames)
    {
        var cls = Class(rt, role, classNames);
        var field = Field(cls, role + ".instance", ["s_instance"], isStatic: true);
        return cls is null || field is null ? default : cls.GetStatic(field);
    }

    /// <summary>
    /// A class by name, else the one class whose full name is that name inside a namespace (a namespace added in a game
    /// update). Two or more such classes are ambiguous and give null rather than a guess.
    /// </summary>
    private MonoClass? Class(MonoRuntime rt, string role, string[] names)
    {
        lock (_classes)
        {
            if (_classes.TryGetValue(role, out var address) && address != 0 && rt.ClassAt(address) is { } cached)
            {
                return cached;
            }
        }

        var found = names.Select(rt.FindClass).FirstOrDefault(c => c is not null);
        if (found is null)
        {
            var namespaced = rt.ClassNames
                .Where(n => names.Any(name => n.EndsWith("." + name, StringComparison.Ordinal)))
                .Take(2)
                .ToList();
            found = namespaced.Count == 1 ? rt.FindClass(namespaced[0]) : null;
        }

        if (found is not null)
        {
            lock (_classes)
            {
                _classes[role] = found.Address;
            }
        }

        return found;
    }

    /// <summary>
    /// The name this client build uses for a field we already read: the known name first, else a field that is the same
    /// name spelled the way a game update tends to respell it ("m_side", "_side", "&lt;Side&gt;k__BackingField", other
    /// case), see <see cref="CanonicalFieldName"/>. Never a different field: no substring or "looks like" matching, so
    /// a rename can only lead back to a value we already read. The answer is remembered per class and dropped when the
    /// game exits, so the lookup runs once per game update instead of once per scan.
    /// </summary>
    private string? Field(MonoClass? cls, string role, string[] known, bool integer = false, bool isStatic = false)
    {
        if (cls is null)
        {
            return null;
        }

        var key = (cls.Address, role);
        lock (_fieldNames)
        {
            if (_fieldNames.TryGetValue(key, out var remembered))
            {
                return remembered;
            }
        }

        var found = known.FirstOrDefault(name => cls.FindField(name) is not null);
        if (found is null)
        {
            var canonical = known.Select(CanonicalFieldName).ToHashSet(StringComparer.Ordinal);
            found = cls.AllFields()
                .FirstOrDefault(f => f.IsStatic == isStatic
                    && (!integer || f.TypeCode is MonoInt32 or MonoUInt32)
                    && canonical.Contains(CanonicalFieldName(f.Name)))?.Name;
        }

        // A field list cut short by the read budget must not hide the field for good.
        if (!ProcessMemory.BudgetExhausted)
        {
            lock (_fieldNames)
            {
                _fieldNames[key] = found;
            }
        }

        return found;
    }

    /// <summary>
    /// A field name without the parts a respelling changes: the auto-property wrapper "&lt;Name&gt;k__BackingField",
    /// an "m_"/"s_" prefix, underscores and case. "m_side", "_side" and "&lt;Side&gt;k__BackingField" all give "side".
    /// </summary>
    private static string CanonicalFieldName(string name)
    {
        const string backingField = ">k__BackingField";
        var core = name.StartsWith('<') && name.EndsWith(backingField, StringComparison.Ordinal)
            ? name[1..^backingField.Length]
            : name;
        if (core.StartsWith("m_", StringComparison.Ordinal) || core.StartsWith("s_", StringComparison.Ordinal))
        {
            core = core[2..];
        }

        return core.Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
    }

    /// <summary>Resolves a service registered in Hearthstone's service locator by type name.</summary>
    public static MonoObject FindService(MonoRuntime rt, string serviceName)
    {
        var jobs = rt.FindClass("Hearthstone.HearthstoneJobs");
        var builders = jobs?.GetStatic("s_dependencyBuilder") ?? default;
        foreach (var builder in builders.ListItems())
        {
            var services = builder["m_serviceLocator"]["m_services"];
            foreach (var entry in services.DictionaryValues())
            {
                if (entry.String("<ServiceTypeName>k__BackingField") == serviceName)
                {
                    return entry["<Service>k__BackingField"];
                }
            }
        }

        return default;
    }

    public static MonoObject FindNetCacheObject(MonoRuntime rt, string className)
    {
        var netCache = FindService(rt, "NetCache")["m_netCache"];
        foreach (var value in netCache.MapValues())
        {
            if (value.ClassName == className)
            {
                return value;
            }
        }

        return default;
    }

    /// <param name="quiet">A failed read does not change <see cref="Status"/> (frequent reads from another thread would make it flicker).</param>
    private T? Safe<T>(Func<MonoRuntime, T?> read, bool quiet = false)
    {
        if (!EnsureAttached())
        {
            return default;
        }

        MonoRuntime? runtime;
        lock (_gate)
        {
            runtime = _runtime;
        }

        if (runtime is null)
        {
            return default;
        }

        try
        {
            using (ProcessMemory.Budget(ScanReadBudget, ScanTimeBudget))
            {
                var result = read(runtime);
                if (!ProcessMemory.BudgetExhausted)
                {
                    return result;
                }
            }

            // The scan was cut short (usually objects freed mid-scan): its half-read values are not used.
            if (DateTime.UtcNow >= _nextBudgetNote)
            {
                _nextBudgetNote = DateTime.UtcNow.AddMinutes(1);
                Diag.Info("A memory scan ran out of its read budget and was skipped");
            }

            return default;
        }
        catch (Exception ex)
        {
            if (!quiet)
            {
                Status = "Read failed: " + ex.Message;
            }

            return default;
        }
    }

    private void DetachLocked()
    {
        _runtime = null;
        _memory?.Dispose();
        _memory = null;
        _netCacheRefreshAt = DateTime.MinValue;
        // A new game process may be a new client build: find the rating layout again instead of reusing the old one.
        _ratingLayout = KnownRatingLayout;
        _nextRatingDiscovery = DateTime.MinValue;
        lock (_fieldNames)
        {
            _fieldNames.Clear();
        }

        lock (_classes)
        {
            _classes.Clear();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            DetachLocked();
        }
    }
}
