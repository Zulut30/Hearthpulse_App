using System.Text.Json;
using Hearthpulse.Core.Arena;

namespace Hearthpulse.Core.Memory;

/// <summary>
/// What a memory fixture records and checks: the reads that need no player's name. The recorder (Bench <c>memory</c>)
/// stores them next to the image; the fixture test reads the image back and compares.
/// </summary>
public sealed record MemoryFixture(
    BattlegroundsRating? Rating,
    int? PostGameRating,
    IReadOnlyList<int>? Races,
    BattlegroundsButtonCosts? ButtonCosts,
    ArenaDraftOffer? ArenaOffer)
{
    public static JsonSerializerOptions Json { get; } = new() { WriteIndented = true };

    public static MemoryFixture Read(HearthstoneMemory memory)
    {
        ArgumentNullException.ThrowIfNull(memory);
        return new(
            memory.GetBattlegroundsRating(),
            memory.GetPostGameBattlegroundsRating(),
            memory.GetBattlegroundsRaces(),
            memory.GetBattlegroundsButtonCosts(),
            memory.GetArenaDraftChoices());
    }
}
