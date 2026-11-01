using Hearthpulse.Core.Localization;
using WpfOverlay = Hearthpulse.App.Overlay.OverlayViewModel;
using AvaOverlay = Hearthpulse.UI.Overlay.OverlayViewModel;

namespace Hearthpulse.Presentation.Tests;

/// <summary>Plan 2.4: the overlay names a mulligan slice by the rank filter that read it, in both hosts.</summary>
public sealed class MulliganRankContractTests
{
    [Theory]
    [InlineData("diamond_4to1", "Core.MulliganRankSource.diamond")]
    [InlineData("top_5k", "Core.MulliganRankSource.top_legend")]
    [InlineData("legend", "Mulligan.Slice.Legend")]
    [InlineData("all", "Mulligan.Slice.AllRanks")]
    public void Slices_at_a_rank_other_than_legend_are_named_by_that_rank(string sliceRank, string key)
    {
        Assert.Equal(Loc.T(key), WpfOverlay.SliceRankName(sliceRank));
        Assert.Equal(Loc.T(key), AvaOverlay.SliceRankName(sliceRank));
    }
}
