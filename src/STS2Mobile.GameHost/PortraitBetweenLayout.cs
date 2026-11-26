using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using STS2Mobile.Platform;

namespace STS2Mobile.GameHost;

/// <summary>
/// Portrait layout for the between-fight screens: the Spire Map, spoils and card rewards, card grids (deck, piles and
/// card selection), events, the shop, the rest site and the treasure room. Every edit is captured first so turning Portrait Mode
/// off restores the desktop layout live.
/// </summary>
internal sealed partial class PortraitBetweenLayout
{
    public const string ProceedButtonTypeName = "MegaCrit.Sts2.Core.Nodes.CommonUi.NProceedButton";
    public const string RewardsScreenTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.NRewardsScreen";
    public const string CardRewardTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NCardRewardSelectionScreen";
    public const string CardGridTypeName = "MegaCrit.Sts2.Core.Nodes.Cards.NCardGrid";
    public const string MapFtueTypeName = "MegaCrit.Sts2.Core.Nodes.Ftue.NMapSelectFtue";
    public const string BackButtonTypeName = "MegaCrit.Sts2.Core.Nodes.CommonUi.NBackButton";

    public static readonly HashSet<string> WatchedTypeNames = new(StringComparer.Ordinal)
    {
        PortraitMapLayout.MapScreenTypeName, ProceedButtonTypeName, RewardsScreenTypeName, CardRewardTypeName, CardGridTypeName,
        PortraitShopLayout.MerchantInventoryTypeName, PortraitShopLayout.FakeMerchantInventoryTypeName, PortraitEventLayout.EventLayoutTypeName, MapFtueTypeName, PortraitEventLayout.AncientLayoutTypeName,
        BackButtonTypeName, PortraitEventLayout.EventOptionTypeName, PortraitRestSiteLayout.RestSiteRoomTypeName, TreasureRoomTypeName, PortraitEventLayout.AncientDialogueLineTypeName,
        PortraitShopLayout.MerchantRoomTypeName, PortraitShopLayout.BlitzMerchantRoomTypeName, PortraitShopLayout.FakeMerchantRoomTypeName,
        PortraitEventLayout.EventRoomTypeName, PortraitEventLayout.CombatEventLayoutTypeName,
    };

    // Card grids in portrait: three cards to a row, the screen's footer band kept at the bottom. The size arithmetic lives in
    // PortraitGridMath and the layout in PortraitBetweenLayout.Grid.cs.
    private const string UpgradeTickboxTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.InspectScreens.NUpgradePreviewTickbox";

    // Sizes for a finger. 127 units are 44 points and a font of 32 units is 11 points (PortraitBands).
    private const string HitPadHolderName = PhoneHitArea.HolderName;
    private static readonly string[] RichFontSizeNames =
        ["normal_font_size", "bold_font_size", "bold_italics_font_size", "italics_font_size", "mono_font_size"];
    private const float CardRowSpacing = 350f;
    private const float RewardCardWidth = 300f;
    private const float RewardCardHeight = 422f;
    private const float RewardMaxCardScale = 1.5f;
    private const float RewardCardGapX = 48f;
    private const float RewardCardGapY = 32f;
    private const float RewardCardSideMargin = 44f;
    // The banner sits above the cards; the alternatives (the card reward's Reroll and Skip) form one row under them, its right
    // edge a stone margin in from the canvas. Loot rows are 88 tall inside the window and their labels read at 15 points.
    private const float RewardBannerGap = 8f;
    private const float RewardCardClearance = 8f;
    private const float RewardAlternativeSeparation = 21f;
    private const float RewardBottomMargin = 24f;
    private const float RewardCardFloorScale = 289f / 300f;
    private const int RewardRowFontSize = 28;
    private const float GridScale = 1.8f;
    // The shared banner ("Choose a Card") is 162 units tall, 172 to 334 above the screen's middle; in portrait it rises to just
    // under the relic strip so the cards under it can grow.
    internal const float BannerTopOffset = 334f;
    internal const float BannerBottomOffset = 172f;
    internal const float BannerContentGap = 16f;


    private readonly Dictionary<ulong, PortraitControlSnapshot> _snapshots = new();
    private readonly HashSet<ulong> _proceedButtons = new();
    private readonly HashSet<ulong> _cardRewards = new();
    private readonly Dictionary<ulong, List<ulong>> _laidOutRewardHolders = new();
    private readonly HashSet<ulong> _mapFtues = new();
    private readonly HashSet<ulong> _hitAreaTargets = new();
    private readonly HashSet<ulong> _liftedBanners = new();
    private readonly PortraitShopLayout _shop = new();
    private readonly PortraitEventLayout _events = new();
    private readonly HashSet<ulong> _hitPadHolders = new();
    private readonly PortraitRestSiteLayout _rest = new();
    private readonly Dictionary<ulong, (bool Had, int Size)> _labelFontSizes = new();
    private readonly Dictionary<ulong, (bool Had, int Size)[]> _richFontSizes = new();

    // ---- Spoils, the action lane and the reward rows ---------------------------------------------------------------------

    /// <summary>One loot screen's window, the list's mask and the list, with the lane its action hangs from.</summary>
    private sealed class LootScreenState(Control screen, Control window, Control mask, Control list, Control? action, bool blitz)
    {
        public readonly Control Screen = screen;
        public readonly Control Window = window;
        public readonly Control Mask = mask;
        public readonly Control List = list;
        public readonly Control? Action = action;
        public readonly bool Blitz = blitz;
        public int RowCount = -1;
        public float ContentHeight;
        public bool Placed;
        public Tween? Fade;
    }

    /// <summary>One proceed/skip ribbon's owned slide, so its centre is the lane's after every enable.</summary>
    private sealed class ProceedLaneState
    {
        public bool Blitz;
        public bool Footer;
        public Tween? Owned;
    }

    private static Type? _proceedType;
    private static PropertyInfo? _proceedIsSkip;
    private static PropertyInfo? _proceedEnabled;
    private static FieldInfo? _proceedAnimTween;
    private static FieldInfo? _proceedImage;
    private static FieldInfo? _rewardsFadeTween;

    private static void ResolveProceedApi(Type type)
    {
        if (_proceedType == type) return;
        _proceedType = type;
        _proceedIsSkip = type.GetProperty("IsSkip", BindingFlags.Instance | BindingFlags.Public);
        _proceedEnabled = type.GetProperty("IsEnabled", BindingFlags.Instance | BindingFlags.Public);
        _proceedAnimTween = type.GetField("_animTween", BindingFlags.Instance | BindingFlags.NonPublic);
        _proceedImage = type.GetField("_buttonImage", BindingFlags.Instance | BindingFlags.NonPublic);
    }

    private static FieldInfo? RewardsFadeTween(Type screenType)
    {
        _rewardsFadeTween ??= screenType.GetField("_fadeTween", BindingFlags.Instance | BindingFlags.NonPublic);
        return _rewardsFadeTween;
    }

    private readonly Dictionary<ulong, LootScreenState> _lootScreens = new();
    private readonly Dictionary<ulong, ProceedLaneState> _proceedLanes = new();
    private readonly HashSet<ulong> _lootRows = new();
    private readonly HashSet<ulong> _lootSkipsHidden = new();
    private readonly Dictionary<ulong, (bool Had, bool Value)> _rewardAutoSizes = new();
    private readonly Dictionary<ulong, BoxContainer.AlignmentMode> _alternativeAlignments = new();

    private static MethodInfo? _runStateGetter;
    private static PropertyInfo? _gameModeProperty;
    private static object? _blitzMode;

    // ---- entry points from PortraitLayout -------------------------------------------------------------------------------

    private readonly HashSet<ulong> _loggedNodes = new();
    private readonly PortraitMapLayout _map = new();

    public void ApplyNode(Control control, string typeName, float capHeight, float canvasHeight, float canvasWidth)
    {
        if (_loggedNodes.Add(control.GetInstanceId()))
            GD.Print($"Portrait between layout: {control.GetType().Name} {control.GetPath()}");
        switch (typeName)
        {
            case PortraitMapLayout.MapScreenTypeName:
                _map.Apply(control, capHeight, canvasHeight, canvasWidth);
                break;
            case ProceedButtonTypeName:
                _proceedButtons.Add(control.GetInstanceId());
                EnsureHitPad(control, 0f, PortraitBands.MinTarget);
                break;
            case BackButtonTypeName:
                EnsureHitPad(control, 0f, PortraitBands.MinTarget);
                break;
            case PortraitEventLayout.EventOptionTypeName:
            case PortraitEventLayout.AncientDialogueLineTypeName:
            case PortraitEventLayout.AncientLayoutTypeName:
            case PortraitEventLayout.EventLayoutTypeName:
            case PortraitEventLayout.CombatEventLayoutTypeName:
            case PortraitEventLayout.EventRoomTypeName:
                _events.Apply(control, typeName, capHeight, canvasHeight, canvasWidth);
                break;
            case PortraitRestSiteLayout.RestSiteRoomTypeName:
                _rest.Apply(control, new Vector2(canvasWidth, canvasHeight));
                break;
            case TreasureRoomTypeName:
                UpdateTreasurePortrait(control, capHeight, new Vector2(canvasWidth, canvasHeight));
                break;
            case RewardsScreenTypeName:
                LayoutRewardsPortrait(control, capHeight, new Vector2(canvasWidth, canvasHeight));
                break;
            case CardRewardTypeName:
                _cardRewards.Add(control.GetInstanceId());
                break;
            case CardGridTypeName:
                LayoutCardGridPortrait(control, capHeight, canvasHeight, canvasWidth);
                break;
            case PortraitShopLayout.MerchantInventoryTypeName:
            case PortraitShopLayout.FakeMerchantInventoryTypeName:
            case PortraitShopLayout.MerchantRoomTypeName:
            case PortraitShopLayout.BlitzMerchantRoomTypeName:
            case PortraitShopLayout.FakeMerchantRoomTypeName:
                _shop.Apply(control, typeName, capHeight, canvasWidth, canvasHeight);
                break;
            case MapFtueTypeName:
                _mapFtues.Add(control.GetInstanceId());
                break;
        }
    }

    /// <summary>Applies layout needed before the native screen measures a new node.</summary>
    public bool LayoutOnAdd(Control control)
    {
        string? typeName = control.GetType().FullName;
        if (_events.LayoutOnAdd(control)) return true;
        if (typeName == TreasureRoomTypeName) return BeginTreasureRoom(control);
        // A View Upgrades tickbox joins the box this file keeps in the deck and selection screens' footers.
        if (typeName == UpgradeTickboxTypeName) return TrackViewUpgradesBox(control);
        return false;
    }

    public void OnProcessFrame(Window root, float capHeight)
    {
        Vector2 canvas = root.GetVisibleRect().Size;
        _map.OnProcessFrame(root, canvas);
        KeepLootSkipsClearOfCardReward(CardRewardShown());
        foreach (ulong id in _proceedButtons)
            if (GodotObject.InstanceFromId(id) is Control button && GodotObject.IsInstanceValid(button) && button.IsVisibleInTree())
                UpdateProceedButtonPortrait(button, canvas);
        foreach (LootScreenState state in _lootScreens.Values)
            if (GodotObject.IsInstanceValid(state.Screen) && state.Screen.IsVisibleInTree())
                UpdateLootPortrait(state, canvas, capHeight);
        foreach (ulong id in _cardRewards)
            if (GodotObject.InstanceFromId(id) is Control screen && GodotObject.IsInstanceValid(screen) && screen.IsInsideTree())
                UpdateCardRewardPortrait(screen, canvas, capHeight);
        foreach (ulong id in _gridStates.Keys)
            if (GodotObject.InstanceFromId(id) is Control grid && GodotObject.IsInstanceValid(grid) && grid.IsVisibleInTree())
                KeepGridPortrait(grid, capHeight, canvas);
        foreach ((ulong id, PortraitGridScreen screen) in _viewUpgrades)
            if (GodotObject.InstanceFromId(id) is Control box && GodotObject.IsInstanceValid(box) && box.IsVisibleInTree())
                UpdateViewUpgradesBox(box, screen, canvas);
        foreach (ulong id in _mapFtues)
            if (GodotObject.InstanceFromId(id) is Control ftue && GodotObject.IsInstanceValid(ftue) && ftue.IsVisibleInTree())
                KeepMapFtueOnScreen(ftue, canvas);
        _events.UpdateEvents(capHeight, canvas);
        _rest.Update(canvas);
        foreach (ulong id in _treasures)
            if (GodotObject.InstanceFromId(id) is Control room && GodotObject.IsInstanceValid(room) && room.IsVisibleInTree())
                UpdateTreasurePortrait(room, capHeight, canvas);
        _events.UpdateAncientPortraits(capHeight, canvas);
        _shop.OnProcessFrame(capHeight, canvas);
    }

    public void RestoreLandscape()
    {
        foreach (ulong id in _cardRewards)
            if (GodotObject.InstanceFromId(id) is Control screen && GodotObject.IsInstanceValid(screen))
                RestoreCardRewardLandscape(screen);
        _laidOutRewardHolders.Clear();
        _events.RestoreEventStructure();
        RestoreGridsLandscape();

        // The loot window's entrance and the ribbons' slides are ours while portrait; kill them before the snapshots put the
        // game's own layout back.
        foreach (LootScreenState loot in _lootScreens.Values)
            if (loot.Fade is { } fade && GodotObject.IsInstanceValid(fade)) fade.Kill();
        _lootScreens.Clear();
        _lootRows.Clear();
        foreach (ProceedLaneState lane in _proceedLanes.Values)
            if (lane.Owned is { } owned && GodotObject.IsInstanceValid(owned)) owned.Kill();
        _proceedLanes.Clear();
        foreach (ulong id in _lootSkipsHidden)
            if (GodotObject.InstanceFromId(id) is Control hidden && GodotObject.IsInstanceValid(hidden) && !hidden.Visible)
                hidden.Show();
        _lootSkipsHidden.Clear();

        foreach ((ulong id, PortraitControlSnapshot snapshot) in _snapshots)
            if (GodotObject.InstanceFromId(id) is Control control && GodotObject.IsInstanceValid(control))
                snapshot.Restore(control);
        _rest.RestoreControls();

        foreach ((ulong id, BoxContainer.AlignmentMode alignment) in _alternativeAlignments)
            if (GodotObject.InstanceFromId(id) is BoxContainer box && GodotObject.IsInstanceValid(box))
                box.Alignment = alignment;
        _alternativeAlignments.Clear();

        RestoreTreasureLandscape();
        _rest.RestoreArt();

        _map.RestoreLandscape();

        foreach (ulong id in _proceedButtons)
            if (GodotObject.InstanceFromId(id) is Control button && GodotObject.IsInstanceValid(button))
                RestoreProceedButtonLandscape(button);

        // The larger hit areas, the map points' size and the larger text go back to the game's own.
        foreach (ulong id in _hitAreaTargets)
            if (GodotObject.InstanceFromId(id) is Control target && GodotObject.IsInstanceValid(target))
                PhoneHitArea.Remove(target);
        _hitAreaTargets.Clear();
        RestoreBanners(_liftedBanners);
        _shop.RestoreLandscape();
        foreach (ulong id in _hitPadHolders)
        {
            if (GodotObject.InstanceFromId(id) is not Control holder || !GodotObject.IsInstanceValid(holder)) continue;
            holder.GetParent()?.RemoveChild(holder);
            holder.QueueFree();
        }
        _hitPadHolders.Clear();
        _rest.RestoreFonts();
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
        foreach ((ulong id, (bool Had, bool Value)) in _rewardAutoSizes)
            if (GodotObject.InstanceFromId(id) is Control label && GodotObject.IsInstanceValid(label))
                label.GetType().GetProperty("AutoSizeEnabled", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?.SetValue(label, Value);
        _rewardAutoSizes.Clear();
        _events.RestoreControls();
    }

    public void PruneDead()
    {
        foreach (ulong id in _snapshots.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _snapshots.Remove(id);
        _map.PruneDead();
        _proceedButtons.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        _cardRewards.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        _mapFtues.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        PruneGridInstances();
        foreach (ulong id in _laidOutRewardHolders.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList())
            _laidOutRewardHolders.Remove(id);
        _hitAreaTargets.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        _liftedBanners.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        _shop.PruneDead();
        _hitPadHolders.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        _events.PruneDead();
        _rest.PruneDead();
        foreach (ulong id in _labelFontSizes.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _labelFontSizes.Remove(id);
        foreach (ulong id in _richFontSizes.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _richFontSizes.Remove(id);
        _lootRows.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        _lootSkipsHidden.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        foreach (ulong id in _lootScreens.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _lootScreens.Remove(id);
        foreach (ulong id in _proceedLanes.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _proceedLanes.Remove(id);
        foreach (ulong id in _rewardAutoSizes.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _rewardAutoSizes.Remove(id);
        foreach (ulong id in _alternativeAlignments.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _alternativeAlignments.Remove(id);
    }

    // ---- Proceed button (rewards, shop, rest site, treasure) ------------------------------------------------------------

    /// <summary>
    /// The ribbon keeps one centre per screen. It is 108 units tall natively and the game slides it by its position, so growing
    /// it for a finger extends it downwards: the game's shown Y stays its top edge and the centre of a grown ribbon is the
    /// same before and after every Skip/Proceed re-enable. A Blitz run instead gives both labels one lane at 0.625 of the
    /// canvas height. Rest and treasure use the footer so the ribbon stays clear of room content. Their slide is taken
    /// over so it ends in that lane without a correction once it settles.
    /// </summary>
    private void UpdateProceedButtonPortrait(Control proceed, Vector2 canvas)
    {
        ResolveProceedApi(proceed.GetType());
        ulong id = proceed.GetInstanceId();
        if (!_proceedLanes.TryGetValue(id, out ProceedLaneState? lane))
        {
            string? parentType = proceed.GetParent()?.GetType().FullName;
            _proceedLanes[id] = lane = new ProceedLaneState
            {
                Blitz = parentType == PortraitShopLayout.BlitzMerchantRoomTypeName ||
                        (parentType == RewardsScreenTypeName && IsBlitzRun()),
                Footer = parentType is TreasureRoomTypeName or PortraitRestSiteLayout.RestSiteRoomTypeName,
            };
        }

        if (proceed.Size.Y < PortraitBands.MinTarget)
        {
            Capture(proceed, captureVisible: false);
            Vector2 topLeft = proceed.Position;
            proceed.Size = new Vector2(proceed.Size.X, PortraitBands.MinTarget);
            // Whatever direction the engine grows a control in, the ribbon's top stays where the game puts it.
            if (proceed.Position != topLeft) proceed.Position = topLeft;
        }

        bool enabled = _proceedEnabled?.GetValue(proceed) is true;
        float height = proceed.Size.Y;
        float centre = lane.Footer
            ? PortraitBetweenMath.ActionFooterCentreY(canvas.Y, height)
            : PortraitBetweenMath.ActionLaneCentreY(lane.Blitz, canvas.Y, height);
        float width = proceed.Size.X * proceed.Scale.X;
        var target = new Vector2(canvas.X - width - PortraitBetweenMath.ActionRightMargin, centre - height / 2f);

        Tween? native = _proceedAnimTween?.GetValue(proceed) as Tween;
        if (native != null && native.IsValid() && native.IsRunning() && lane.Owned != native)
        {
            if (!enabled)
            {
                // The game hides the ribbon towards its desktop lane; the Blitz and treasure lanes are elsewhere, so the
                // ribbon leaves from its own place instead of sliding back across the screen.
                if (!lane.Blitz && !lane.Footer) return;
                native.Kill();
                Tween leave = proceed.CreateTween();
                leave.TweenProperty(proceed, "position", new Vector2(canvas.X + width, target.Y), 0.8)
                    .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Expo);
                _proceedAnimTween?.SetValue(proceed, leave);
                lane.Owned = leave;
                return;
            }
            // The game slides the ribbon to its desktop place on enable; take the slide over so it ends in the phone's lane.
            native.Kill();
            Tween owned = proceed.CreateTween().SetParallel(true);
            owned.TweenProperty(proceed, "position", target, 0.5).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Expo);
            if (_proceedImage?.GetValue(proceed) is CanvasItem image)
                owned.TweenProperty(image, "self_modulate", Colors.White, 0.5);
            _proceedAnimTween?.SetValue(proceed, owned);
            lane.Owned = owned;
            return;
        }
        if (lane.Owned != null && lane.Owned.IsValid() && lane.Owned.IsRunning()) return;
        lane.Owned = null;
        if (!enabled) return;
        if (proceed.Position.DistanceSquaredTo(target) > 0.25f) proceed.Position = target;
    }

    private static void RestoreProceedButtonLandscape(Control button)
    {
        bool enabled = button.GetType().GetProperty("IsEnabled")?.GetValue(button) is true;
        PropertyInfo? pos = button.GetType().GetProperty(enabled ? "ShowPos" : "HidePos", BindingFlags.Instance | BindingFlags.NonPublic);
        if (pos?.GetValue(button) is Vector2 target)
            button.Position = target;
    }

    /// <summary>Whether the run is the Blitz run, whose loot and merchant share one action lane.</summary>
    private static bool IsBlitzRun()
    {
        try
        {
            Type? manager = typeof(NGame).Assembly.GetType("MegaCrit.Sts2.Core.Runs.RunManager");
            object? instance = manager?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            _runStateGetter ??= manager?.GetMethod("DebugOnlyGetState", BindingFlags.Public | BindingFlags.Instance);
            object? state = _runStateGetter?.Invoke(instance, null);
            if (state == null) return false;
            _gameModeProperty ??= state.GetType().GetProperty("GameMode", BindingFlags.Public | BindingFlags.Instance);
            object? mode = _gameModeProperty?.GetValue(state);
            if (mode == null) return false;
            _blitzMode ??= Enum.Parse(mode.GetType(), "Blitz");
            return mode.Equals(_blitzMode);
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ---- Spoils screen --------------------------------------------------------------------------------------------------

    /// <summary>
    /// The loot window is 526 x 640 with a banner that overhangs its top; in portrait it is scaled so the banner fits the
    /// phone's width (75 % of the canvas for the panel, 93 % for the banner) and its banner is kept under the stone header
    /// while its bottom stays above the screen's action, shrinking the window and re-cutting the list's mask when the tall
    /// canvas is too short for the whole panel. The list keeps scrolling: the game's own rule counts the rows' height.
    /// </summary>
    private void LayoutRewardsPortrait(Control screen, float capHeight, Vector2 canvas)
    {
        if (screen.GetNodeOrNull<Control>("Rewards") is not { } window) return;
        if (screen.GetNodeOrNull<Control>("%RewardContainerMask") is not { } mask) return;
        if (screen.GetNodeOrNull<Control>("%RewardsContainer") is not { } list) return;
        ulong id = screen.GetInstanceId();
        if (!_lootScreens.TryGetValue(id, out LootScreenState? state))
            _lootScreens[id] = state = new LootScreenState(
                screen, window, mask, list, screen.GetNodeOrNull<Control>("ProceedButton"), IsBlitzRun());
        Capture(window, captureVisible: false);
        Capture(mask, captureVisible: false);
        Capture(list, captureVisible: false);
        // Rows are two units taller and 14 apart (was 86 and 10), so a loot row is at least 44 points deep.
        if (!list.HasThemeConstantOverride("separation") || list.GetThemeConstant("separation") != (int)LootRowSeparation)
            list.AddThemeConstantOverride("separation", (int)LootRowSeparation);
        UpdateLootPortrait(state, canvas, capHeight);
    }

    private const float LootRowSeparation = 14f;

    private void UpdateLootPortrait(LootScreenState state, Vector2 canvas, float capHeight)
    {
        float laneTop = PortraitBetweenMath.ActionLaneTop(state.Blitz, canvas.Y, PortraitBands.MinTarget);
        LootWindowFit fit = PortraitBetweenMath.LootWindow(
            canvas.X, PortraitBands.HeaderBottom(capHeight), laneTop, NativeLootWidth, NativeLootHeight);
        Control window = state.Window;

        if (Math.Abs(window.Scale.X - fit.Scale) > 0.0005f)
        {
            window.PivotOffset = Vector2.Zero;
            window.Scale = new Vector2(fit.Scale, fit.Scale);
        }
        if (Math.Abs(window.Size.Y - fit.WindowHeight) > 0.25f)
            window.Size = new Vector2(window.Size.X, fit.WindowHeight);
        // The mask is re-cut from the window's edges, as the game's own 640-tall window no longer bounds the list.
        Control mask = state.Mask;
        if (mask.AnchorLeft != 0f || mask.AnchorTop != 0f || mask.AnchorRight != 1f || mask.AnchorBottom != 1f ||
            Math.Abs(mask.OffsetLeft - LootMaskLeft) > 0.01f || Math.Abs(mask.OffsetRight + LootMaskRight) > 0.01f ||
            Math.Abs(mask.OffsetTop - LootMaskTop) > 0.01f || Math.Abs(mask.OffsetBottom + LootMaskBottom) > 0.01f)
        {
            mask.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            mask.OffsetLeft = LootMaskLeft;
            mask.OffsetRight = -LootMaskRight;
            mask.OffsetTop = LootMaskTop;
            mask.OffsetBottom = -LootMaskBottom;
        }

        var target = new Vector2(fit.X, fit.Y);
        FieldInfo? fadeField = RewardsFadeTween(state.Screen.GetType());
        Tween? fadeTween = fadeField?.GetValue(state.Screen) as Tween;
        if (fadeTween != null && fadeTween.IsValid() && fadeTween.IsRunning() && state.Fade != fadeTween)
        {
            // The game's entrance slides the window to its desktop place and fades it in; take the slide over so it ends at
            // the portrait place (the window starts transparent, so nothing is seen at the desktop place). A loot screen
            // coming back from the card reward is already in its portrait place (only its modulate faded): the list must not
            // dip 100 units and slide back, because a row that moves under a finger drops its press (and the rows' guard
            // then holds touches for another third of a second). Its return is the fade alone.
            fadeTween.Kill();
            Tween fade = window.CreateTween().SetParallel(true);
            fade.TweenProperty(window, "modulate", Colors.White, 0.5);
            if (!state.Placed)
                fade.TweenProperty(window, "position:y", fit.Y, 0.5)
                    .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Back).From(fit.Y + 100f);
            window.Position = target;
            fadeField?.SetValue(state.Screen, fade);
            state.Fade = fade;
        }
        else if (state.Fade == null || !state.Fade.IsValid() || !state.Fade.IsRunning())
        {
            if (window.Position.DistanceSquaredTo(target) > 0.25f) window.Position = target;
            // The place is known after a fade of ours has finished; a layout that ran before the game's entrance still gets
            // its slide.
            if (state.Fade != null) state.Placed = true;
        }

        // The rows keep one size; their labels read at 15 points. The list is walked only when its rows change.
        int rowCount = state.List.GetChildCount();
        if (rowCount != state.RowCount) ApplyLootRows(state, rowCount);

        // The game's scroll engages when the list is 400 units tall; a list that overflows this shorter mask is made tall
        // enough for the drag to reach its last row (see LootScrollMinimumHeight).
        float maskHeight = mask.Size.Y;
        float minimum = PortraitBetweenMath.LootScrollMinimumHeight(state.ContentHeight, maskHeight);
        if (Math.Abs(state.List.CustomMinimumSize.Y - minimum) > 0.5f)
            state.List.CustomMinimumSize = new Vector2(state.List.CustomMinimumSize.X, minimum);
    }

    private void ApplyLootRows(LootScreenState state, int rowCount)
    {
        state.RowCount = rowCount;
        float content = 0f;
        int rows = 0;
        foreach (Node node in state.List.GetChildren())
        {
            if (node is not Control row || row.IsQueuedForDeletion()) continue;
            if (_lootRows.Add(row.GetInstanceId()))
            {
                Capture(row, captureVisible: false);
                if (row.CustomMinimumSize.Y < PortraitBetweenMath.LootRowMinHeight)
                    row.CustomMinimumSize = new Vector2(
                        row.CustomMinimumSize.X, PortraitBetweenMath.LootRowMinHeight);
                if (row.GetNodeOrNull<Control>("%Label") is { } label)
                {
                    SetRichFontSize(label, RewardRowFontSize);
                    PropertyInfo? autoSize = label.GetType().GetProperty(
                        "AutoSizeEnabled", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (autoSize?.GetValue(label) is bool autoSizing)
                    {
                        ulong labelId = label.GetInstanceId();
                        if (!_rewardAutoSizes.ContainsKey(labelId)) _rewardAutoSizes[labelId] = (true, autoSizing);
                        if (autoSizing) autoSize.SetValue(label, false);
                    }
                }
            }
            content += row.GetCombinedMinimumSize().Y;
            rows++;
        }
        if (rows > 1) content += (rows - 1) * state.List.GetThemeConstant("separation");
        state.ContentHeight = content;
    }

    private const float NativeLootWidth = 526f;
    private const float NativeLootHeight = 640f;
    private const float LootMaskLeft = 26f;
    private const float LootMaskRight = 26f;
    private const float LootMaskTop = 103f;
    private const float LootMaskBottom = 36f;

    /// <summary>Whether a card reward is open over the loot screen.</summary>
    private bool CardRewardShown()
    {
        foreach (ulong id in _cardRewards)
            if (GodotObject.InstanceFromId(id) is Control screen && GodotObject.IsInstanceValid(screen) &&
                screen.IsInsideTree() && screen.IsVisibleInTree())
                return true;
        return false;
    }

    /// <summary>
    /// The loot screen's own Skip stays under a card reward otherwise: the screen below only disables it (its slide takes most
    /// of a second) and the reward screen is transparent. It is hidden while the reward is open and shown again when the loot
    /// screen comes back, with its place kept.
    /// </summary>
    private void KeepLootSkipsClearOfCardReward(bool cardRewardOpen)
    {
        foreach (LootScreenState state in _lootScreens.Values)
        {
            if (state.Action is not { } action || !GodotObject.IsInstanceValid(action)) continue;
            ResolveProceedApi(action.GetType());
            ulong actionId = action.GetInstanceId();
            if (cardRewardOpen && state.Screen.IsVisibleInTree() && _proceedIsSkip?.GetValue(action) is true && action.Visible)
            {
                action.Hide();
                _lootSkipsHidden.Add(actionId);
            }
            else if (_lootSkipsHidden.Remove(actionId) && !action.Visible)
            {
                action.Show();
            }
        }
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
        // The banner sits above the cards; the alternatives (44 points tall, grown by PortraitOverlayLayout) keep one row
        // under them, right-aligned a stone margin in from the canvas, and the block takes what is left between.
        float altHeight = PortraitBands.MinTarget;
        float footerTop = canvas.Y - RewardBottomMargin - altHeight;
        float bandTop = bannerBottom + RewardBannerGap;
        float bandBottom = footerTop - RewardCardClearance;
        CardGridFit grid = holders.Count <= 3
            ? PortraitBetweenMath.CardRow(
                holders.Count, canvas.X, bandTop, bandBottom, RewardCardWidth, RewardCardHeight)
            : PortraitBetweenMath.CardGrid(
                holders.Count, canvas.X, bandTop, bandBottom, RewardCardWidth, RewardCardHeight,
                RewardCardGapX, RewardCardGapY, RewardMaxCardScale, RewardCardSideMargin, RewardCardFloorScale);
        float cardScale = grid.Scale;
        float holderScale = 0.8f;
        float rowScale = cardScale / holderScale;
        float focusGrow = PortraitBetweenMath.CardFocusGrowth * RewardCardHeight * cardScale;

        float blockHeight = grid.BlockHeight;
        // One row of cards sits under the banner like the reference's row; a taller widening grid fills the band.
        float blockTop = bandTop + focusGrow;
        float blockCentreY = blockTop + blockHeight / 2f;

        Capture(row, captureVisible: false);
        row.Scale = new Vector2(rowScale, rowScale);
        row.OffsetTop = blockCentreY - centreY;
        row.OffsetBottom = row.OffsetTop;

        if (screen.GetNodeOrNull<Control>("UI/RewardAlternatives") is { } alts)
        {
            // The game slides the row up as the screen opens; it is placed here instead, so that slide is taken over.
            if (type.GetField("_buttonTween", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(screen) is Tween { } slide &&
                slide.IsValid() && slide.IsRunning())
                slide.Kill();
            Capture(alts, captureVisible: false);
            float top = blockCentreY + blockHeight / 2f + focusGrow + RewardCardClearance - centreY;
            alts.OffsetTop = top;
            alts.OffsetBottom = top + altHeight;
            alts.OffsetLeft = 0f;
            alts.OffsetRight = -PortraitBetweenMath.ActionRightMargin;
            if (alts is BoxContainer box)
            {
                if (box.Alignment != BoxContainer.AlignmentMode.End)
                {
                    _alternativeAlignments.TryAdd(alts.GetInstanceId(), box.Alignment);
                    box.Alignment = BoxContainer.AlignmentMode.End;
                }
                if (!alts.HasThemeConstantOverride("separation") ||
                    alts.GetThemeConstant("separation") != (int)RewardAlternativeSeparation)
                    alts.AddThemeConstantOverride("separation", (int)RewardAlternativeSeparation);
            }
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
            order.Count, grid.Columns, RewardCardWidth * cardScale, RewardCardHeight * cardScale, grid.GapX, RewardCardGapY);
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
