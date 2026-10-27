using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Localization;

namespace Hearthpulse.UI.Main;

public sealed partial class MainViewModel
{
    /// <summary>Community Constructed stats by format key ("standard", "wild"), as the host loaded them.</summary>
    private readonly Dictionary<string, ConstructedMeta> _constructedMetas = new(StringComparer.Ordinal);

    /// <summary>
    /// Raised when the Standard or Wild page has no stats for its format (key "standard" or "wild"); the host downloads
    /// them and calls <see cref="SetConstructedMeta"/>.
    /// </summary>
    public event Action<string>? ConstructedMetaRequested;

    /// <summary>Stats of one format the host loaded (for your decks' archetypes and the deck page); null is ignored.</summary>
    public void SetConstructedMeta(ConstructedMeta? meta)
    {
        if (meta is null)
        {
            return;
        }

        _constructedMetas[meta.Format] = meta;
        RebuildConstructedStats();
        RebuildDecks();
    }

    private static string ConstructedNote(ConstructedMeta? meta) =>
        Loc.T("Stats.Ct.NoteWindowsOnly");

    /// <summary>The class's default hero, for its portrait.</summary>
    internal static string ClassHero(string heroClass) => heroClass.ToUpperInvariant() switch
    {
        "WARRIOR" => "HERO_01",
        "SHAMAN" => "HERO_02",
        "ROGUE" => "HERO_03",
        "PALADIN" => "HERO_04",
        "HUNTER" => "HERO_05",
        "DRUID" => "HERO_06",
        "WARLOCK" => "HERO_07",
        "MAGE" => "HERO_08",
        "PRIEST" => "HERO_09",
        "DEMONHUNTER" => "HERO_10",
        "DEATHKNIGHT" => "HERO_11",
        _ => string.Empty,
    };

    partial void OnShowingConstructedFormat(string formatType)
    {
        // Your decks show their archetype from the page's format stats.
        if (ConstructedMetaFor(formatType) is null && ConstructedMeta.FormatKeyOf(formatType) is { } key)
        {
            ConstructedMetaRequested?.Invoke(key);
        }
    }
}
