using System.Collections.Generic;
using System.Linq;
using STS2Mobile.Platform;
using Xunit;
using static STS2Mobile.Platform.InspectCardMath;

namespace STS2Mobile.Platform.Tests;

public class InspectCardMathTests
{
    private static readonly Size Keyword = new(367f, 151f);
    private static readonly Size Preview = new(239f, 323f);

    private static Layout Compute(float height, IReadOnlyList<Size> text, IReadOnlyList<Size> previews,
        bool upgrade = true) =>
        InspectCardMath.Compute(new Request(
            CanvasWidth: 1080f,
            CanvasHeight: height,
            CardCentreX: 540f,
            CardCentreY: height * 0.5f,
            CardScale: 2f,
            TextTips: text,
            CardPreviews: previews,
            Upgrade: upgrade
                ? new Rect(540f - 122.5f, height * 0.5f + 422f + 18f, 245f, 127f)
                : default,
            PreferredScale: 1.4f,
            MinScale: InspectCardMath.MinReadableScale,
            LeftArrow: new Rect(52f, height * 0.5f - 64f, 128f, 128f),
            RightArrow: new Rect(900f, height * 0.5f - 64f, 128f, 128f)));

    [Theory]
    [InlineData(1920f)]
    [InlineData(2340f)]
    public void AKeywordCardWithOnePreviewKeepsBothOffTheCard(float height)
    {
        Layout layout = Compute(height, new[] { Keyword, Keyword }, new[] { Preview });
        Block text = layout.Text ?? throw new Xunit.Sdk.XunitException("no text block");
        Block cards = layout.Cards ?? throw new Xunit.Sdk.XunitException("no card block");
        Assert.False(text.Rect.Intersects(layout.Card));
        Assert.False(cards.Rect.Intersects(layout.Card));
        Assert.False(text.Rect.Intersects(cards.Rect));
        Assert.True(text.Scale >= InspectCardMath.MinReadableScale);
        Assert.True(cards.Scale >= InspectCardMath.MinReadableScale);
        AssertRectInsideCanvas(text.Rect, height);
        AssertRectInsideCanvas(cards.Rect, height);
    }

    [Theory]
    [InlineData(1920f)]
    [InlineData(2340f)]
    public void ManyKeywordTipsWrapIntoRowsAboveTheCard(float height)
    {
        Layout layout = Compute(height, Enumerable.Repeat(Keyword, 6).ToList(), System.Array.Empty<Size>());
        Block text = layout.Text ?? throw new Xunit.Sdk.XunitException("no text block");
        Assert.False(text.Rect.Intersects(layout.Card));
        Assert.True(text.Scale >= InspectCardMath.MinReadableScale);
        Assert.True(text.Rect.Height > Keyword.Height * text.Scale * 1.5f);
        AssertRectInsideCanvas(text.Rect, height);
    }

    [Theory]
    [InlineData(1920f)]
    [InlineData(2340f)]
    public void AStackOfPreviewsStacksInTheSideMargin(float height)
    {
        Layout layout = Compute(height, new[] { Keyword }, new[] { Preview, Preview });
        Block cards = layout.Cards ?? throw new Xunit.Sdk.XunitException("no card block");
        Assert.False(cards.Rect.Intersects(layout.Card));
        Assert.True(cards.Rect.CentreX > layout.Card.Right);
        Assert.True(cards.Scale >= 0.9f);
    }

    [Theory]
    [InlineData(1920f)]
    [InlineData(2340f)]
    public void TheUpgradeBoxIsCentredUnderTheCardAtAFingerHeight(float height)
    {
        Layout layout = Compute(height, System.Array.Empty<Size>(), System.Array.Empty<Size>());
        Assert.Equal(layout.Card.CentreX, layout.Upgrade.CentreX, 1);
        Assert.Equal(layout.Card.Bottom + 18f, layout.Upgrade.Y, 1);
        Assert.Equal(127f, layout.Upgrade.Height, 1);
        Assert.False(layout.Upgrade.Intersects(layout.Card));
    }

    [Fact]
    public void AnEnormousTipStackShrinksRatherThanCoveringTheCard()
    {
        Layout layout = Compute(1920f, Enumerable.Repeat(Keyword, 20).ToList(), new[] { Preview, Preview, Preview });
        Block text = layout.Text ?? throw new Xunit.Sdk.XunitException("no text block");
        Assert.False(text.Rect.Intersects(layout.Card));
        Block cards = layout.Cards ?? throw new Xunit.Sdk.XunitException("no card block");
        Assert.False(cards.Rect.Intersects(layout.Card));
        Assert.False(text.Rect.Intersects(cards.Rect));
    }

    [Fact]
    public void PackFillsRowsNoWiderThanTheLimit()
    {
        Grid grid = InspectCardMath.Pack(new[] { new Size(100f, 20f), new Size(100f, 20f), new Size(100f, 20f) }, 210f, 8f);
        Assert.Equal(2, grid.Offsets.Select(o => o.Y).Distinct().Count());
        Assert.True(grid.Size.Width <= 210f);
        Assert.Equal(48f, grid.Size.Height, 1);
    }

    private static void AssertRectInsideCanvas(Rect rect, float height)
    {
        Assert.True(rect.X >= 0f);
        Assert.True(rect.Y >= 0f);
        Assert.True(rect.Right <= 1080f + 0.01f);
        Assert.True(rect.Bottom <= height + 0.01f);
    }
}
