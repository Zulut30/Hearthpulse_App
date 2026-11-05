using Hearthpulse.Core.Memory;

namespace Hearthpulse.Tests;

/// <summary>
/// T3 Memory: the walk over the game's Mono runtime on a hand-built image (<see cref="MonoImageBuilder"/>) laid out
/// as Unity's mono-2.0-bdwgc is: attaching through the root domain to Assembly-CSharp's class table, then two reads
/// the overlay lives on, the Battlegrounds rating (NetCache through the service locator) and the Arena draft offer
/// (DraftDisplay's static instance). A wrong offset in <see cref="MonoOffsets"/> fails these.
/// </summary>
public sealed class MonoRuntimeTests
{
    // Mono type codes of the fields below.
    private const byte I4 = 0x08, Boolean = 0x02, String = 0x0E, Class = 0x12, SzArray = 0x1D;

    [Fact]
    public void Attaching_walks_the_root_domain_to_assembly_csharp_and_indexes_its_classes()
    {
        var game = new Game();
        using var memory = new HearthstoneMemory();

        Assert.True(memory.AttachTo(ProcessMemory.FromSource(game.Build())), memory.Status);
        var runtime = memory.Runtime!;
        Assert.True(runtime.ClassCount > 500);
        Assert.Equal("Attached · " + runtime.ClassCount.ToString("N0", System.Globalization.CultureInfo.CurrentCulture) + " classes", memory.Status);
        var rating = runtime.FindClass("NetCacheBaconRatingInfo")!;
        Assert.Equal(("NetCacheBaconRatingInfo", string.Empty), (rating.Name, rating.Namespace));
        Assert.Equal("Hearthstone.HearthstoneJobs", runtime.FindClass("HearthstoneJobs")!.FullName);
        Assert.Equal([("<Rating>k__BackingField", 16, false), ("<DuosRating>k__BackingField", 20, false)],
            rating.Fields.Select(f => (f.Name, f.Offset, f.IsStatic)));
        Assert.True(runtime.FindClass("HearthstoneJobs")!.FindField("s_dependencyBuilder")!.IsStatic);
    }

    [Fact]
    public void The_battlegrounds_rating_is_read_from_the_net_cache_the_service_locator_holds()
    {
        var game = new Game { Solo = 6560, Duos = 7020 };
        using var memory = new HearthstoneMemory();
        Assert.True(memory.AttachTo(ProcessMemory.FromSource(game.Build())), memory.Status);

        Assert.Equal(new BattlegroundsRating(6560, 7020), memory.GetBattlegroundsRating());
    }

    [Fact]
    public void The_arena_offer_is_read_from_the_draft_display_with_each_legendarys_package()
    {
        var game = new Game
        {
            Draft = ["CORE_EX1_001", "CORE_CS2_029", "TOY_100"],
            Packages = [[], [], ["TOY_100t", "TOY_100t2"]],
            Underground = true,
        };
        using var memory = new HearthstoneMemory();
        Assert.True(memory.AttachTo(ProcessMemory.FromSource(game.Build())), memory.Status);

        var offer = memory.GetArenaDraftChoices()!;
        Assert.Equal(["CORE_EX1_001", "CORE_CS2_029", "TOY_100"], offer.CardIds);
        Assert.True(offer.Underground);
        Assert.Equal(["TOY_100t", "TOY_100t2"], offer.Packages![2]);
        Assert.Empty(offer.Packages[0]);
    }

    [Fact]
    public void Without_a_draft_screen_or_a_rating_the_reads_give_nothing()
    {
        var game = new Game { Solo = 0, Duos = 0 };
        using var memory = new HearthstoneMemory();
        Assert.True(memory.AttachTo(ProcessMemory.FromSource(game.Build())), memory.Status);

        Assert.Null(memory.GetBattlegroundsRating());
        Assert.Null(memory.GetArenaDraftChoices());
    }

    [Fact]
    public void A_class_table_too_small_to_be_the_games_is_not_attached_to()
    {
        var game = new Game { Fillers = 10 };
        using var memory = new HearthstoneMemory();

        Assert.False(memory.AttachTo(ProcessMemory.FromSource(game.Build())));
        Assert.StartsWith("Class cache looks wrong", memory.Status, StringComparison.Ordinal);
        Assert.False(memory.AttachTo(ProcessMemory.FromSource(new MemoryImage())));
        Assert.Equal("Mono runtime not loaded yet", memory.Status);
    }

    /// <summary>The objects of a client: the service locator with NetCache, and the Arena draft screen when it is open.</summary>
    private sealed class Game
    {
        public int Fillers { get; init; } = 520;

        public int Solo { get; init; } = 6500;

        public int Duos { get; init; }

        public string[]? Draft { get; init; }

        public string[][]? Packages { get; init; }

        public bool Underground { get; init; }

        public MemoryImage Build()
        {
            var b = new MonoImageBuilder(Fillers);
            var objects = b.ArrayClass("Object[]", 8);
            var entries = b.ArrayClass("Entry[]", 24);
            var list = b.Class("System.Collections.Generic.List`1", ("_items", 16, false, SzArray), ("_size", 24, false, I4));
            var dictionary = b.Class("System.Collections.Generic.Dictionary`2", ("_entries", 16, false, SzArray), ("_count", 24, false, I4));
            var jobs = b.Class("Hearthstone.HearthstoneJobs", ("s_dependencyBuilder", 8, true, Class));
            var builder = b.Class("Hearthstone.DependencyBuilder", ("m_serviceLocator", 16, false, Class));
            var locator = b.Class("Hearthstone.ServiceLocator", ("m_services", 16, false, Class));
            var service = b.Class("Hearthstone.ServiceRecord",
                ("<ServiceTypeName>k__BackingField", 16, false, String), ("<Service>k__BackingField", 24, false, Class));
            var netCacheClass = b.Class("NetCache", ("m_netCache", 16, false, Class));
            var map = b.Class("Blizzard.T5.Core.Map`2", ("valueSlots", 16, false, SzArray));
            var ratingClass = b.Class("NetCacheBaconRatingInfo", ("<Rating>k__BackingField", 16, false, I4), ("<DuosRating>k__BackingField", 20, false, I4));
            var otherCache = b.Class("NetCacheMedalInfo", ("m_unused", 16, false, I4));
            var display = b.Class("DraftDisplay",
                ("s_instance", 0, true, Class), ("m_choices", 16, false, Class), ("m_choicesAreUnderground", 24, false, Boolean));
            var choice = b.Class("DraftDisplay+Choice", ("m_cardID", 16, false, String), ("m_packageCardIds", 24, false, Class));

            ulong List(params ulong[] items)
            {
                var array = b.Array(objects, items.Length);
                for (var i = 0; i < items.Length; i++)
                {
                    b.Ptr(b.Element(array, i), items[i]);
                }

                var obj = b.Object(list);
                b.Ptr(obj + 16, array);
                b.Int(obj + 24, items.Length);
                return obj;
            }

            // NetCache's map: another cache first, then the rating, and an empty slot.
            var rating = b.Object(ratingClass);
            b.Int(rating + 16, Solo);
            b.Int(rating + 20, Duos);
            var slots = b.Array(objects, 3);
            b.Ptr(b.Element(slots, 0), b.Object(otherCache));
            b.Ptr(b.Element(slots, 1), rating);
            var netCacheMap = b.Object(map);
            b.Ptr(netCacheMap + 16, slots);
            var netCache = b.Object(netCacheClass);
            b.Ptr(netCache + 16, netCacheMap);

            // The service locator's dictionary: entries of 24 bytes (hash, next, key, value), the value last.
            var records = b.Array(entries, 2, 24);
            ulong Record(string name, ulong instance)
            {
                var record = b.Object(service);
                b.Ptr(record + 16, b.MonoString(name));
                b.Ptr(record + 24, instance);
                return record;
            }

            b.Ptr(b.Element(records, 0, 24) + 16, Record("GameMgr", b.Object(otherCache)));
            b.Ptr(b.Element(records, 1, 24) + 16, Record("NetCache", netCache));
            var services = b.Object(dictionary);
            b.Ptr(services + 16, records);
            b.Int(services + 24, 2);
            var serviceLocator = b.Object(locator);
            b.Ptr(serviceLocator + 16, services);
            var dependencyBuilder = b.Object(builder);
            b.Ptr(dependencyBuilder + 16, serviceLocator);
            b.Ptr(b.Statics(jobs) + 8, List(dependencyBuilder));

            if (Draft is { } draft)
            {
                var choices = draft.Select((id, i) =>
                {
                    var obj = b.Object(choice);
                    b.Ptr(obj + 16, b.MonoString(id));
                    b.Ptr(obj + 24, List([.. (Packages?[i] ?? []).Select(b.MonoString)]));
                    return obj;
                }).ToArray();
                var screen = b.Object(display);
                b.Ptr(screen + 16, List(choices));
                b.Bool(screen + 24, Underground);
                b.Ptr(b.Statics(display), screen);
            }

            return b.Build();
        }
    }
}
