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
    public const string MapFtueTypeName = "MegaCrit.Sts2.Core.Nodes.Ftue.NMapSelectFtue";
    public const string BackButtonTypeName = "MegaCrit.Sts2.Core.Nodes.CommonUi.NBackButton";

    public static readonly HashSet<string> WatchedTypeNames = new(StringComparer.Ordinal)
    {
        PortraitMapLayout.MapScreenTypeName, ProceedButtonTypeName, PortraitRewardsLayout.RewardsScreenTypeName,
        PortraitRewardsLayout.CardRewardTypeName, PortraitCardGridLayout.CardGridTypeName,
        PortraitShopLayout.MerchantInventoryTypeName, PortraitShopLayout.FakeMerchantInventoryTypeName, PortraitEventLayout.EventLayoutTypeName, MapFtueTypeName, PortraitEventLayout.AncientLayoutTypeName,
        BackButtonTypeName, PortraitEventLayout.EventOptionTypeName, PortraitRestSiteLayout.RestSiteRoomTypeName, PortraitTreasureLayout.TreasureRoomTypeName, PortraitEventLayout.AncientDialogueLineTypeName,
        PortraitShopLayout.MerchantRoomTypeName, PortraitShopLayout.BlitzMerchantRoomTypeName, PortraitShopLayout.FakeMerchantRoomTypeName,
        PortraitEventLayout.EventRoomTypeName, PortraitEventLayout.CombatEventLayoutTypeName,
    };

    // Sizes for a finger. 127 units are 44 points and a font of 32 units is 11 points (PortraitBands).
    private const string HitPadHolderName = PhoneHitArea.HolderName;
    // The shared banner ("Choose a Card") is 162 units tall, 172 to 334 above the screen's middle; in portrait it rises to just
    // under the relic strip so the cards under it can grow.
    internal const float BannerTopOffset = 334f;
    internal const float BannerBottomOffset = 172f;
    internal const float BannerContentGap = 16f;


    private readonly Dictionary<ulong, PortraitControlSnapshot> _snapshots = new();
    private readonly HashSet<ulong> _proceedButtons = new();
    private readonly HashSet<ulong> _mapFtues = new();
    private readonly HashSet<ulong> _hitAreaTargets = new();
    private readonly PortraitShopLayout _shop = new();
    private readonly PortraitEventLayout _events = new();
    private readonly HashSet<ulong> _hitPadHolders = new();
    private readonly PortraitRestSiteLayout _rest = new();
    private readonly PortraitTreasureLayout _treasure = new();
    private readonly PortraitRewardsLayout _rewards = new();
    private readonly PortraitCardGridLayout _grids = new();

    // ---- The action lane and the run mode --------------------------------------------------------------------------------

    /// <summary>One proceed/skip ribbon's owned slide, so its centre is the lane's after every enable.</summary>
    private sealed class ProceedLaneState
    {
        public bool Blitz;
        public bool Rest;
        public Tween? Owned;
    }

    private static Type? _proceedType;
    private static PropertyInfo? _proceedIsSkip;
    private static PropertyInfo? _proceedEnabled;
    private static FieldInfo? _proceedAnimTween;
    private static FieldInfo? _proceedImage;

    private static void ResolveProceedApi(Type type)
    {
        if (_proceedType == type) return;
        _proceedType = type;
        _proceedIsSkip = type.GetProperty("IsSkip", BindingFlags.Instance | BindingFlags.Public);
        _proceedEnabled = type.GetProperty("IsEnabled", BindingFlags.Instance | BindingFlags.Public);
        _proceedAnimTween = type.GetField("_animTween", BindingFlags.Instance | BindingFlags.NonPublic);
        _proceedImage = type.GetField("_buttonImage", BindingFlags.Instance | BindingFlags.NonPublic);
    }

    /// <summary>Whether a proceed ribbon is a Skip, so the rewards owner can clear it from an open card reward.</summary>
    internal static bool ProceedIsSkip(Control proceed)
    {
        ResolveProceedApi(proceed.GetType());
        return _proceedIsSkip?.GetValue(proceed) is true;
    }

    private readonly Dictionary<ulong, ProceedLaneState> _proceedLanes = new();

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
            case PortraitTreasureLayout.TreasureRoomTypeName:
                _treasure.Apply(control, capHeight, new Vector2(canvasWidth, canvasHeight));
                break;
            case PortraitRewardsLayout.RewardsScreenTypeName:
                _rewards.Apply(control, capHeight, new Vector2(canvasWidth, canvasHeight));
                break;
            case PortraitRewardsLayout.CardRewardTypeName:
                _rewards.ApplyCardReward(control);
                break;
            case PortraitCardGridLayout.CardGridTypeName:
                _grids.Apply(control, capHeight, canvasHeight, canvasWidth);
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
        if (typeName == PortraitTreasureLayout.TreasureRoomTypeName) return _treasure.Begin(control);
        // A View Upgrades tickbox joins the box the grid owner keeps in the deck and selection screens' footers.
        if (typeName == PortraitCardGridLayout.UpgradeTickboxTypeName) return _grids.TrackViewUpgradesBox(control);
        return false;
    }

    public void OnProcessFrame(Window root, float capHeight)
    {
        Vector2 canvas = root.GetVisibleRect().Size;
        _map.OnProcessFrame(root, canvas);
        _rewards.UpdateSkipVisibility();
        foreach (ulong id in _proceedButtons)
            if (GodotObject.InstanceFromId(id) is Control button && GodotObject.IsInstanceValid(button) && button.IsVisibleInTree())
                UpdateProceedButtonPortrait(button, canvas);
        _rewards.Update(canvas, capHeight);
        _grids.Update(capHeight, canvas);
        foreach (ulong id in _mapFtues)
            if (GodotObject.InstanceFromId(id) is Control ftue && GodotObject.IsInstanceValid(ftue) && ftue.IsVisibleInTree())
                KeepMapFtueOnScreen(ftue, canvas);
        _events.UpdateEvents(capHeight, canvas);
        _rest.Update(canvas);
        _treasure.Update(capHeight, canvas);
        _events.UpdateAncientPortraits(capHeight, canvas);
        _shop.OnProcessFrame(capHeight, canvas);
    }

    public void RestoreLandscape()
    {
        _rewards.RestoreCardRewardsLandscape();
        _events.RestoreEventStructure();
        _grids.RestoreGridsLandscape();

        // The loot window's entrance and the ribbons' slides are ours while portrait; kill them before the snapshots put the
        // game's own layout back.
        _rewards.RestoreLootLandscape();
        foreach (ProceedLaneState lane in _proceedLanes.Values)
            if (lane.Owned is { } owned && GodotObject.IsInstanceValid(owned)) owned.Kill();
        _proceedLanes.Clear();
        _rewards.RestoreHiddenSkips();

        _rewards.RestoreControls();
        foreach ((ulong id, PortraitControlSnapshot snapshot) in _snapshots)
            if (GodotObject.InstanceFromId(id) is Control control && GodotObject.IsInstanceValid(control))
                snapshot.Restore(control);
        _grids.RestoreControls();
        _rest.RestoreControls();

        _rewards.RestoreAlignments();

        _treasure.RestoreLandscape();
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
        _rewards.RestoreBanners();
        _shop.RestoreLandscape();
        foreach (ulong id in _hitPadHolders)
        {
            if (GodotObject.InstanceFromId(id) is not Control holder || !GodotObject.IsInstanceValid(holder)) continue;
            holder.GetParent()?.RemoveChild(holder);
            holder.QueueFree();
        }
        _hitPadHolders.Clear();
        _rest.RestoreFonts();
        _grids.RestoreFonts();
        _rewards.RestoreFonts();
        _events.RestoreControls();
    }

    public void PruneDead()
    {
        foreach (ulong id in _snapshots.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _snapshots.Remove(id);
        _map.PruneDead();
        _proceedButtons.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        _mapFtues.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        _grids.PruneDead();
        _rewards.PruneDead();
        _hitAreaTargets.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        _shop.PruneDead();
        _hitPadHolders.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        _events.PruneDead();
        _rest.PruneDead();
        _treasure.PruneDead();
        foreach (ulong id in _proceedLanes.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _proceedLanes.Remove(id);
    }

    // ---- Proceed button (rewards, shop, rest site, treasure) ------------------------------------------------------------

    /// <summary>
    /// The ribbon keeps one centre per screen. It is 108 units tall natively and the game slides it by its position, so growing
    /// it for a finger extends it downwards: the game's shown Y stays its top edge and the centre of a grown ribbon is the
    /// same before and after every Skip/Proceed re-enable. A Blitz run instead gives both labels one lane at 0.625 of the
    /// canvas height. The treasure room shares the loot screen's lane; the rest site has its own, lower lane
    /// (<see cref="PortraitRestMath.ProceedLaneTop"/>) under its options. Their slide is taken over so it ends in that lane
    /// without a correction once it settles.
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
                        (parentType == PortraitRewardsLayout.RewardsScreenTypeName && IsBlitzRun()),
                Rest = parentType == PortraitRestSiteLayout.RestSiteRoomTypeName,
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
        float centre = lane.Rest
            ? PortraitRestMath.ProceedLaneTop(canvas.Y) + height / 2f
            : PortraitBetweenMath.ActionLaneCentreY(lane.Blitz, canvas.X, canvas.Y, height);
        float width = proceed.Size.X * proceed.Scale.X;
        var target = new Vector2(canvas.X - width - PortraitBetweenMath.ActionRightMargin, centre - height / 2f);

        Tween? native = _proceedAnimTween?.GetValue(proceed) as Tween;
        if (native != null && native.IsValid() && native.IsRunning() && lane.Owned != native)
        {
            if (!enabled)
            {
                // The game hides the ribbon towards its desktop lane; the Blitz and rest lanes are elsewhere, so the ribbon
                // leaves from its own place instead of sliding back across the screen.
                if (!lane.Blitz && !lane.Rest) return;
                native.Kill();
                Tween leave = proceed.CreateTween();
                leave.TweenProperty(proceed, "position", new Vector2(canvas.X + width, target.Y), 0.8)
                    .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Expo);
                _proceedAnimTween?.SetValue(proceed, leave);
                lane.Owned = leave;
                return;
            }
            // The game slides the ribbon to its desktop place on enable; take the slide over so it ends in the phone's lane.
            // A ribbon coming in from the game's hidden place (only just past the right edge, at its desktop height) enters
            // from beyond the right edge on the lane's own line, as the game's horizontal slide does, not diagonally. One
            // already in its lane (a Skip/Proceed re-enable) stays where it is.
            native.Kill();
            if (proceed.Position.X > target.X + 1f && Math.Abs(proceed.Position.Y - target.Y) > 1f)
                proceed.Position = new Vector2(Math.Max(proceed.Position.X, canvas.X + width), target.Y);
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
    internal static bool IsBlitzRun()
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

    internal static IEnumerable<Node> Descendants(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            yield return child;
            foreach (Node below in Descendants(child)) yield return below;
        }
    }
}
