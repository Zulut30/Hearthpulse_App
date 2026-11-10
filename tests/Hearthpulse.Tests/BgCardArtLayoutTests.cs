using System.Xml.Linq;

namespace Hearthpulse.Tests;

/// <summary>
/// The art frame of a row on Battlegrounds "Cards", read from the markup of both UIs. The frame is 54x38 and a card's
/// tile is a 256x59 strip with the art at its right end: stretched into the frame it left the frame empty or showed a
/// sliver at one edge. The square portrait crop is the frame's own fill instead, so it covers the frame, centred, and a
/// card without art keeps the frame's plain surface.
/// </summary>
public sealed class BgCardArtLayoutTests
{
    private static readonly string[] ViewFiles =
    [
        "src/Hearthpulse.App/Main/Meta/BattlegroundsTiersView.xaml",
        "src/Hearthpulse.UI/Main/Meta/BattlegroundsTiersView.axaml",
    ];

    public static TheoryData<string> Views => new(ViewFiles);

    [Theory]
    [MemberData(nameof(Views))]
    public void Card_row_fills_its_art_frame_with_the_portrait_crop(string path)
    {
        var row = CardRow(path);

        // No tile strip anywhere in the row.
        Assert.DoesNotContain(row.DescendantsAndSelf().Attributes(), a => a.Name.LocalName == "CardArt.Tile");

        // One art element: the frame itself, bound to the row's card.
        var frame = Assert.Single(row.Descendants(), e => e.Attributes().Any(a => a.Name.LocalName.StartsWith("CardArt.", StringComparison.Ordinal)));
        Assert.Equal("Border", frame.Name.LocalName);
        Assert.Equal("{Binding CardId}", (string?)frame.Attributes().Single(a => a.Name.LocalName == "CardArt.Portrait"));
        Assert.Equal(("54", "38"), ((string?)frame.Attribute("Width"), (string?)frame.Attribute("Height")));

        // The art is the frame's fill (UniformToFill, centred on the crop), not a child laid out inside it; the frame's
        // own background stays as the placeholder of a card without art.
        Assert.Empty(frame.Elements());
        Assert.NotNull(frame.Attribute("Background"));
    }

    [Fact]
    public void Both_UIs_draw_the_same_art_frame()
    {
        var frames = ViewFiles.Select(v => CardRow(v).Descendants().Single(e => e.Attributes().Any(a => a.Name.LocalName == "CardArt.Portrait"))).ToList();
        string?[] Shape(XElement e) => [(string?)e.Attribute("Width"), (string?)e.Attribute("Height"), (string?)e.Attribute("CornerRadius"), (string?)e.Attribute("Background"), (string?)e.Attribute("HorizontalAlignment"), (string?)e.Attribute("Grid.Column")];
        Assert.Equal(Shape(frames[0]), Shape(frames[1]));
    }

    /// <summary>The row template of the card list in a tier-list view: the one that draws a card's art in a 54x38 frame or from its tile.</summary>
    private static XElement CardRow(string relative) =>
        XDocument.Load(Path.Combine(RepoFiles.Root, relative)).Root!.Descendants()
            .Single(e => e.Name.LocalName == "DataTemplate" && e.Descendants().Any(d =>
                d.Attributes().Any(a => a.Name.LocalName == "CardArt.Tile")
                || ((string?)d.Attribute("Width") == "54" && d.Attributes().Any(a => a.Name.LocalName.StartsWith("CardArt.", StringComparison.Ordinal)))));
}
