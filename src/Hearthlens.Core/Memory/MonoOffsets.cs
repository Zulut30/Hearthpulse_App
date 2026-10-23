namespace Hearthlens.Core.Memory;

/// <summary>
/// Struct offsets for Unity's 64-bit Mono (mono-2.0-bdwgc). The layout is identical for the
/// unity-2022.3-mbe and unity-6000.x-mbe branches; values match the set live-validated against
/// Hearthstone 6000.3.11 by the open-source unity-spy project (MIT).
/// </summary>
internal static class MonoOffsets
{
    public const string MonoModule = "mono-2.0-bdwgc.dll";

    // MonoDomain / MonoAssembly / MonoImage
    public const ulong DomainAssemblies = 160;
    public const ulong AssemblyName = 0x10;
    public const ulong AssemblyImage = 96;
    public const ulong ImageClassCache = 1232;
    public const ulong HashTableSize = 24;
    public const ulong HashTableTable = 32;

    // MonoClass (common) / MonoClassDef / MonoClassGenericInst
    public const ulong ClassBitFields = 32;
    public const ulong ClassKind = 27;
    public const ulong ClassParent = 48;
    public const ulong ClassNestedIn = 56;
    public const ulong ClassName = 72;
    public const ulong ClassNamespace = 80;
    public const ulong ClassVTableSize = 92;
    public const ulong ClassSizes = 144;
    public const ulong ClassFields = 152;
    public const ulong ClassRuntimeInfo = 208;
    public const ulong ClassGenericClass = 240;
    public const ulong ClassDefFieldCount = 256;
    public const ulong ClassDefNextClassCache = 264;

    public const ulong RuntimeInfoDomainVTables = 8;
    public const ulong VTableMethods = 72;

    // MonoClassField: type*, name*, parent*, int offset (+ padding) = 32 bytes.
    public const ulong FieldSize = 32;
    public const ulong FieldType = 0;
    public const ulong FieldName = 8;
    public const ulong FieldOffset = 24;

    // MonoType: data(8), then attrs:16 | type:8 | flags.
    public const ulong TypeAttrs = 8;

    // Managed objects
    public const ulong ObjectHeader = 16;
    public const ulong StringLength = 16;
    public const ulong StringChars = 20;
    public const ulong ArrayLength = 24;
    public const ulong ArrayData = 32;

    public const ushort FieldAttributeStatic = 0x10;
    public const ushort FieldAttributeLiteral = 0x40;
}

internal enum MonoClassKind : byte
{
    Def = 1,
    Gtd = 2,
    GInst = 3,
    GParam = 4,
    Array = 5,
    Pointer = 6,
}
