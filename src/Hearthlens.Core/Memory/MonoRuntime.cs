using System.Text;

namespace Hearthlens.Core.Memory;

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
    private readonly object _gate = new();

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

public sealed class MonoField
{
    internal MonoField(string name, int offset, ushort attributes, byte typeCode, MonoClass declaringClass)
    {
        Name = name;
        Offset = offset;
        Attributes = attributes;
        TypeCode = typeCode;
        DeclaringClass = declaringClass;
    }

    public string Name { get; }

    public int Offset { get; }

    public ushort Attributes { get; }

    /// <summary>MONO_TYPE_* element type code.</summary>
    public byte TypeCode { get; }

    public MonoClass DeclaringClass { get; }

    public bool IsStatic => (Attributes & MonoOffsets.FieldAttributeStatic) != 0;

    public bool IsLiteral => (Attributes & MonoOffsets.FieldAttributeLiteral) != 0;

    public override string ToString() => $"{Name} @0x{Offset:X} type=0x{TypeCode:X2}{(IsStatic ? " static" : "")}";
}

public sealed class MonoClass
{
    /// <summary>Real class chains are a few levels deep; garbage parents can loop forever.</summary>
    private const int MaxHierarchyDepth = 32;

    private readonly MonoRuntime _runtime;
    private IReadOnlyList<MonoField>? _fields;
    private readonly Dictionary<string, MonoField?> _lookup = new(StringComparer.Ordinal);

    internal MonoClass(MonoRuntime runtime, ulong address)
    {
        _runtime = runtime;
        Address = address;
        var memory = runtime.Memory;
        Name = memory.ReadCString(memory.ReadPtr(address + MonoOffsets.ClassName), 256) ?? "?";
        Namespace = memory.ReadCString(memory.ReadPtr(address + MonoOffsets.ClassNamespace), 256) ?? string.Empty;
        Kind = (MonoClassKind)memory.ReadByte(address + MonoOffsets.ClassKind);
    }

    public ulong Address { get; }

    public string Name { get; }

    public string Namespace { get; }

    public string FullName => Namespace.Length > 0 ? Namespace + "." + Name : Name;

    internal MonoClassKind Kind { get; }

    /// <summary>Real classes have a known kind and a printable name; garbage memory almost never does.</summary>
    internal bool LooksValid =>
        Kind is >= MonoClassKind.Def and <= MonoClassKind.Pointer
        && Name.Length is > 0 and < 256 && Name != "?" && Name.All(c => c is > ' ' and <= '~');

    public MonoClass? Parent => _runtime.ClassAt(_runtime.Memory.ReadPtr(Address + MonoOffsets.ClassParent));

    /// <summary>For array classes: size in bytes of one element.</summary>
    public int ElementSize => _runtime.Memory.ReadInt32(Address + MonoOffsets.ClassSizes);

    public IReadOnlyList<MonoField> Fields
    {
        get
        {
            if (_fields is { } fields)
            {
                return fields;
            }

            // A failed or cut-short read is tried again next time instead of hiding the fields for good.
            var read = ReadFields();
            if (read.Count > 0 && !ProcessMemory.BudgetExhausted)
            {
                _fields = read;
            }

            return read;
        }
    }

    public MonoField? FindField(string name)
    {
        lock (_lookup)
        {
            if (_lookup.TryGetValue(name, out var cached))
            {
                return cached;
            }

            MonoField? found = null;
            var depth = 0;
            for (var cls = this; cls is not null && found is null && depth++ < MaxHierarchyDepth; cls = cls.Parent)
            {
                found = cls.Fields.FirstOrDefault(f => f.Name == name);
            }

            if (found is not null || !ProcessMemory.BudgetExhausted)
            {
                _lookup[name] = found;
            }

            return found;
        }
    }

    public IEnumerable<MonoField> AllFields()
    {
        var depth = 0;
        for (var cls = this; cls is not null && depth++ < MaxHierarchyDepth; cls = cls.Parent)
        {
            foreach (var field in cls.Fields)
            {
                yield return field;
            }
        }
    }

    /// <summary>Address of this class's static field block in the root domain, or 0.</summary>
    public ulong StaticData
    {
        get
        {
            var memory = _runtime.Memory;
            var runtimeInfo = memory.ReadPtr(Address + MonoOffsets.ClassRuntimeInfo);
            if (runtimeInfo == 0)
            {
                return 0;
            }

            var vtable = memory.ReadPtr(runtimeInfo + MonoOffsets.RuntimeInfoDomainVTables);
            if (vtable == 0)
            {
                return 0;
            }

            var vtableSize = memory.ReadInt32(Address + MonoOffsets.ClassVTableSize);
            return memory.ReadPtr(vtable + MonoOffsets.VTableMethods + (ulong)(vtableSize * 8));
        }
    }

    public MonoObject GetStatic(string fieldName)
    {
        var address = StaticFieldAddress(fieldName);
        return new MonoObject(_runtime, address == 0 ? 0 : _runtime.Memory.ReadPtr(address));
    }

    public int GetStaticInt(string fieldName)
    {
        var address = StaticFieldAddress(fieldName);
        return address == 0 ? 0 : _runtime.Memory.ReadInt32(address);
    }

    private ulong StaticFieldAddress(string fieldName)
    {
        var field = FindField(fieldName);
        if (field is null || !field.IsStatic || field.IsLiteral)
        {
            return 0;
        }

        var data = StaticData;
        return data == 0 ? 0 : data + (ulong)field.Offset;
    }

    private IReadOnlyList<MonoField> ReadFields()
    {
        var memory = _runtime.Memory;
        var source = this;
        if (Kind == MonoClassKind.GInst)
        {
            // Generic instances share the generic type definition's field metadata.
            var genericClass = memory.ReadPtr(Address + MonoOffsets.ClassGenericClass);
            var definition = _runtime.ClassAt(memory.ReadPtr(genericClass));
            // A generic instance is never its own definition: anything else is garbage and would recurse forever.
            return definition is null || definition == this || definition.Kind == MonoClassKind.GInst
                ? Array.Empty<MonoField>()
                : definition.Fields;
        }

        if (Kind is not (MonoClassKind.Def or MonoClassKind.Gtd))
        {
            return Array.Empty<MonoField>();
        }

        var first = memory.ReadPtr(Address + MonoOffsets.ClassFields);
        var count = memory.ReadInt32(Address + MonoOffsets.ClassDefFieldCount);
        if (first == 0 || count is <= 0 or > 4096)
        {
            return Array.Empty<MonoField>();
        }

        var raw = memory.ReadBytes(first, count * (int)MonoOffsets.FieldSize);
        if (raw is null)
        {
            return Array.Empty<MonoField>();
        }

        var fields = new List<MonoField>(count);
        for (var i = 0; i < count; i++)
        {
            var baseOffset = i * (int)MonoOffsets.FieldSize;
            var type = BitConverter.ToUInt64(raw, baseOffset + (int)MonoOffsets.FieldType);
            var namePtr = BitConverter.ToUInt64(raw, baseOffset + (int)MonoOffsets.FieldName);
            var offset = BitConverter.ToInt32(raw, baseOffset + (int)MonoOffsets.FieldOffset);
            var name = memory.ReadCString(namePtr, 256);
            if (name is null)
            {
                continue;
            }

            var attrs = memory.ReadUInt32(type + MonoOffsets.TypeAttrs);
            fields.Add(new MonoField(name, offset, (ushort)(attrs & 0xFFFF), (byte)((attrs >> 16) & 0xFF), source));
        }

        return fields;
    }

    public override string ToString() => FullName;
}

/// <summary>A managed object reference in the target process. Default value is null.</summary>
public readonly struct MonoObject
{
    private readonly MonoRuntime? _runtime;

    internal MonoObject(MonoRuntime runtime, ulong address)
    {
        _runtime = runtime;
        Address = address;
    }

    public ulong Address { get; }

    public bool IsNull => Address == 0 || _runtime is null;

    public MonoClass? Class
    {
        get
        {
            if (IsNull)
            {
                return null;
            }

            var vtable = _runtime!.Memory.ReadPtr(Address);
            return vtable == 0 ? null : _runtime.ClassAt(_runtime.Memory.ReadPtr(vtable));
        }
    }

    public string? ClassName => Class?.Name;

    public MonoObject this[string field] => Object(field);

    public MonoObject Object(string field)
    {
        var address = FieldAddress(field);
        return address == 0 ? default : new MonoObject(_runtime!, _runtime!.Memory.ReadPtr(address));
    }

    public int Int(string field, int fallback = 0)
    {
        var address = FieldAddress(field);
        return address == 0 ? fallback : _runtime!.Memory.ReadInt32(address);
    }

    public long Long(string field)
    {
        var address = FieldAddress(field);
        return address == 0 ? 0 : _runtime!.Memory.ReadInt64(address);
    }

    public bool Bool(string field)
    {
        var address = FieldAddress(field);
        return address != 0 && _runtime!.Memory.ReadBool(address);
    }

    public string? String(string field)
    {
        var address = FieldAddress(field);
        return address == 0 ? null : _runtime!.Memory.ReadMonoString(_runtime.Memory.ReadPtr(address));
    }

    public MonoArray Array(string field)
    {
        var obj = Object(field);
        return obj.IsNull ? default : new MonoArray(_runtime!, obj.Address);
    }

    /// <summary>Address of an instance field's storage (object header included in Mono offsets).</summary>
    public ulong FieldAddress(string field)
    {
        if (IsNull)
        {
            return 0;
        }

        var definition = Class?.FindField(field);
        return definition is null || definition.IsStatic ? 0 : Address + (ulong)definition.Offset;
    }

    /// <summary>Reads elements of a System.Collections.Generic.List&lt;T&gt; of references.</summary>
    public IEnumerable<MonoObject> ListItems()
    {
        var items = Array("_items");
        var size = Math.Min(Int("_size"), items.Length);
        for (var i = 0; i < size && !ProcessMemory.BudgetExhausted; i++)
        {
            yield return items.ObjectAt(i);
        }
    }

    /// <summary>Reads elements of a List&lt;int&gt; / List&lt;enum&gt;.</summary>
    public IReadOnlyList<int> IntListItems()
    {
        var items = Array("_items");
        var size = Math.Min(Math.Min(Int("_size"), items.Length), 4096);
        var result = new int[Math.Max(size, 0)];
        for (var i = 0; i < size; i++)
        {
            result[i] = items.IntAt(i);
        }

        return result;
    }

    /// <summary>Non-null values of Blizzard's Map&lt;K,V&gt; (keySlots/valueSlots arrays) holding references.</summary>
    public IEnumerable<MonoObject> MapValues()
    {
        var values = Array("valueSlots");
        for (var i = 0; i < values.Length && !ProcessMemory.BudgetExhausted; i++)
        {
            var value = values.ObjectAt(i);
            if (!value.IsNull)
            {
                yield return value;
            }
        }
    }

    /// <summary>Values of a CoreFX Dictionary&lt;TRef, TRef&gt; (Entry = hash, next, key, value).</summary>
    public IEnumerable<MonoObject> DictionaryValues()
    {
        var entries = Array("_entries");
        var count = Math.Min(Int("_count"), entries.Length);
        var size = entries.ElementSize;
        if (size <= 0)
        {
            size = 24;
        }

        for (var i = 0; i < count && !ProcessMemory.BudgetExhausted; i++)
        {
            var value = entries.ObjectAtRaw((ulong)(i * size) + 16);
            if (!value.IsNull)
            {
                yield return value;
            }
        }
    }

    public override string ToString() => IsNull ? "null" : $"{ClassName}@0x{Address:X}";
}

public readonly struct MonoArray
{
    private readonly MonoRuntime? _runtime;

    internal MonoArray(MonoRuntime runtime, ulong address)
    {
        _runtime = runtime;
        Address = address;
        var length = runtime.Memory.ReadInt64(address + MonoOffsets.ArrayLength);
        Length = length is < 0 or > 1_000_000 ? 0 : (int)length;
    }

    public ulong Address { get; }

    public int Length { get; }

    public int ElementSize
    {
        get
        {
            if (_runtime is null || Address == 0)
            {
                return 0;
            }

            var vtable = _runtime.Memory.ReadPtr(Address);
            var cls = _runtime.ClassAt(_runtime.Memory.ReadPtr(vtable));
            return cls?.ElementSize ?? 0;
        }
    }

    public MonoObject ObjectAt(int index) => ObjectAtRaw((ulong)index * 8);

    internal MonoObject ObjectAtRaw(ulong byteOffset) =>
        _runtime is null || Address == 0
            ? default
            : new MonoObject(_runtime, _runtime.Memory.ReadPtr(Address + MonoOffsets.ArrayData + byteOffset));

    public int IntAt(int index) =>
        _runtime is null || Address == 0 ? 0 : _runtime.Memory.ReadInt32(Address + MonoOffsets.ArrayData + (ulong)index * 4);
}
