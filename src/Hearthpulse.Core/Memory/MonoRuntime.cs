using System.Text;

namespace Hearthpulse.Core.Memory;

/// <summary>
/// Minimal read-only walker over a Mono runtime hosted in another process: resolves the root
/// domain, the Assembly-CSharp image, classes by name, static fields and object fields.
/// </summary>
public sealed class MonoRuntime
{
    private readonly ulong _image;
    private readonly Dictionary<string, ulong> _byFullName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ulong> _bySimpleName = new(StringComparer.Ordinal);
    private readonly Dictionary<ulong, MonoClass> _classes = new();
    private readonly Lock _gate = new();

    /// <summary>Stale objects can point anywhere; the class cache is dropped before it grows without end.</summary>
    private const int MaxCachedClasses = 50_000;

    private MonoRuntime(ProcessMemory memory, ulong image)
    {
        Memory = memory;
        _image = image;
    }

    public ProcessMemory Memory { get; }

    public int ClassCount => _byFullName.Count;

    public static MonoRuntime? Attach(ProcessMemory memory, out string error)
    {
        var monoBase = memory.FindModuleBase(MonoOffsets.MonoModule);
        if (monoBase == 0)
        {
            error = "Mono runtime not loaded yet";
            return null;
        }

        var rootDomainFn = FindExport(memory, monoBase, "mono_get_root_domain");
        if (rootDomainFn == 0)
        {
            error = "mono_get_root_domain export not found";
            return null;
        }

        // x64 body: 48 8B 05 <disp32>  ->  mov rax, [rip + disp32]
        var code = memory.ReadBytes(rootDomainFn, 7);
        if (code is null || code[0] != 0x48 || code[1] != 0x8B || code[2] != 0x05)
        {
            error = "Unexpected mono_get_root_domain prologue";
            return null;
        }

        var displacement = BitConverter.ToInt32(code, 3);
        var domain = memory.ReadPtr((ulong)((long)rootDomainFn + 7 + displacement));
        if (domain == 0)
        {
            error = "Root domain not initialised";
            return null;
        }

        var image = FindAssemblyImage(memory, domain, "Assembly-CSharp");
        if (image == 0)
        {
            error = "Assembly-CSharp not loaded yet";
            return null;
        }

        var runtime = new MonoRuntime(memory, image);
        runtime.BuildClassIndex();
        if (runtime.ClassCount < 500)
        {
            error = $"Class cache looks wrong ({runtime.ClassCount} classes)";
            return null;
        }

        error = string.Empty;
        return runtime;
    }

    public MonoClass? FindClass(string name)
    {
        if (_byFullName.TryGetValue(name, out var address) || _bySimpleName.TryGetValue(name, out address))
        {
            return ClassAt(address);
        }

        return null;
    }

    public IEnumerable<string> ClassNames => _byFullName.Keys;

    internal MonoClass? ClassAt(ulong address)
    {
        if (address == 0)
        {
            return null;
        }

        lock (_gate)
        {
            if (_classes.TryGetValue(address, out var cached))
            {
                return cached;
            }
        }

        // A stale object points at garbage instead of a class: such an address is neither used nor remembered.
        var cls = new MonoClass(this, address);
        if (!cls.LooksValid)
        {
            return null;
        }

        lock (_gate)
        {
            if (_classes.Count >= MaxCachedClasses)
            {
                _classes.Clear();
            }

            return _classes.TryAdd(address, cls) ? cls : _classes[address];
        }
    }

    public MonoObject Object(ulong address) => new(this, address);

    private void BuildClassIndex()
    {
        var table = Memory.ReadPtr(_image + MonoOffsets.ImageClassCache + MonoOffsets.HashTableTable);
        var size = Memory.ReadInt32(_image + MonoOffsets.ImageClassCache + MonoOffsets.HashTableSize);
        if (table == 0 || size is <= 0 or > 1_000_000)
        {
            return;
        }

        var buckets = Memory.ReadBytes(table, size * 8);
        if (buckets is null)
        {
            return;
        }

        var nested = new List<(ulong Address, ulong Outer, string Name)>();
        for (var i = 0; i < size; i++)
        {
            var cls = BitConverter.ToUInt64(buckets, i * 8);
            var guard = 0;
            while (cls != 0 && guard++ < 10_000)
            {
                var name = Memory.ReadCString(Memory.ReadPtr(cls + MonoOffsets.ClassName), 256);
                if (!string.IsNullOrEmpty(name))
                {
                    var outer = Memory.ReadPtr(cls + MonoOffsets.ClassNestedIn);
                    if (outer != 0)
                    {
                        nested.Add((cls, outer, name));
                    }
                    else
                    {
                        var ns = Memory.ReadCString(Memory.ReadPtr(cls + MonoOffsets.ClassNamespace), 256) ?? string.Empty;
                        var full = ns.Length > 0 ? ns + "." + name : name;
                        _byFullName.TryAdd(full, cls);
                        _bySimpleName.TryAdd(name, cls);
                    }
                }

                cls = Memory.ReadPtr(cls + MonoOffsets.ClassDefNextClassCache);
            }
        }

        var byAddress = _byFullName.ToDictionary(kv => kv.Value, kv => kv.Key);
        // Nested types can nest several levels deep; resolve until no progress is made.
        for (var pass = 0; pass < 4 && nested.Count > 0; pass++)
        {
            var remaining = new List<(ulong, ulong, string)>();
            foreach (var (address, outer, name) in nested)
            {
                if (byAddress.TryGetValue(outer, out var outerName))
                {
                    var full = outerName + "+" + name;
                    _byFullName.TryAdd(full, address);
                    byAddress[address] = full;
                }
                else
                {
                    remaining.Add((address, outer, name));
                }
            }

            nested = remaining;
        }
    }

    private static ulong FindAssemblyImage(ProcessMemory memory, ulong domain, string assemblyName)
    {
        var node = memory.ReadPtr(domain + MonoOffsets.DomainAssemblies);
        var guard = 0;
        while (node != 0 && guard++ < 4096)
        {
            var assembly = memory.ReadPtr(node);
            if (assembly != 0)
            {
                var name = memory.ReadCString(memory.ReadPtr(assembly + MonoOffsets.AssemblyName), 256);
                if (name == assemblyName)
                {
                    return memory.ReadPtr(assembly + MonoOffsets.AssemblyImage);
                }
            }

            node = memory.ReadPtr(node + 8);
        }

        return 0;
    }

    private static ulong FindExport(ProcessMemory memory, ulong moduleBase, string exportName)
    {
        var peHeader = moduleBase + (ulong)memory.ReadInt32(moduleBase + 0x3C);
        if (memory.ReadUInt32(peHeader) != 0x00004550)
        {
            return 0;
        }

        // PE32+ optional header starts at +0x18; data directory 0 (exports) at +0x70 within it.
        var exportRva = memory.ReadUInt32(peHeader + 0x18 + 0x70);
        if (exportRva == 0)
        {
            return 0;
        }

        var exportDir = moduleBase + exportRva;
        var nameCount = memory.ReadInt32(exportDir + 0x18);
        var functions = moduleBase + memory.ReadUInt32(exportDir + 0x1C);
        var names = moduleBase + memory.ReadUInt32(exportDir + 0x20);
        var ordinals = moduleBase + memory.ReadUInt32(exportDir + 0x24);

        var target = Encoding.ASCII.GetBytes(exportName + "\0");
        for (var i = 0; i < nameCount; i++)
        {
            var nameAddress = moduleBase + memory.ReadUInt32(names + (ulong)(i * 4));
            var candidate = memory.ReadBytes(nameAddress, target.Length);
            if (candidate is null || !candidate.AsSpan().SequenceEqual(target))
            {
                continue;
            }

            var ordinal = memory.ReadUInt16(ordinals + (ulong)(i * 2));
            return moduleBase + memory.ReadUInt32(functions + (ulong)(ordinal * 4));
        }

        return 0;
    }
}
