using System.Buffers.Binary;
using System.Text;
using Hearthpulse.Core.Memory;

namespace Hearthpulse.Tests;

/// <summary>
/// Builds a <see cref="MemoryImage"/> of a tiny Mono runtime by hand: the mono module's export, the root domain, the
/// Assembly-CSharp image and its class table, classes with their fields and static data, objects, arrays, lists,
/// dictionaries, maps and strings. The layout numbers below are Unity's 64-bit mono-2.0-bdwgc layout written out on
/// their own, not read from <see cref="MonoOffsets"/>, so a wrong offset there makes the memory tests fail.
/// </summary>
internal sealed class MonoImageBuilder
{
    // MonoDomain, MonoAssembly, MonoImage.
    private const int DomainAssemblies = 160, AssemblyName = 0x10, AssemblyImage = 96, ImageClassCache = 1232, HashSize = 24, HashTable = 32;

    // MonoClass.
    private const int ClassKind = 27, ClassParent = 48, ClassNestedIn = 56, ClassName = 72, ClassNamespace = 80, ClassVTableSize = 92,
        ClassSizes = 144, ClassFields = 152, ClassRuntimeInfo = 208, ClassFieldCount = 256, ClassNextInCache = 264, ClassStructSize = 288;

    private const int RuntimeInfoVTables = 8, VTableMethods = 72, FieldStructSize = 32, FieldName = 8, FieldOffset = 24, TypeAttrs = 8;

    private const int StringLength = 16, StringChars = 20, ArrayLength = 24, ArrayData = 32;

    public const ulong MonoBase = 0x7FFA_0000_0000;
    private const ulong HeapBase = 0x2000_0000;

    private readonly byte[] _module = new byte[0x2000];
    private readonly byte[] _heap = new byte[0x40_0000];
    private readonly List<ulong> _classes = [];
    private readonly Dictionary<string, ulong> _strings = new(StringComparer.Ordinal);
    private int _used;

    public MonoImageBuilder(int fillerClasses = 520)
    {
        WriteMonoModule();
        for (var i = 0; i < fillerClasses; i++)
        {
            Class("Filler" + i);
        }
    }

    /// <summary>The image: the mono module, the heap, and the class table of Assembly-CSharp linked in.</summary>
    public MemoryImage Build()
    {
        // Class table: 64 buckets, each a chain through the classes' next-in-cache pointer.
        const int buckets = 64;
        var table = Alloc(buckets * 8);
        var heads = new ulong[buckets];
        for (var i = 0; i < _classes.Count; i++)
        {
            var bucket = i % buckets;
            Ptr(_classes[i] + ClassNextInCache, heads[bucket]);
            heads[bucket] = _classes[i];
        }

        for (var i = 0; i < buckets; i++)
        {
            Ptr(table + (ulong)(i * 8), heads[i]);
        }

        var image = Alloc(ImageClassCache + 64);
        Int(image + ImageClassCache + HashSize, buckets);
        Ptr(image + ImageClassCache + HashTable, table);

        // Root domain -> assembly list (another assembly first) -> Assembly-CSharp -> image.
        var other = Alloc(128);
        Ptr(other + AssemblyName, CString("mscorlib"));
        var csharp = Alloc(128);
        Ptr(csharp + AssemblyName, CString("Assembly-CSharp"));
        Ptr(csharp + AssemblyImage, image);
        var second = Alloc(16);
        Ptr(second, csharp);
        var first = Alloc(16);
        Ptr(first, other);
        Ptr(first + 8, second);
        var domain = Alloc(256);
        Ptr(domain + DomainAssemblies, first);
        BinaryPrimitives.WriteUInt64LittleEndian(_module.AsSpan(0x1800), domain);

        var result = new MemoryImage();
        result.AddModule("mono-2.0-bdwgc.dll", MonoBase);
        result.Add(MonoBase, _module);
        result.Add(HeapBase, _heap.AsSpan(0, _used));
        return result;
    }

    /// <summary>A class and its fields: (name, offset, static, Mono type code).</summary>
    public ulong Class(string fullName, params (string Name, int Offset, bool Static, byte Type)[] fields)
    {
        var dot = fullName.LastIndexOf('.');
        var cls = Alloc(ClassStructSize);
        _heap[(int)(cls - HeapBase) + ClassKind] = 1; // a class definition
        Ptr(cls + ClassName, CString(dot < 0 ? fullName : fullName[(dot + 1)..]));
        Ptr(cls + ClassNamespace, CString(dot < 0 ? string.Empty : fullName[..dot]));
        if (fields.Length > 0)
        {
            var block = Alloc(fields.Length * FieldStructSize);
            for (var i = 0; i < fields.Length; i++)
            {
                var (name, offset, isStatic, type) = fields[i];
                var typeStruct = Alloc(16);
                Int(typeStruct + TypeAttrs, (type << 16) | (isStatic ? 0x10 : 0x06));
                var entry = block + (ulong)(i * FieldStructSize);
                Ptr(entry, typeStruct);
                Ptr(entry + FieldName, CString(name));
                Int(entry + FieldOffset, offset);
            }

            Ptr(cls + ClassFields, block);
            Int(cls + ClassFieldCount, fields.Length);
        }

        _classes.Add(cls);
        return cls;
    }

    /// <summary>An array class (System.Object[] and the like) whose elements are <paramref name="elementSize"/> bytes.</summary>
    public ulong ArrayClass(string name, int elementSize)
    {
        var cls = Alloc(ClassStructSize);
        _heap[(int)(cls - HeapBase) + ClassKind] = 5;
        Ptr(cls + ClassName, CString(name));
        Ptr(cls + ClassNamespace, CString("System"));
        Int(cls + ClassSizes, elementSize);
        return cls;
    }

    /// <summary>The block a class's static fields live in (through its runtime info and domain vtable).</summary>
    public ulong Statics(ulong cls, int size = 64)
    {
        const int vtableSize = 3;
        var data = Alloc(size);
        var vtable = Alloc(VTableMethods + ((vtableSize + 1) * 8));
        Ptr(vtable + VTableMethods + (vtableSize * 8), data);
        var runtimeInfo = Alloc(16);
        Ptr(runtimeInfo + RuntimeInfoVTables, vtable);
        Ptr(cls + ClassRuntimeInfo, runtimeInfo);
        Int(cls + ClassVTableSize, vtableSize);
        return data;
    }

    /// <summary>An object of <paramref name="cls"/> with <paramref name="size"/> bytes (header included).</summary>
    public ulong Object(ulong cls, int size = 64)
    {
        var vtable = Alloc(16);
        Ptr(vtable, cls);
        var obj = Alloc(size);
        Ptr(obj, vtable);
        return obj;
    }

    /// <summary>An array of <paramref name="arrayClass"/> holding <paramref name="length"/> elements of <paramref name="elementSize"/> bytes.</summary>
    public ulong Array(ulong arrayClass, int length, int elementSize = 8)
    {
        var array = Object(arrayClass, ArrayData + (length * elementSize));
        Long(array + ArrayLength, length);
        return array;
    }

    public ulong Element(ulong array, int index, int elementSize = 8) => array + ArrayData + (ulong)(index * elementSize);

    /// <summary>A managed System.String.</summary>
    public ulong MonoString(string text)
    {
        var obj = Alloc(StringChars + (text.Length * 2) + 2);
        Int(obj + StringLength, text.Length);
        Encoding.Unicode.GetBytes(text).CopyTo(_heap, (int)(obj - HeapBase) + StringChars);
        return obj;
    }

    public void Ptr(ulong address, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(Heap(address, 8), value);

    public void Long(ulong address, long value) => BinaryPrimitives.WriteInt64LittleEndian(Heap(address, 8), value);

    public void Int(ulong address, int value) => BinaryPrimitives.WriteInt32LittleEndian(Heap(address, 4), value);

    public void Bool(ulong address, bool value) => Heap(address, 1)[0] = value ? (byte)1 : (byte)0;

    private ulong CString(string text)
    {
        if (!_strings.TryGetValue(text, out var address))
        {
            var bytes = Encoding.UTF8.GetBytes(text + "\0");
            address = Alloc(bytes.Length);
            bytes.CopyTo(_heap, (int)(address - HeapBase));
            _strings[text] = address;
        }

        return address;
    }

    private ulong Alloc(int size)
    {
        var address = HeapBase + (ulong)_used;
        _used += (size + 15) & ~15;
        return address;
    }

    private Span<byte> Heap(ulong address, int length) => _heap.AsSpan((int)(address - HeapBase), length);

    /// <summary>A PE header with one export, mono_get_root_domain: mov rax, [rip + disp32] to the root domain pointer.</summary>
    private void WriteMonoModule()
    {
        var m = _module.AsSpan();
        const int pe = 0x80, exports = 0x1000, functions = 0x1100, names = 0x1110, ordinals = 0x1120, name = 0x1130, code = 0x1700;
        BinaryPrimitives.WriteInt32LittleEndian(m[0x3C..], pe);
        BinaryPrimitives.WriteUInt32LittleEndian(m[pe..], 0x00004550);
        BinaryPrimitives.WriteUInt32LittleEndian(m[(pe + 0x18 + 0x70)..], exports);
        BinaryPrimitives.WriteInt32LittleEndian(m[(exports + 0x18)..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(m[(exports + 0x1C)..], functions);
        BinaryPrimitives.WriteUInt32LittleEndian(m[(exports + 0x20)..], names);
        BinaryPrimitives.WriteUInt32LittleEndian(m[(exports + 0x24)..], ordinals);
        BinaryPrimitives.WriteUInt32LittleEndian(m[functions..], code);
        BinaryPrimitives.WriteUInt32LittleEndian(m[names..], name);
        BinaryPrimitives.WriteUInt16LittleEndian(m[ordinals..], 0);
        Encoding.ASCII.GetBytes("mono_get_root_domain\0").CopyTo(m[name..]);
        // 48 8B 05 <disp32>: the root domain pointer sits at code + 7 + disp32 = 0x1800.
        m[code] = 0x48;
        m[code + 1] = 0x8B;
        m[code + 2] = 0x05;
        BinaryPrimitives.WriteInt32LittleEndian(m[(code + 3)..], 0x1800 - (code + 7));
    }
}
