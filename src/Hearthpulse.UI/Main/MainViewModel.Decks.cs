using System.Globalization;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Stats;

namespace Hearthpulse.UI.Main;

public sealed partial class MainViewModel
{
    private string DeckMeta(string? deckCode, string? heroClass, string format) =>
        ConstructedStats.ArchetypeOf(deckCode, heroClass, ConstructedMetaFor(format), _cards) is { } archetype
            ? Loc.F("Main.Meta.DeckInMeta", archetype.Name, archetype.WinRate.ToString("P0", CultureInfo.CurrentCulture))
            : string.Empty;
}
