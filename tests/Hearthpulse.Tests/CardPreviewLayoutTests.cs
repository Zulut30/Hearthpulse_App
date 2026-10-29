using Hearthpulse.UI.Controls;

namespace Hearthpulse.Tests;

/// <summary>
/// Where both UIs open the hover preview of a card (shared presentation, <c>Controls/CardPreviewLayout.cs</c>), on a
/// 1200 × 800 host with a 236 × 358 preview: under small marks, against grid cards, beside floating panels on the side
/// facing the centre, and on window pages beside the row's list, never over the row, always inside the host.
/// </summary>
public sealed class CardPreviewLayoutTests
{
    private const double Width = 236;
    private const double Height = 358;

    private static readonly Func<PreviewRect> NotAsked = () => throw new InvalidOperationException("not needed for this placement");

    private static (double Left, double Top) Place(
        CardPreviewPlacement placement, PreviewRect row, Func<PreviewRect>? panel = null, Func<PreviewRect>? list = null) =>
        CardPreviewLayout.Place(placement, row, Width, Height, 1200, 800, panel ?? NotAsked, list ?? NotAsked);

    [Fact]
    public void Below_centres_the_preview_under_the_mark()
    {
        Assert.Equal((402, 60), Place(CardPreviewPlacement.Below, new(500, 20, 40, 30)));
        // Near the left edge it is pulled back inside.
        Assert.Equal(4, Place(CardPreviewPlacement.Below, new(10, 20, 40, 30)).Left);
    }

    [Fact]
    public void Adjacent_opens_right_against_the_card_or_left_of_it_without_room()
    {
        Assert.Equal((384, 76), Place(CardPreviewPlacement.Adjacent, new(300, 200, 80, 110)));
        Assert.Equal(760, Place(CardPreviewPlacement.Adjacent, new(1000, 200, 80, 110)).Left);
    }

    [Fact]
    public void Beside_a_floating_panel_opens_on_the_side_facing_the_centre()
    {
        PreviewRect row = new(40, 300, 200, 30);

        var leftPanel = Place(CardPreviewPlacement.Beside, row, panel: () => new(20, 100, 260, 500));
        Assert.Equal((290, 136), leftPanel);

        var rightPanel = Place(CardPreviewPlacement.Beside, row with { X = 920 }, panel: () => new(900, 100, 260, 500));
        Assert.Equal(654, rightPanel.Left);
    }

    [Fact]
    public void On_a_page_it_opens_beside_the_rows_list_at_the_row()
    {
        Func<PreviewRect> page = () => new(0, 0, 1000, 800);

        // Right of the list; at the row's height, pulled down inside the host.
        Assert.Equal((390, 4), Place(CardPreviewPlacement.Beside, new(100, 60, 250, 30), page, () => new(80, 40, 300, 500)));

        // The list fills the page: beside the row instead.
        Assert.Equal(360, Place(CardPreviewPlacement.Beside, new(100, 300, 250, 30), page, () => new(0, 0, 1200, 800)).Left);
    }

    [Fact]
    public void A_row_as_wide_as_the_page_gets_the_preview_under_it_or_over_it()
    {
        Func<PreviewRect> page = () => new(0, 0, 1200, 800);

        Assert.Equal((10, 340), Place(CardPreviewPlacement.Beside, new(0, 300, 1200, 30), page, () => new(0, 300, 1200, 30)));
        Assert.Equal((10, 232), Place(CardPreviewPlacement.Beside, new(0, 600, 1200, 30), page, () => new(0, 600, 1200, 30)));
    }

    [Fact]
    public void A_host_smaller_than_the_preview_keeps_it_at_the_top_left()
    {
        var at = CardPreviewLayout.Place(CardPreviewPlacement.Below, new(50, 50, 20, 20), Width, Height, 200, 300, NotAsked, NotAsked);

        Assert.Equal((CardPreviewLayout.Edge, CardPreviewLayout.Edge), at);
    }
}
