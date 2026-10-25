using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Localization;

namespace Hearthpulse.App.Main;

public sealed partial class MainViewModel
{
    private static string ConstructedNote(ConstructedMeta? meta) => meta is null
        ? Loc.T("Stats.Ct.NoteNoMeta")
        : Loc.F("Stats.Ct.NoteSource", meta.Source, ConstructedMeta.Label(ConstructedMeta.Ranks, meta.Rank));

    partial void OnConstructedFormatChanged() => RebuildConstructedMeta();

    // The tier list and top decks follow the page; the overlay keeps the game's format.
    partial void OnConstructedFormatShown() => RequestPageMeta();
}
