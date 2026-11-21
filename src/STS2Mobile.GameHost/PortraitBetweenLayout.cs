using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using STS2Mobile.Platform;

namespace STS2Mobile.GameHost;

/// <summary>
/// Portrait layout for the between-fight screens: the Spire Map, spoils and card rewards, card grids (deck, piles and
/// card selection), events, the shop, the rest site and the treasure room. Every edit is captured first so turning Portrait Mode
/// off restores the desktop layout live.
/// </summary>
internal sealed partial class PortraitBetweenLayout
{
    public const string MapScreenTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen";
    public const string ProceedButtonTypeName = "MegaCrit.Sts2.Core.Nodes.CommonUi.NProceedButton";
    public const string RewardsScreenTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.NRewardsScreen";
    public const string CardRewardTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NCardRewardSelectionScreen";
    public const string CardGridTypeName = "MegaCrit.Sts2.Core.Nodes.Cards.NCardGrid";
    public const string DeckCardSelectTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NDeckCardSelectScreen";
    public const string MerchantInventoryTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.Shops.NMerchantInventory";
    public const string FakeMerchantInventoryTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.Shops.NFakeMerchantInventory";
    public const string MerchantRoomTypeName = "MegaCrit.Sts2.Core.Nodes.Rooms.NStandardMerchantRoom";
    public const string BlitzMerchantRoomTypeName = "MegaCrit.Sts2.Core.Nodes.Rooms.NBlitzMerchantRoom";
    public const string FakeMerchantRoomTypeName = "MegaCrit.Sts2.Core.Nodes.Events.Custom.NFakeMerchant";
    public const string EventLayoutTypeName = "MegaCrit.Sts2.Core.Nodes.Events.NEventLayout";
    public const string MapFtueTypeName = "MegaCrit.Sts2.Core.Nodes.Ftue.NMapSelectFtue";
    public const string AncientLayoutTypeName = "MegaCrit.Sts2.Core.Nodes.Events.NAncientEventLayout";
    public const string BackButtonTypeName = "MegaCrit.Sts2.Core.Nodes.CommonUi.NBackButton";
    public const string EventOptionTypeName = "MegaCrit.Sts2.Core.Nodes.Events.NEventOptionButton";
    public const string RestSiteRoomTypeName = "MegaCrit.Sts2.Core.Nodes.Rooms.NRestSiteRoom";
    public const string AncientDialogueLineTypeName = "MegaCrit.Sts2.Core.Nodes.Events.NAncientDialogueLine";

    public static readonly HashSet<string> WatchedTypeNames = new(StringComparer.Ordinal)
    {
        MapScreenTypeName, ProceedButtonTypeName, RewardsScreenTypeName, CardRewardTypeName, CardGridTypeName, DeckCardSelectTypeName,
        MerchantInventoryTypeName, FakeMerchantInventoryTypeName, EventLayoutTypeName, MapFtueTypeName, AncientLayoutTypeName,
        BackButtonTypeName, EventOptionTypeName, RestSiteRoomTypeName, AncientDialogueLineTypeName,
        MerchantRoomTypeName, BlitzMerchantRoomTypeName, FakeMerchantRoomTypeName,
        EventRoomTypeName, CombatEventLayoutTypeName,
    };

    // Card grids in portrait: two cards to a row at 1.8, each card 432 units (150 points) wide. The grid's
    // cards are 240 units with 40 between, so a 540 unit scroll area holds two, 40 units from each side: a selected card grows
    // to 300 and its cost badge still stays inside; the scrollbar sits in the right margin.
    private const float GridScrollInsetLeft = 30f;
    private const float GridScrollInsetRight = 30f;
    private const float GridScrollbarLeft = -38f;
    private const float GridScrollbarRight = -8f;
    // The deck's sort buttons stand in one row above the cards (which start 180 units down the scroll area), 75 units tall.
    private const float SortRowTop = 98f;
    private const float SortRowHeight = 75f;
    private const float SortRowGap = 6f;
    private const float SortButtonPad = 4f;

    // Sizes for a finger. 127 units are 44 points and a font of 32 units is 11 points (PortraitBands).
    private const string HitPadHolderName = PhoneHitArea.HolderName;
    private const float RewardsScale = 1.5f;
    private const float RewardsPivotY = 400f;
    private const int EventTextFontSize = 32;
    private const float RestChoicesMaxScale = 1.35f;
    private const float RestChoicesMinScale = 0.6f;
    private const float RestChoicesPivotY = 80f;
    private static readonly string[] RichFontSizeNames =
        ["normal_font_size", "bold_font_size", "bold_italics_font_size", "italics_font_size", "mono_font_size"];
    private const float CardRowSpacing = 350f;
    private const float RewardCardWidth = 300f;
    private const float RewardCardHeight = 422f;
    private const float RewardMaxCardScale = 1.5f;
    private const float GridScale = 1.8f;
    // The shared banner ("Choose a Card") is 162 units tall, 172 to 334 above the screen's middle; in portrait it rises to just
    // under the relic strip so the cards under it can grow.
    internal const float BannerTopOffset = 334f;
    internal const float BannerBottomOffset = 172f;
    private const float BannerContentGap = 16f;

    private static readonly Color PillFillColor = new(24f / 255f, 37f / 255f, 45f / 255f, 0.94f);
    private static readonly Color PillBorderColor = new(118f / 255f, 146f / 255f, 162f / 255f, 0.9f);
    private static readonly Color PillTextColor = new(239f / 255f, 200f / 255f, 81f / 255f, 1f);

    private readonly Dictionary<ulong, PortraitControlSnapshot> _snapshots = new();
    private readonly HashSet<ulong> _proceedButtons = new();
    private readonly HashSet<ulong> _cardRewards = new();
    private readonly Dictionary<ulong, List<ulong>> _laidOutRewardHolders = new();
    private readonly HashSet<ulong> _mapFtues = new();
    private readonly HashSet<ulong> _grids = new();
    private readonly HashSet<ulong> _hitAreaTargets = new();
    private readonly HashSet<ulong> _liftedBanners = new();
    private readonly HashSet<ulong> _shops = new();
    private readonly HashSet<ulong> _hitPadHolders = new();
    private readonly HashSet<ulong> _events = new();
    private readonly HashSet<ulong> _restSites = new();
    private readonly Dictionary<ulong, int> _labelMaxFontSizes = new();
    private readonly Dictionary<ulong, int> _labelMinFontSizes = new();
    private readonly Dictionary<ulong, (bool Had, int Size)> _labelFontSizes = new();
    private readonly Dictionary<ulong, (bool Had, int Size)[]> _richFontSizes = new();

    // ---- entry points from PortraitLayout -------------------------------------------------------------------------------

    private readonly HashSet<ulong> _loggedNodes = new();

    public void ApplyNode(Control control, string typeName, float capHeight, float canvasHeight, float canvasWidth)
    {
        if (_loggedNodes.Add(control.GetInstanceId()))
            GD.Print($"Portrait between layout: {control.GetType().Name} {control.GetPath()}");
        switch (typeName)
        {
            case MapScreenTypeName:
                LayoutMapPortrait(control, capHeight, canvasHeight, canvasWidth);
                _maps.Add(control.GetInstanceId());
                break;
            case ProceedButtonTypeName:
                _proceedButtons.Add(control.GetInstanceId());
                EnsureHitPad(control, 0f, PortraitBands.MinTarget);
                break;
            case BackButtonTypeName:
                EnsureHitPad(control, 0f, PortraitBands.MinTarget);
                break;
            case EventOptionTypeName:
                if (IsInLayout(control, AncientLayoutTypeName)) LayoutAncientOptionPortrait(control);
                else ApplyEventOption(control);
                break;
            case AncientDialogueLineTypeName:
                LayoutAncientDialogueLinePortrait(control);
                break;
            case RestSiteRoomTypeName:
                _restSites.Add(control.GetInstanceId());
                break;
            case RewardsScreenTypeName:
                LayoutRewardsPortrait(control);
                break;
            case CardRewardTypeName:
                _cardRewards.Add(control.GetInstanceId());
                break;
            case CardGridTypeName:
                LayoutCardGridPortrait(control, capHeight, canvasHeight, canvasWidth);
                if (control.GetType().FullName == CardGridTypeName) _grids.Add(control.GetInstanceId());
                break;
            case DeckCardSelectTypeName:
                LayoutShopRemovalPreview(control);
                break;
            case AncientLayoutTypeName:
                LayoutAncientPortrait(control, capHeight, canvasWidth, canvasHeight);
                break;
            case MerchantInventoryTypeName:
                _shops.Add(control.GetInstanceId());
                UpdateShopPortrait(control, capHeight, new Vector2(canvasWidth, canvasHeight));
                break;
            case FakeMerchantInventoryTypeName:
                _shops.Add(control.GetInstanceId());
                UpdateShopPortrait(control, capHeight, new Vector2(canvasWidth, canvasHeight));
                break;
            case MerchantRoomTypeName:
            case BlitzMerchantRoomTypeName:
            case FakeMerchantRoomTypeName:
                ApplyShopRoom(control, typeName, canvasWidth, canvasHeight);
                break;
            case EventLayoutTypeName:
                LayoutEventPortrait(control, capHeight, canvasHeight, canvasWidth);
                break;
            case CombatEventLayoutTypeName:
                LayoutCombatEventPortrait(control, capHeight, canvasHeight, canvasWidth);
                break;
            case EventRoomTypeName:
                TrackEventRoom(control, canvasWidth, canvasHeight);
                break;
            case MapFtueTypeName:
                _mapFtues.Add(control.GetInstanceId());
                break;
        }
    }

    /// <summary>
    /// Called as a node is added, before the game sets its screen up: an ancient's option rows and dialogue lines get their
    /// portrait size here, because the game measures them for the column's slide in the frame they are added.
    /// Returns whether it changed anything.
    /// </summary>
    public bool LayoutOnAdd(Control control)
    {
        string? typeName = control.GetType().FullName;
        if (typeName == AncientDialogueLineTypeName)
        {
            LayoutAncientDialogueLinePortrait(control);
            return true;
        }
        if (typeName == EventOptionTypeName && IsInLayout(control, AncientLayoutTypeName))
        {
            LayoutAncientOptionPortrait(control);
            return true;
        }
        if (typeName == EventOptionTypeName) return ApplyEventOption(control);
        if (typeName == EventLayoutTypeName) return HoldEventArtUntilFramed(control);
        return false;
    }

    private static bool IsInLayout(Node node, string layoutTypeName)
    {
        for (Node? parent = node.GetParent(); parent != null; parent = parent.GetParent())
            if (parent.GetType().FullName == layoutTypeName) return true;
        return false;
    }

    public void OnProcessFrame(Window root, float capHeight)
    {
        Vector2 canvas = root.GetVisibleRect().Size;
        foreach (ulong id in _maps)
            if (GodotObject.InstanceFromId(id) is Control map && GodotObject.IsInstanceValid(map) && map.IsVisibleInTree())
                UpdateMapPortrait(map, root, canvas);
        foreach (ulong id in _proceedButtons)
            if (GodotObject.InstanceFromId(id) is Control button && GodotObject.IsInstanceValid(button) && button.IsVisibleInTree())
                UpdateProceedButtonPortrait(button, canvas);
        foreach (ulong id in _cardRewards)
            if (GodotObject.InstanceFromId(id) is Control screen && GodotObject.IsInstanceValid(screen) && screen.IsInsideTree())
                UpdateCardRewardPortrait(screen, canvas, capHeight);
        foreach (ulong id in _grids)
            if (GodotObject.InstanceFromId(id) is Control grid && GodotObject.IsInstanceValid(grid) && grid.IsVisibleInTree())
                KeepGridColumnsPortrait(grid);
        foreach (ulong id in _mapFtues)
            if (GodotObject.InstanceFromId(id) is Control ftue && GodotObject.IsInstanceValid(ftue) && ftue.IsVisibleInTree())
                KeepMapFtueOnScreen(ftue, canvas);
        foreach (ulong id in _events)
            if (GodotObject.InstanceFromId(id) is Control layout && GodotObject.IsInstanceValid(layout) && layout.IsVisibleInTree())
                UpdateEventPortrait(layout, capHeight, canvas);
        foreach (ulong id in _eventRooms)
            if (GodotObject.InstanceFromId(id) is Control room && GodotObject.IsInstanceValid(room) && room.IsInsideTree())
                UpdateEventRoomCover(room, canvas);
        foreach (ulong id in _restSites)
            if (GodotObject.InstanceFromId(id) is Control room && GodotObject.IsInstanceValid(room) && room.IsVisibleInTree())
                UpdateRestSitePortrait(room, canvas);
        UpdateAncientPortraits(capHeight, canvas);
        foreach (ulong id in _shops)
            if (GodotObject.InstanceFromId(id) is Control shop && GodotObject.IsInstanceValid(shop) && shop.IsVisibleInTree())
                UpdateShopPortrait(shop, capHeight, canvas);
    }

    public void RestoreLandscape()
    {
        foreach (ulong id in _cardRewards)
            if (GodotObject.InstanceFromId(id) is Control screen && GodotObject.IsInstanceValid(screen))
                RestoreCardRewardLandscape(screen);
        _laidOutRewardHolders.Clear();
        RestoreEventsLandscape();

        foreach ((ulong id, PortraitControlSnapshot snapshot) in _snapshots)
            if (GodotObject.InstanceFromId(id) is Control control && GodotObject.IsInstanceValid(control))
                snapshot.Restore(control);

        RestoreMapLandscape();

        foreach (ulong id in _proceedButtons)
            if (GodotObject.InstanceFromId(id) is Control button && GodotObject.IsInstanceValid(button))
                RestoreProceedButtonLandscape(button);

        // The larger hit areas, the map points' size and the larger text go back to the game's own.
        foreach (ulong id in _hitAreaTargets)
            if (GodotObject.InstanceFromId(id) is Control target && GodotObject.IsInstanceValid(target))
                PhoneHitArea.Remove(target);
        _hitAreaTargets.Clear();
        RestoreBanners(_liftedBanners);
        RestoreShopRoomsLandscape();
        RestoreShopPreviewButtons();
        foreach (ulong id in _shops)
            if (GodotObject.InstanceFromId(id) is Control shop && GodotObject.IsInstanceValid(shop))
                RestoreShopLandscape(shop);
        foreach (ulong id in _hitPadHolders)
        {
            if (GodotObject.InstanceFromId(id) is not Control holder || !GodotObject.IsInstanceValid(holder)) continue;
            holder.GetParent()?.RemoveChild(holder);
            holder.QueueFree();
        }
        _hitPadHolders.Clear();
        foreach ((ulong id, int size) in _labelMaxFontSizes)
            if (GodotObject.InstanceFromId(id) is Control label && GodotObject.IsInstanceValid(label))
                label.Set("MaxFontSize", size);
        _labelMaxFontSizes.Clear();
        foreach ((ulong id, int size) in _labelMinFontSizes)
            if (GodotObject.InstanceFromId(id) is Control label && GodotObject.IsInstanceValid(label))
                label.Set("MinFontSize", size);
        _labelMinFontSizes.Clear();
        foreach ((ulong id, (bool had, int size)) in _labelFontSizes)
        {
            if (GodotObject.InstanceFromId(id) is not Control label || !GodotObject.IsInstanceValid(label)) continue;
            if (had) label.AddThemeFontSizeOverride("font_size", size);
            else label.RemoveThemeFontSizeOverride("font_size");
        }
        _labelFontSizes.Clear();
        foreach ((ulong id, (bool Had, int Size)[] sizes) in _richFontSizes)
        {
            if (GodotObject.InstanceFromId(id) is not Control label || !GodotObject.IsInstanceValid(label)) continue;
            for (int i = 0; i < RichFontSizeNames.Length; i++)
            {
                if (sizes[i].Had) label.AddThemeFontSizeOverride(RichFontSizeNames[i], sizes[i].Size);
                else label.RemoveThemeFontSizeOverride(RichFontSizeNames[i]);
            }
        }
        _richFontSizes.Clear();
        // After the snapshots: the painting goes back into the layout and the game's own window rule places it.
        RestoreAncientLandscape();
    }

    public void PruneDead()
    {
        foreach (ulong id in _snapshots.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _snapshots.Remove(id);
        PruneMapInstances();
        _proceedButtons.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        _cardRewards.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        _mapFtues.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        _grids.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        foreach (ulong id in _laidOutRewardHolders.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList())
            _laidOutRewardHolders.Remove(id);
        _hitAreaTargets.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        _liftedBanners.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        _shops.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        PruneShopInstances();
        _hitPadHolders.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        _events.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        _restSites.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        PruneAncientInstances();
        foreach (ulong id in _labelMaxFontSizes.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _labelMaxFontSizes.Remove(id);
        foreach (ulong id in _labelMinFontSizes.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _labelMinFontSizes.Remove(id);
        foreach (ulong id in _labelFontSizes.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _labelFontSizes.Remove(id);
        foreach (ulong id in _richFontSizes.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _richFontSizes.Remove(id);
        PruneEventInstances();
    }

    // ---- Proceed button (rewards, shop, rest site, treasure) ------------------------------------------------------------

    private void UpdateProceedButtonPortrait(Control button, Vector2 canvas)
    {
        // ShowPos is a fraction of the canvas (1583/1920 across), which leaves the button partly off the narrow portrait edge.
        if (button.GetType().GetField("_animTween", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(button) is Tween { } tween &&
            tween.IsValid() && tween.IsRunning())
            return;
        // The ribbon is 108 units tall natively; its rectangle grows to the phone's smallest touch target, keeping its centre
        // (the game animates the position, not the size, so the slide is untouched).
        if (button.Size.Y < PortraitBands.MinTarget)
        {
            Capture(button, captureVisible: false);
            float grow = PortraitBands.MinTarget - button.Size.Y;
            button.Size = new Vector2(button.Size.X, PortraitBands.MinTarget);
            button.Position -= new Vector2(0f, grow / 2f);
        }
        float width = button.Size.X * button.Scale.X;
        if (button.Position.X >= canvas.X - 1f || button.Position.X + width <= canvas.X - 16f) return;
        button.Position = new Vector2(canvas.X - width - 24f, button.Position.Y);
    }

    private static void RestoreProceedButtonLandscape(Control button)
    {
        bool enabled = button.GetType().GetProperty("IsEnabled")?.GetValue(button) is true;
        PropertyInfo? pos = button.GetType().GetProperty(enabled ? "ShowPos" : "HidePos", BindingFlags.Instance | BindingFlags.NonPublic);
        if (pos?.GetValue(button) is Vector2 target)
            button.Position = target;
    }

    // ---- Spoils screen --------------------------------------------------------------------------------------------------

    private void LayoutRewardsPortrait(Control screen)
    {
        if (screen.GetNodeOrNull<Control>("Rewards") is not { } window) return;
        Capture(window, captureVisible: false);
        // The Loot panel is 526 wide with a 652 wide banner and rows of 402 x 86 units: at 1.5 the banner is 978 of the
        // canvas's 1080 and a row is 129 units (44 points) with 12 point text. The list is part of the panel, so as many
        // rows show as before and the rest scroll. The pivot is below the middle so the panel grows more up than down and
        // its list stays clear of the Proceed button.
        window.PivotOffset = new Vector2(window.Size.X / 2f, RewardsPivotY);
        window.Scale = new Vector2(RewardsScale, RewardsScale);
    }

    // ---- Card reward: 2 x 2 grid ----------------------------------------------------------------------------------------

    private void UpdateCardRewardPortrait(Control screen, Vector2 canvas, float capHeight)
    {
        Type type = screen.GetType();
        if (type.GetField("_completionSource", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(screen) is { } source &&
            source.GetType().GetProperty("Task")?.GetValue(source) is System.Threading.Tasks.Task { IsCompleted: true })
            return;
        if (screen.GetNodeOrNull<Control>("UI/CardRow") is not { } row) return;
        List<Control> holders = row.GetChildren().OfType<Control>().Where(h => !h.IsQueuedForDeletion()).ToList();
        if (holders.Count == 0) return;

        float centreY = canvas.Y / 2f;
        float lift = BannerLift(canvas, capHeight);
        LiftBanner(screen.GetNodeOrNull<Control>("UI/Banner"), canvas, lift, _liftedBanners);
        float bannerBottom = centreY - BannerBottomOffset - lift;
        // The alternatives are 44 points tall in portrait (PortraitOverlayLayout grows them).
        float altHeight = PortraitBands.MinTarget;
        float bottomLimit = canvas.Y - 120f - altHeight - 30f;
        int rows = holders.Count <= 2 ? 1 : (holders.Count + 1) / 2;
        const float gapX = 48f, gapY = 40f;
        float maxScaleByHeight = ((bottomLimit - (bannerBottom + 30f)) - (rows - 1) * gapY) / (rows * RewardCardHeight);
        float maxScaleByWidth = (canvas.X - 60f - gapX) / (2f * RewardCardWidth);
        float cardScale = Math.Min(RewardMaxCardScale, Math.Min(maxScaleByHeight, maxScaleByWidth));
        float holderScale = 0.8f;
        float rowScale = cardScale / holderScale;

        float blockHeight = rows * RewardCardHeight * cardScale + (rows - 1) * gapY;
        float blockCentreY = bannerBottom + 30f + blockHeight / 2f;

        Capture(row, captureVisible: false);
        row.Scale = new Vector2(rowScale, rowScale);
        row.OffsetTop = blockCentreY - centreY;
        row.OffsetBottom = row.OffsetTop;

        if (screen.GetNodeOrNull<Control>("UI/RewardAlternatives") is { } alts)
        {
            Capture(alts, captureVisible: false);
            float top = blockCentreY + blockHeight / 2f + 30f - centreY;
            alts.OffsetTop = top;
            alts.OffsetBottom = top + altHeight;
        }

        // A card keeps its slot: the game moves a hovered card to the end of the row so it draws on top, so the
        // row's child order is not the cards' order. Slots follow the game's options (the card each holder shows), and a holder
        // the options do not name keeps the place it was first seen in.
        ulong screenId = screen.GetInstanceId();
        if (!_laidOutRewardHolders.TryGetValue(screenId, out List<ulong>? order))
            _laidOutRewardHolders[screenId] = order = new List<ulong>();
        order.RemoveAll(id => !holders.Any(h => h.GetInstanceId() == id));
        bool fresh = false;
        foreach (Control holder in holders
                     .Select(h => (Holder: h, Option: PhoneCardRewardChoice.OptionIndexOf(screen, h)))
                     .OrderBy(p => p.Option < 0 ? int.MaxValue : p.Option)
                     .ThenBy(p => p.Holder.GetInstanceId())
                     .Select(p => p.Holder))
        {
            if (order.Contains(holder.GetInstanceId())) continue;
            order.Add(holder.GetInstanceId());
            fresh = true;
        }
        IReadOnlyList<GridSlot> slots = PortraitBetweenMath.RewardSlots(
            order.Count, RewardCardWidth * cardScale, RewardCardHeight * cardScale, gapX, gapY);
        Vector2 SlotOf(Control holder)
        {
            GridSlot slot = slots[order.IndexOf(holder.GetInstanceId())];
            return new Vector2(slot.CenterX / rowScale, slot.CenterY / rowScale);
        }

        if (fresh)
        {
            // New cards: replace the game's slide to its single row with a slide to the portrait grid, keeping its fade in.
            FieldInfo? tweenField = type.GetField("_cardTween", BindingFlags.Instance | BindingFlags.NonPublic);
            if (tweenField?.GetValue(screen) is Tween { } old && old.IsValid()) old.Kill();
            Tween tween = screen.CreateTween().SetParallel(true);
            foreach (Control holder in holders)
            {
                tween.TweenProperty(holder, "position", SlotOf(holder), 0.5).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Expo);
                tween.TweenProperty(holder, "modulate", Colors.White, 1.0).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Quart);
            }
            tweenField?.SetValue(screen, tween);
            return;
        }

        if (type.GetField("_cardTween", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(screen) is Tween { } running &&
            running.IsValid() && running.IsRunning())
            return;
        foreach (Control holder in holders)
        {
            Vector2 position = SlotOf(holder);
            if (holder.Position != position) holder.Position = position;
        }
    }

    private static void RestoreCardRewardLandscape(Control screen)
    {
        if (screen.GetNodeOrNull<Control>("UI/CardRow") is not { } row) return;
        List<Control> holders = row.GetChildren().OfType<Control>().Where(h => !h.IsQueuedForDeletion()).ToList();
        for (int i = 0; i < holders.Count; i++)
            holders[i].Position = new Vector2(-(holders.Count - 1) * CardRowSpacing / 2f + CardRowSpacing * i, 0f);
    }

    // ---- The shared banner -----------------------------------------------------------------------------------------------

    /// <summary>How far the shared banner rises in portrait: its top comes to just under the top bar and relic strip.</summary>
    internal static float BannerLift(Vector2 canvas, float capHeight) =>
        Math.Max(0f, canvas.Y / 2f - BannerTopOffset - (capHeight + PortraitBands.RunContentTop + BannerContentGap));

    /// <summary>
    /// Moves a screen's banner (NCommonBanner) up by <paramref name="lift"/>. The banner keeps where it shows and where it slides
    /// in from, measured from the middle of the screen, and goes back there when the window changes; so both are set here, and a
    /// slide that is under way is started again towards the new place.
    /// </summary>
    internal static void LiftBanner(Control? banner, Vector2 canvas, float lift, HashSet<ulong> lifted)
    {
        // Before its _Ready the banner has not measured where it stands (its image offset), and moving it then would be
        // measured as the game's own place (the choose a card banner ended off the screen).
        if (banner == null || !GodotObject.IsInstanceValid(banner) || !banner.IsNodeReady()) return;
        Type type = banner.GetType();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        if (type.GetField("_imgOffset", flags)?.GetValue(banner) is not Vector2 imgOffset ||
            type.GetField("_showPos", flags) is not { } showField || type.GetField("_hidePos", flags) is not { } hideField ||
            showField.GetValue(banner) is not Vector2 shown)
            return;
        Vector2 target = canvas / 2f - imgOffset - new Vector2(0f, lift);
        if (shown.DistanceSquaredTo(target) < 0.25f) return;
        showField.SetValue(banner, target);
        hideField.SetValue(banner, target + new Vector2(0f, 50f));
        lifted.Add(banner.GetInstanceId());
        if (type.GetField("_tween", flags)?.GetValue(banner) is Tween { } tween && tween.IsValid() && tween.IsRunning())
            banner.Call("AnimateIn");
        else
            banner.Position = target;
    }

    /// <summary>Puts lifted banners back where the game shows them (it works that out again from the window).</summary>
    internal static void RestoreBanners(HashSet<ulong> lifted)
    {
        foreach (ulong id in lifted)
            if (GodotObject.InstanceFromId(id) is Control banner && GodotObject.IsInstanceValid(banner) && banner.HasMethod("OnWindowChange"))
                banner.Call("OnWindowChange");
        lifted.Clear();
    }

    // ---- Card grids: deck, piles, card selection --------------------------------------------------------------------------

    private void LayoutCardGridPortrait(Control grid, float capHeight, float canvasHeight, float canvasWidth)
    {
        if (grid.GetType().FullName != CardGridTypeName) return;
        float top = capHeight + PortraitBands.TopBarHeight + 8f;
        PortraitControlSnapshot snap = Capture(grid, captureVisible: false);
        float parentTop = grid.GetParent() is Control parent ? parent.GlobalPosition.Y : 0f;
        float localTop = Math.Max(top - parentTop, snap.InitialOffsetTop);
        grid.AnchorLeft = 0f;
        grid.AnchorTop = 0f;
        grid.AnchorRight = 0f;
        grid.AnchorBottom = 0f;
        grid.PivotOffset = Vector2.Zero;
        grid.Scale = new Vector2(GridScale, GridScale);
        grid.OffsetLeft = 0f;
        grid.OffsetTop = localTop;
        grid.OffsetRight = canvasWidth / GridScale;
        grid.OffsetBottom = localTop + (canvasHeight - parentTop - localTop) / GridScale;

        // The deck view instances the grid with editable children, so the unique name does not resolve from the grid there.
        if (GridScroll(grid) is { } scroll)
        {
            Capture(scroll, captureVisible: false);
            scroll.OffsetLeft = GridScrollInsetLeft;
            scroll.OffsetRight = -GridScrollInsetRight;
            if (scroll.GetNodeOrNull<Control>("SortingOptions") is { } sorting)
                LayoutSortRowPortrait(sorting, canvasWidth / GridScale - GridScrollInsetLeft - GridScrollInsetRight);
        }
        if (grid.GetNodeOrNull<Control>("Scrollbar") is { } scrollbar)
        {
            Capture(scrollbar, captureVisible: false);
            scrollbar.OffsetLeft = GridScrollbarLeft;
            scrollbar.OffsetRight = GridScrollbarRight;
        }
    }

    /// <summary>
    /// The deck's four sort buttons (Obtained, Card Type, Cost, A - Z) are 250 x 42 units with 22 unit text, 50 apart: at the
    /// grid's portrait scale they would be 1150 units wide, so the game's row was shrunk to 0.66 (11.6 points tall). In portrait
    /// the row is the scroll area's width and 75 units tall (47 points at 1.8, its text 13.8 points), each button as wide as its
    /// own text and arrow need and the spare width shared out; if the texts do not fit, their font is made smaller to fit.
    /// </summary>
    private void LayoutSortRowPortrait(Control sorting, float width)
    {
        Capture(sorting, captureVisible: false);
        sorting.Scale = Vector2.One;
        sorting.PivotOffset = Vector2.Zero;
        sorting.OffsetLeft = 0f;
        sorting.OffsetRight = 0f;
        sorting.OffsetTop = SortRowTop;
        sorting.OffsetBottom = SortRowTop + SortRowHeight;
        if (sorting.GetNodeOrNull<Control>("HBoxContainer") is not { } row) return;
        Capture(row, captureVisible: false);
        row.OffsetTop = -SortRowHeight / 2f;
        row.OffsetBottom = SortRowHeight / 2f;
        row.AddThemeConstantOverride("separation", (int)SortRowGap);

        List<Control> buttons = row.GetChildren().OfType<Control>().Where(b => b.Visible).ToList();
        if (buttons.Count == 0) return;
        // A button is its text, 12 units, a 36 unit arrow and 8 units of inset (deck_view_sort_button.tscn), plus some padding.
        float Need(Control button, int font)
        {
            if (button.GetNodeOrNull<Label>("%Label") is not { } label) return 120f;
            Font? face = label.GetThemeFont("font");
            float text = face?.GetStringSize(label.Text, HorizontalAlignment.Left, -1, font).X ?? label.Text.Length * font * 0.55f;
            return text + 12f + 36f + 8f + SortButtonPad;
        }
        float room = width - SortRowGap * (buttons.Count - 1);
        int gameFont = buttons[0].GetNodeOrNull<Label>("%Label") is { } first ? first.GetThemeFontSize("font_size") : 22;
        if (_labelFontSizes.TryGetValue(buttons[0].GetNodeOrNull<Label>("%Label")?.GetInstanceId() ?? 0, out (bool Had, int Size) kept))
            gameFont = kept.Size;
        int fontSize = gameFont;
        while (fontSize > 14 && buttons.Sum(b => Need(b, fontSize)) > room) fontSize--;
        float spare = Math.Max(0f, room - buttons.Sum(b => Need(b, fontSize))) / buttons.Count;
        foreach (Control button in buttons)
        {
            Capture(button, captureVisible: false);
            button.CustomMinimumSize = new Vector2(MathF.Floor(Need(button, fontSize) + spare), SortRowHeight);
            if (button.GetNodeOrNull<Label>("%Label") is { } label && fontSize != gameFont) SetLabelFontSize(label, fontSize);
        }
        GD.Print($"Portrait between layout: deck sort row {buttons.Count} buttons in {width:F0} units, text {fontSize}.");
    }

    private static Control? GridScroll(Control grid) =>
        grid.GetNodeOrNull<Control>("%ScrollContainer") ?? grid.GetNodeOrNull<Control>("ScrollContainer");

    private static void KeepGridColumnsPortrait(Control grid)
    {
        // The grid sizes its scroll container itself after laying out cards, which can put back the old width. Columns follow
        // that width, so hold the portrait width and ask the grid to lay out again when it drifts.
        if (GridScroll(grid) is not { } scroll) return;
        float target = grid.Size.X - GridScrollInsetLeft - GridScrollInsetRight;
        if (Math.Abs(scroll.Size.X - target) < 1f) return;
        scroll.OffsetLeft = GridScrollInsetLeft;
        scroll.OffsetRight = -GridScrollInsetRight;
        scroll.Size = new Vector2(target, scroll.Size.Y);
        grid.GetType().GetField("_needsReinit", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(grid, true);
    }

    // ---- Map tutorial tip ------------------------------------------------------------------------------------------------

    private static void KeepMapFtueOnScreen(Control ftue, Vector2 canvas)
    {
        // The tip opens to the right of the room it points at; on the narrow canvas that runs off the edge. Slide only the panel.
        if (ftue.GetNodeOrNull<Control>("%Header") is not { } header || ftue.GetNodeOrNull<Control>("%Positioner") is not { } positioner)
            return;
        Control panel = header;
        while (panel.GetParent() is Control parent && parent != positioner) panel = parent;
        Rect2 rect = panel.GetGlobalRect();
        float overflow = rect.End.X - (canvas.X - 16f);
        if (overflow > 0.5f) panel.Position -= new Vector2(overflow, 0f);
        else if (rect.Position.X < 16f) panel.Position += new Vector2(16f - rect.Position.X, 0f);
    }

    /// <summary>Sets a label's font size in portrait, keeping the game's own to put back in landscape.</summary>
    private void SetLabelFontSize(Control label, int size)
    {
        ulong id = label.GetInstanceId();
        if (!_labelFontSizes.ContainsKey(id))
            _labelFontSizes[id] = (label.HasThemeFontSizeOverride("font_size"), label.GetThemeFontSize("font_size"));
        if (label.GetThemeFontSize("font_size") != size) label.AddThemeFontSizeOverride("font_size", size);
    }

    private int GameLabelFontSize(Control label) =>
        _labelFontSizes.TryGetValue(label.GetInstanceId(), out (bool Had, int Size) kept) ? kept.Size : label.GetThemeFontSize("font_size");

    // ---- Rest site -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The rest site's options are a row of 247 x 163 buttons, 100 apart, with a 28 unit (10 point) label under each, in
    /// the middle of an empty screen. The row is drawn as large as its buttons fit the width, up to a third larger (two
    /// options), and smaller when four or more would pass the screen's edges. The pivot is above the row's middle, so
    /// the row stays under the question above it and its labels stay above the description below.
    /// </summary>
    private void UpdateRestSitePortrait(Control room, Vector2 canvas)
    {
        if (room.GetNodeOrNull<Control>("%ChoicesContainer") is not { } choices) return;
        float width = choices.GetCombinedMinimumSize().X;
        float scale = Math.Clamp(
            PortraitBetweenMath.FitScale(width, canvas.X - 40f, RestChoicesMaxScale), RestChoicesMinScale, RestChoicesMaxScale);
        var pivot = new Vector2(choices.Size.X / 2f, RestChoicesPivotY);
        if (Mathf.IsEqualApprox(choices.Scale.X, scale) && choices.PivotOffset.IsEqualApprox(pivot)) return;
        Capture(choices, captureVisible: false);
        choices.PivotOffset = pivot;
        choices.Scale = new Vector2(scale, scale);
    }

    // ---- shared --------------------------------------------------------------------------------------------------------

    private PortraitControlSnapshot Capture(Control control, bool captureVisible)
    {
        ulong id = control.GetInstanceId();
        if (_snapshots.TryGetValue(id, out PortraitControlSnapshot? existing))
        {
            if (captureVisible && !existing.CaptureVisible) existing.RecordInitialVisibility(control.Visible);
            return existing;
        }
        var created = new PortraitControlSnapshot(control, captureVisible);
        _snapshots[id] = created;
        return created;
    }

    /// <summary>
    /// Gives a control a hit area of at least this size on the canvas, centred on it, without changing its picture (row
    /// 21h; PhoneHitArea). A container would lay the area out as one of its own children, so there it hangs from a child
    /// the container may size as it likes. Taken away again in landscape.
    /// </summary>
    private void EnsureHitPad(Control target, float canvasWidth, float canvasHeight)
    {
        if (!target.IsInsideTree()) return;
        Vector2 scale = target.GetGlobalTransformWithCanvas().Scale.Abs();
        if (scale.X < 0.01f || scale.Y < 0.01f) return;
        float growX = Math.Max(0f, canvasWidth / scale.X - target.Size.X) / 2f;
        float growY = Math.Max(0f, canvasHeight / scale.Y - target.Size.Y) / 2f;
        if (!PhoneHitArea.IsContainer(target))
        {
            PhoneHitArea.Grow(target, growX, growY, growX, growY);
            _hitAreaTargets.Add(target.GetInstanceId());
            return;
        }

        Control? holder = target.GetNodeOrNull<Control>(HitPadHolderName);
        if (holder == null)
        {
            holder = new Control { Name = HitPadHolderName, MouseFilter = Control.MouseFilterEnum.Ignore };
            var pad = new Control
            {
                Name = PhoneHitArea.NodeName,
                MouseFilter = Control.MouseFilterEnum.Pass,
                FocusMode = Control.FocusModeEnum.None,
            };
            pad.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            holder.AddChild(pad);
            target.AddChild(holder);
            _hitPadHolders.Add(holder.GetInstanceId());
        }
        if (holder.GetNodeOrNull<Control>(PhoneHitArea.NodeName) is { } area)
        {
            area.OffsetLeft = -growX;
            area.OffsetTop = -growY;
            area.OffsetRight = growX;
            area.OffsetBottom = growY;
        }
    }

    /// <summary>Sets every font size of a rich text label that does not size itself, keeping what it had for landscape.</summary>
    private void SetRichFontSize(Control label, int size)
    {
        ulong id = label.GetInstanceId();
        if (!_richFontSizes.ContainsKey(id))
            _richFontSizes[id] = RichFontSizeNames
                .Select(name => (label.HasThemeFontSizeOverride(name), label.HasThemeFontSizeOverride(name) ? label.GetThemeFontSize(name) : 0))
                .ToArray();
        foreach (string name in RichFontSizeNames)
            if (!label.HasThemeFontSizeOverride(name) || label.GetThemeFontSize(name) != size)
                label.AddThemeFontSizeOverride(name, size);
    }

    /// <summary>One line naming the visible between-fight screens and their rectangles, for the proof log.</summary>
    public static string DescribeForProof(Node root)
    {
        var parts = new List<string>();
        foreach (Node node in Descendants(root))
        {
            if (node is not Control control || !control.IsVisibleInTree()) continue;
            string? name = control.GetType().FullName;
            if (name == null || !name.StartsWith("MegaCrit.Sts2.Core.Nodes.", StringComparison.Ordinal)) continue;
            string shortName = control.GetType().Name;
            if (shortName is "NMapScreen" or "NRewardsScreen" or "NCardRewardSelectionScreen" or "NDeckViewScreen" or "NCardGrid"
                or "NEventRoom" or "NEventLayout" or "NAncientEventLayout" or "NMerchantRoom" or "NStandardMerchantRoom"
                or "NMerchantInventory" or "NRestSiteRoom" or "NTreasureRoom" or "NProceedButton")
            {
                Rect2 r = control.GetGlobalRect();
                parts.Add($"{shortName}[{r.Position.X:F0},{r.Position.Y:F0} {r.Size.X:F0}x{r.Size.Y:F0}]");
                if (shortName == "NMapScreen" && (control.GetNodeOrNull<Control>("TheMap") ?? control.FindChild("TheMap", true, false) as Control) is { } theMap &&
                    control.GetType().GetField("_bossPointNode", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(control) is Control boss)
                    parts.Add($"mapY={theMap.Position.Y:F0} boss=[{boss.GetGlobalRect().Position.Y:F0}..{boss.GetGlobalRect().End.Y:F0}]");
                if (shortName == "NMerchantInventory" && control.GetNodeOrNull<Control>("%SlotsContainer") is { } slotsNode)
                    parts.Add($"slots=[{slotsNode.GetGlobalRect().Position.X:F0},{slotsNode.GetGlobalRect().Position.Y:F0} scale={slotsNode.Scale.X:F2} pivot={slotsNode.PivotOffset.Y:F0} off={slotsNode.OffsetLeft:F0}]");
                if (shortName == "NCardGrid" && (control.GetNodeOrNull<Control>("%ScrollContainer") ?? control.GetNodeOrNull<Control>("ScrollContainer")) is { } scrollNode)
                    parts.Add($"scroll=[{scrollNode.Size.X:F0}x{scrollNode.Size.Y:F0} off={scrollNode.OffsetLeft:F0},{scrollNode.OffsetRight:F0} anchors={scrollNode.AnchorLeft},{scrollNode.AnchorRight} gridSize={control.Size.X:F0}]");
                if (shortName == "NCardGrid" && control.GetType().GetProperty("Columns", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(control) is int columns)
                    parts.Add($"columns={columns}");
            }
        }
        return "screens=" + string.Join(" ", parts.Distinct());
    }

    private static IEnumerable<Node> Descendants(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            yield return child;
            foreach (Node below in Descendants(child)) yield return below;
        }
    }
}
