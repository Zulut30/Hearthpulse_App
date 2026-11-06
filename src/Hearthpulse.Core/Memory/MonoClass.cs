using System.Text;

namespace Hearthpulse.Core.Memory;

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
