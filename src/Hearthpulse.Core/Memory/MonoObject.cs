using System.Text;

namespace Hearthpulse.Core.Memory;

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

    /// <summary>Key and value of each non-null entry of Blizzard's Map&lt;int,V&gt; holding references (keySlots is an int[]).</summary>
    public IEnumerable<(int Key, MonoObject Value)> MapIntEntries()
    {
        var keys = Array("keySlots");
        var values = Array("valueSlots");
        var count = Math.Min(keys.Length, values.Length);
        for (var i = 0; i < count && !ProcessMemory.BudgetExhausted; i++)
        {
            var value = values.ObjectAt(i);
            if (!value.IsNull)
            {
                yield return (keys.IntAt(i), value);
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
