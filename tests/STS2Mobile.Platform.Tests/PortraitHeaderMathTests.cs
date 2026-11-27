namespace STS2Mobile.Platform.Tests;

public class PortraitHeaderMathTests
{
    // The launcher's bands (PortraitBands): 88 unit status row, 140 unit potion band, 222 unit relic band, 106 unit slots.
    private const float TopBarRow = 88f, PotionRow = 140f, TopBar = 228f, Band = 222f, Cap = 162f, Slot = 106f;
    private const float MapDeck = 262f, Margin = 10f, Gap = 8f;

    [Fact]
    public void PotionRowSitsLeftOfTheMapAndDeckButtonsInTheSecondRow()
    {
        HeaderBox row = PortraitHeaderMath.PotionRow(1080f, Cap, TopBarRow, PotionRow, MapDeck, Margin, Gap);
        HeaderBox buttons = PortraitHeaderMath.MapDeckRow(1080f, Cap, TopBarRow, PotionRow, MapDeck, 127f, Margin);
        Assert.Equal(Margin, row.Left, 3);
        // Below the top row and the ascension number's ink, which reaches 3 units past the row.
        Assert.Equal(Cap + TopBarRow + PortraitHeaderMath.PotionRowDrop, row.Top, 3);
        Assert.True(row.Top > Cap + TopBarRow + 3f);
        Assert.Equal(119f, row.Height, 3);
        Assert.Equal(790f, row.Width, 3);
        Assert.Equal(row.Right + Gap, buttons.Left, 3);
        Assert.Equal(1080f - Margin, buttons.Right, 3);
        // The buttons keep the second row's band.
        Assert.Equal(Cap + TopBarRow + PotionRow / 2f, buttons.Top + buttons.Height / 2f, 3);
        // The tray's dark box (the slot plus 4 units, 2 below the row's top) ends on the stone, above its 20 unit torn edge,
        // and inside the row.
        Assert.True(row.Top + 2f + Slot + 4f <= Cap + TopBar - 20f);
        Assert.True(row.Top + 2f + Slot + 4f <= row.Bottom);
    }

    [Fact]
    public void PotionRowKeepsAWholeNumberOfSlotsForTheDefaultCapacity()
    {
        // Three 106 unit slots, 4 units apart, plus the tray's 10 unit margins fit the row twice over.
        float tray = PortraitHeaderMath.PotionStripWidth(3, Slot, 4f, Margin, Margin);
        Assert.Equal(346f, tray, 3);
        Assert.True(tray < 790f);
    }

    [Fact]
    public void TwelvePotionSlotsScrollInsteadOfShrinking()
    {
        float tray = PortraitHeaderMath.PotionStripWidth(12, Slot, 4f, Margin, Margin);
        Assert.Equal(1336f, tray, 3);
        // Nearly two rows' worth of swiping, inside the eight the acceptance allows.
        Assert.True(tray - 790f <= 8f * 790f);
    }

    [Fact]
    public void RelicClipStartsUnderTheStoneAndEndsAtTheReservedBandBottom()
    {
        HeaderBox clip = PortraitHeaderMath.RelicClip(1080f, Cap, TopBar, Band, 20f, 14f);
        Assert.Equal(20f, clip.Left, 3);
        Assert.Equal(Cap + TopBar + 14f, clip.Top, 3);
        Assert.Equal(1040f, clip.Width, 3);
        Assert.Equal(Band - 14f, clip.Height, 3);
        Assert.Equal(Cap + TopBar + Band, clip.Bottom, 3);
    }

    [Theory]
    [InlineData(0, 0f)]
    [InlineData(10, 96f)]
    [InlineData(11, 200f)]
    [InlineData(30, 304f)]
    [InlineData(41, 512f)]
    public void WrappedRelicsTakeAWholeNumberOfLines(int count, float expected)
    {
        Assert.Equal(expected, PortraitHeaderMath.RelicContentHeight(count, 1040f, 96f, 8f), 3);
    }

    [Fact]
    public void RelicColumnsFollowTheClipWidth()
    {
        Assert.Equal(10, PortraitHeaderMath.RelicColumns(1040f, 96f, 8f));
        Assert.Equal(9, PortraitHeaderMath.RelicColumns(1000f, 96f, 8f));
        Assert.Equal(1, PortraitHeaderMath.RelicColumns(100f, 96f, 8f));
        Assert.Equal(1, PortraitHeaderMath.RelicColumns(0f, 96f, 8f));
    }

    [Fact]
    public void ThreeLinesOfRelicsAreTallerThanTheBand()
    {
        // Thirty relics do not fit the fixed band, so the band scrolls rather than growing into the stage.
        Assert.True(PortraitHeaderMath.RelicContentHeight(30, 1040f, 96f, 8f) > Band);
        Assert.True(PortraitHeaderMath.RelicContentHeight(41, 1040f, 96f, 8f) > Band);
    }

    [Theory]
    [InlineData(2560, 128, 30, 20, 30, 30)]
    [InlineData(1280, 100, 30, 20, 30, 30)]
    [InlineData(40, 20, 19, 9, 19, 9)]
    [InlineData(0, 0, 0, 0, 0, 0)]
    public void StonePatchMarginsFollowTheHeaderAndFitTheTexture(int width, int height, int left, int top, int right, int bottom)
    {
        StonePatchMargins margins = PortraitHeaderMath.StonePatchMargins(width, height);
        Assert.Equal(new StonePatchMargins(left, top, right, bottom), margins);
    }

    [Fact]
    public void PopupDropsBelowTheRelicBandAndGrowsTheButtonsTo44Points()
    {
        // The game's popup is 259 x 239 with 86 unit buttons; 127 / 86 is the scale that makes them 44 points.
        PopupPlacement placement = PortraitHeaderMath.PotionPopup(
            safeLeft: 10f, safeTop: Cap, safeRight: 1070f, safeBottom: 2340f,
            anchorX: 90f, inventoryBottom: Cap + TopBar + Band, popupWidth: 259f, popupHeight: 239f,
            buttonScale: 127f / 86f, maxScale: 1.6f, gap: 24f);
        Assert.True(placement.Visible);
        Assert.Equal(Cap + TopBar + Band + 24f, placement.Y, 3);
        Assert.Equal(127f, 86f * placement.Scale, 2);
        // Centred on the holder, pushed in from the left edge.
        Assert.Equal(10f, placement.X, 3);
        Assert.True(placement.X + placement.DrawnWidth(259f) <= 1070f);
    }

    [Fact]
    public void PopupCentresOnAHolderWithRoomAroundIt()
    {
        // The buttons never shrink below the game's own height, so the smallest popup is the native size.
        PopupPlacement placement = PortraitHeaderMath.PotionPopup(10f, Cap, 1070f, 2340f, 540f, 612f, 259f, 239f, 1f, 1.6f, 24f);
        Assert.Equal(1f, placement.Scale, 3);
        Assert.Equal(540f - 259f / 2f, placement.X, 3);
    }

    [Fact]
    public void PopupShrinksRatherThanLeavingTheScreen()
    {
        // On a short canvas the room below the band decides the scale: 300 units of room for a 239 unit popup.
        PopupPlacement placement = PortraitHeaderMath.PotionPopup(10f, Cap, 1070f, 912f, 540f, 588f, 259f, 239f, 1.5f, 1.6f, 24f);
        Assert.Equal(300f / 239f, placement.Scale, 3);
        Assert.Equal(612f, placement.Y, 3);
        Assert.True(placement.Y + placement.DrawnHeight(239f) <= 912f);
    }

    [Fact]
    public void PopupIsNotPlacedWhenNoRoomIsLeftBelowTheBand()
    {
        // The safe frame ends at the band: the popup is left to the game rather than drawn over the header.
        PopupPlacement placement = PortraitHeaderMath.PotionPopup(10f, Cap, 1070f, 670f, 540f, 650f, 259f, 239f, 1.5f, 1.6f, 24f);
        Assert.False(placement.Visible);
    }

    [Fact]
    public void PopupNeverRisesAboveTheSafeArea()
    {
        PopupPlacement atTop = PortraitHeaderMath.PotionPopup(10f, Cap, 1070f, 2340f, 540f, 100f, 259f, 239f, 1.5f, 1.6f, 24f);
        Assert.Equal(Cap, atTop.Y, 3);
    }
}
