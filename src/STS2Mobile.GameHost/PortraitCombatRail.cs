using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using STS2Mobile.Platform;
using CombatReflection = STS2Mobile.GameHost.PortraitCombatLayout.CombatReflection;

namespace STS2Mobile.GameHost;

/// <summary>Positions the combat action rail and owns its entrance, input filtering, and desktop restore state.</summary>
internal sealed class PortraitCombatRail
{
    // Touch targets: the End Turn button (220 by 90 in the scene) and the pile buttons (80 by 80) are drawn at the
    // scales in PortraitHudLayout, large enough for the 44-point side a finger needs (PortraitBands.MinTarget).
    private const float HudEdgeMargin = 16f;

    private readonly HashSet<ulong> _wiredCombatUis = new();
    private readonly HashSet<ulong> _railEntranceRooms = new();
    private readonly HashSet<ulong> _railVisiblePiles = new();
    private readonly Dictionary<ulong, Vector2> _starNativeSizes = new();

    private readonly Dictionary<ulong, Control.MouseFilterEnum> _endTurnFilters = new();

    private readonly Func<Control, CombatReflection> _getReflectionAndCaptureRoom;

    public PortraitCombatRail(Func<Control, CombatReflection> getReflectionAndCaptureRoom) =>
        _getReflectionAndCaptureRoom = getReflectionAndCaptureRoom;

    public void Apply(Control ui, Control room, Control? hand, CombatReflection refl, PortraitHudLayout hud)
    {
        LayoutCombatHudPortrait(ui, room, refl, hud);
        // Settle the piles before NCombatUi.AnimIn can start a desktop slide.
        if (_railEntranceRooms.Add(room.GetInstanceId()))
            ForcePileEntrance(ui, refl, hud);
        KeepEndTurnUnlitWhileHolding(ui, hand, refl);
    }

    public void OnFramePreDraw(Control ui, Control room, CombatReflection refl, PortraitHudLayout hud)
    {
        CorrectRailTweenPortrait(ui, room, refl, hud);
        ForceNewlyVisiblePiles(ui, refl, hud);
    }

    public void EndSession()
    {
        // A later portrait session in the same room must force the piles' entrance again.
        _railEntranceRooms.Clear();
        _railVisiblePiles.Clear();
    }

    public void PruneDead(List<ulong> dead)
    {
        dead.Clear();
        foreach (ulong id in _endTurnFilters.Keys)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _endTurnFilters.Remove(id);

        dead.Clear();
        foreach (ulong id in _wiredCombatUis)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _wiredCombatUis.Remove(id);

        dead.Clear();
        foreach (ulong id in _railEntranceRooms)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _railEntranceRooms.Remove(id);

        dead.Clear();
        foreach (ulong id in _railVisiblePiles)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _railVisiblePiles.Remove(id);

        dead.Clear();
        foreach (ulong id in _starNativeSizes.Keys)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _starNativeSizes.Remove(id);
    }

    /// <summary>
    /// The rail places a native show/hide tween can move after the full pass: the energy counter's own slide, the End Turn
    /// button, the three piles, and the star, which hangs off the counter's live rect. Nothing here runs on a frame where no
    /// tween moved: each place is only written when it differs.
    /// </summary>
    private void CorrectRailTweenPortrait(Control ui, Control room, CombatReflection refl, PortraitHudLayout hud)
    {
        Control? energyCounter = refl.EnergyCounterField?.GetValue(ui) as Control;
        if (energyCounter != null && GodotObject.IsInstanceValid(energyCounter))
        {
            if (energyCounter.Position != Vector2.Zero)
                energyCounter.Position = Vector2.Zero;
            if (refl.StarCounterField?.GetValue(ui) is Control starCounter && starCounter.IsVisibleInTree())
                PlaceStarCounter(starCounter, energyCounter);
        }

        if ((ui.GetNodeOrNull<Control>("%EndTurnButton") ?? ui.GetNodeOrNull<Control>("EndTurnButton")) is { } endTurn &&
            GodotObject.IsInstanceValid(endTurn))
        {
            if (refl.EndTurnPosTweenField?.GetValue(endTurn) is Tween posTween)
            {
                if (posTween.IsRunning()) posTween.Kill();
                refl.EndTurnPosTweenField.SetValue(endTurn, null);
            }
            Vector2 target = EndTurnRailPosition(endTurn, refl, hud);
            if (endTurn.Position != target)
                endTurn.Position = target;
        }

        if ((ui.GetNodeOrNull<Control>("%PingButton") ?? ui.GetNodeOrNull<Control>("PingButton")) is { } pingBtn &&
            GodotObject.IsInstanceValid(pingBtn) && pingBtn.Visible)
        {
            int pingState = Convert.ToInt32(refl.PingStateField?.GetValue(pingBtn) ?? 2);
            if (refl.PingPosTweenField?.GetValue(pingBtn) is Tween pingTween)
            {
                if (pingTween.IsRunning()) pingTween.Kill();
                refl.PingPosTweenField.SetValue(pingBtn, null);
            }
            Vector2 target = new(pingState == 2 ? 1140f : hud.PingShowX, hud.PingShowY);
            if (pingBtn.Position != target)
                pingBtn.Position = target;
        }

        if ((room.GetNodeOrNull<Control>("%ProceedButton") ?? room.GetNodeOrNull<Control>("ProceedButton")) is { } proceedBtn &&
            GodotObject.IsInstanceValid(proceedBtn))
        {
            bool enabled = (bool)(refl.ButtonIsEnabledProp?.GetValue(proceedBtn) ?? false);
            if (refl.ProceedAnimTweenField?.GetValue(proceedBtn) is Tween procTween)
            {
                if (procTween.IsRunning()) procTween.Kill();
                refl.ProceedAnimTweenField.SetValue(proceedBtn, null);
            }
            float procW = Math.Max(200f, proceedBtn.Size.X);
            Vector2 target = new(enabled ? hud.EndTurnShowX + 110f - procW * 0.5f : 1140f, hud.EndTurnShowY);
            if (proceedBtn.Position != target)
                proceedBtn.Position = target;
        }

        if ((ui.GetNodeOrNull<Control>("%CombatPileContainer") ?? ui.GetNodeOrNull<Control>("CombatPileContainer")) is { } pileContainer &&
            GodotObject.IsInstanceValid(pileContainer))
        {
            CorrectPile(pileContainer, refl, "%DrawPile", "DrawPile",
                new Vector2(hud.DrawShowX, hud.DrawShowY), new Vector2(hud.DrawHideX, hud.DrawHideY), hud.PileScale);
            CorrectPile(pileContainer, refl, "%DiscardPile", "DiscardPile",
                new Vector2(hud.DiscardShowX, hud.DiscardShowY), new Vector2(hud.DiscardHideX, hud.DiscardHideY), hud.PileScale);
            CorrectPile(pileContainer, refl, "%ExhaustPile", "ExhaustPile",
                new Vector2(hud.ExhaustShowX, hud.ExhaustShowY), new Vector2(hud.ExhaustHideX, hud.ExhaustShowY), hud.PileScale);
        }
    }

    private static void CorrectPile(Control pileContainer, CombatReflection refl, string unique, string name,
        Vector2 showPos, Vector2 hidePos, float scale)
    {
        if ((pileContainer.GetNodeOrNull<Control>(unique) ?? pileContainer.GetNodeOrNull<Control>(name)) is not { } pile ||
            !GodotObject.IsInstanceValid(pile) || !pile.IsVisibleInTree())
            return;
        LayoutPileButton(pile, refl, showPos, hidePos, scale);
    }

    /// <summary>
    /// The End Turn button's place on the rail: its own x while the fight runs, the hidden x once it is over (the Proceed
    /// button takes its place), clamped so the scaled button stays on the canvas.
    /// </summary>
    private static Vector2 EndTurnRailPosition(Control endTurn, CombatReflection refl, PortraitHudLayout hud)
    {
        float showX = Math.Min(hud.EndTurnShowX, SafeAreaCanvas.PortraitWidth - HudEdgeMargin - 110f * (1f + hud.EndTurnScale));
        int btnState = Convert.ToInt32(refl.EndTurnStateField?.GetValue(endTurn) ?? 2);
        return new Vector2(btnState == 2 && refl.IsCombatOver() ? hud.EndTurnHideX : showX, hud.EndTurnShowY);
    }

    /// <summary>
    /// The star's place from the energy counter's live rect: native size and TopLeft anchors, hung on the orb's
    /// lower-left corner. The native size is captured once per star node, before any of our writes. The target is computed
    /// first and each property is only written when it differs, so a settled star costs a rect read and no layout
    /// invalidation (the node is the counter's last child, so it already draws in front of the orb and the "3/3").
    /// </summary>
    private void PlaceStarCounter(Control starCounter, Control energyCounter)
    {
        ulong starId = starCounter.GetInstanceId();
        if (!_starNativeSizes.TryGetValue(starId, out Vector2 nativeSize))
            _starNativeSizes[starId] = nativeSize = starCounter.Size;
        Rect2 orb = energyCounter.GetGlobalRect();
        Vector2 span = nativeSize * starCounter.GetGlobalTransform().Scale.Abs();
        (float centerX, float centerY) = PortraitCombatMath.StarBadgeCenter(
            orb.GetCenter().X, orb.GetCenter().Y, orb.Size.X, orb.Size.Y);
        var target = new Vector2(centerX, centerY) - span * 0.5f;

        if (starCounter.AnchorLeft != 0f || starCounter.AnchorTop != 0f ||
            starCounter.AnchorRight != 0f || starCounter.AnchorBottom != 0f)
            starCounter.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopLeft);
        if (starCounter.Size != nativeSize)
            starCounter.Size = nativeSize;
        if (starCounter.GlobalPosition != target)
            starCounter.GlobalPosition = target;
    }

    /// <summary>
    /// A pile that first becomes visible after combat start (normally exhaust) gets the same entrance treatment as the
    /// initial draw/discard piles: no native desktop tween, placed on its rail show position from its first drawn frame.
    /// Later AnimOut tweens are left alone once the pile has been seen.
    /// </summary>
    private void ForceNewlyVisiblePiles(Control ui, CombatReflection refl, PortraitHudLayout hud)
    {
        if ((ui.GetNodeOrNull<Control>("%CombatPileContainer") ?? ui.GetNodeOrNull<Control>("CombatPileContainer")) is not { } pileContainer)
            return;
        Check(pileContainer.GetNodeOrNull<Control>("%DrawPile") ?? pileContainer.GetNodeOrNull<Control>("DrawPile"),
            new Vector2(hud.DrawShowX, hud.DrawShowY));
        Check(pileContainer.GetNodeOrNull<Control>("%DiscardPile") ?? pileContainer.GetNodeOrNull<Control>("DiscardPile"),
            new Vector2(hud.DiscardShowX, hud.DiscardShowY));
        Check(pileContainer.GetNodeOrNull<Control>("%ExhaustPile") ?? pileContainer.GetNodeOrNull<Control>("ExhaustPile"),
            new Vector2(hud.ExhaustShowX, hud.ExhaustShowY));

        void Check(Control? pile, Vector2 show)
        {
            if (pile == null)
                return;
            ulong id = pile.GetInstanceId();
            bool visible = pile.IsVisibleInTree();
            if (!visible)
            {
                _railVisiblePiles.Remove(id);
                return;
            }
            if (!_railVisiblePiles.Add(id))
                return;
            if (refl.PilePosTweenField?.GetValue(pile) is Tween tween)
            {
                if (tween.IsRunning()) tween.Kill();
                refl.PilePosTweenField.SetValue(pile, null);
            }
            refl.PileShowPosField?.SetValue(pile, show);
            refl.PileHidePosField?.SetValue(pile, show);
            pile.Position = show;
        }
    }

    /// <summary>
    /// NCombatUi.Activate sets Visible=true and immediately calls AnimIn; the piles' AnimIn starts at their native
    /// desktop show/hide targets. VisibilityChanged fires between those two lines, so this handler puts the whole rail on
    /// its portrait places before the entrance tween is created and makes the piles' first entrance target the rail.
    /// </summary>
    public void WireCombatUi(Control ui, ulong roomId)
    {
        if (!_wiredCombatUis.Add(ui.GetInstanceId()))
            return;
        if (ui.HasSignal("visibility_changed"))
            ui.Connect("visibility_changed", Callable.From(() => OnCombatUiVisibilityChanged(roomId)));
    }

    private void OnCombatUiVisibilityChanged(ulong roomId)
    {
        if (!SafeAreaCanvas.IsPortrait || GodotObject.InstanceFromId(roomId) is not Control room ||
            !GodotObject.IsInstanceValid(room) || !room.IsInsideTree())
            return;
        Control? ui = room.GetNodeOrNull<Control>("%CombatUi") ?? room.GetNodeOrNull<Control>("CombatUi");
        if (ui == null || !ui.Visible || !ui.IsVisibleInTree())
            return;

        CombatReflection refl = _getReflectionAndCaptureRoom(room);
        float canvasHeight = Math.Max(room.GetViewport().GetVisibleRect().Size.Y, SafeAreaCanvas.MinPortraitHeight);
        PortraitCombatBands bands = PortraitCombatMath.ComputeCombatBands(canvasHeight, stageTopY: 0f);
        PortraitHudLayout hud = PortraitCombatMath.ComputeHudLayout(canvasHeight, bands);
        LayoutCombatHudPortrait(ui, room, refl, hud);
        _railEntranceRooms.Add(roomId);
        ForcePileEntrance(ui, refl, hud);
    }

    /// <summary>
    /// Stops the native pile entrance from using its desktop corner targets: kill the running position tween, point both
    /// show and hide at the rail show position (so AnimIn starts and ends there), and place the pile on the rail now.
    /// </summary>
    private static void ForcePileEntrance(Control ui, CombatReflection refl, PortraitHudLayout hud)
    {
        if ((ui.GetNodeOrNull<Control>("%CombatPileContainer") ?? ui.GetNodeOrNull<Control>("CombatPileContainer")) is not { } pileContainer)
            return;

        void Force(Control? pile, Vector2 show)
        {
            if (pile == null)
                return;
            if (refl.PilePosTweenField?.GetValue(pile) is Tween tween)
            {
                if (tween.IsRunning())
                    tween.Kill();
                refl.PilePosTweenField.SetValue(pile, null);
            }
            refl.PileShowPosField?.SetValue(pile, show);
            refl.PileHidePosField?.SetValue(pile, show);
            pile.Position = show;
        }

        Force(pileContainer.GetNodeOrNull<Control>("%DrawPile") ?? pileContainer.GetNodeOrNull<Control>("DrawPile"),
            new Vector2(hud.DrawShowX, hud.DrawShowY));
        Force(pileContainer.GetNodeOrNull<Control>("%DiscardPile") ?? pileContainer.GetNodeOrNull<Control>("DiscardPile"),
            new Vector2(hud.DiscardShowX, hud.DiscardShowY));
        Force(pileContainer.GetNodeOrNull<Control>("%ExhaustPile") ?? pileContainer.GetNodeOrNull<Control>("ExhaustPile"),
            new Vector2(hud.ExhaustShowX, hud.ExhaustShowY));
    }

    /// <summary>
    /// In portrait End Turn sits in the hand's band, so a card carried toward an enemy passes
    /// over it, and the button lit up (its hover: brighter, its label red, the playable cards flashed) as if letting go there
    /// would end the turn, which it never does while a card is held. While a card is held the button takes no pointer, so it
    /// cannot light; its filter is put back as soon as the card is played or put back, and when portrait is left.
    /// </summary>
    private void KeepEndTurnUnlitWhileHolding(Control ui, Control? hand, CombatReflection refl)
    {
        if ((ui.GetNodeOrNull<Control>("%EndTurnButton") ?? ui.GetNodeOrNull<Control>("EndTurnButton")) is not { } endTurn)
            return;
        bool holding = hand != null && (bool)(refl.HandInCardPlayProp?.GetValue(hand) ?? false);
        ulong id = endTurn.GetInstanceId();
        if (holding)
        {
            if (endTurn.MouseFilter != Control.MouseFilterEnum.Ignore)
            {
                _endTurnFilters[id] = endTurn.MouseFilter;
                endTurn.MouseFilter = Control.MouseFilterEnum.Ignore;
            }
        }
        else if (_endTurnFilters.Remove(id, out Control.MouseFilterEnum filter))
        {
            endTurn.MouseFilter = filter;
        }
    }

    public void RestoreEndTurnFilters()
    {
        foreach ((ulong id, Control.MouseFilterEnum filter) in _endTurnFilters)
        {
            if (GodotObject.InstanceFromId(id) is Control endTurn && GodotObject.IsInstanceValid(endTurn))
                endTurn.MouseFilter = filter;
        }
        _endTurnFilters.Clear();
    }

    private void LayoutCombatHudPortrait(
        Control ui,
        Control room,
        CombatReflection refl,
        PortraitHudLayout hud)
    {
        if ((ui.GetNodeOrNull<Control>("%EnergyCounterContainer") ?? ui.GetNodeOrNull<Control>("EnergyCounterContainer")) is { } energyContainer)
        {
            energyContainer.AnchorLeft = 0f;
            energyContainer.AnchorRight = 0f;
            energyContainer.AnchorTop = 0f;
            energyContainer.AnchorBottom = 0f;
            energyContainer.Position = new Vector2(hud.EnergyContainerX, hud.EnergyContainerY);

            if (refl.EnergyCounterField?.GetValue(ui) is Control energyCounter)
            {
                energyCounter.PivotOffset = new Vector2(64f, 64f);
                energyCounter.Scale = new Vector2(hud.EnergyScale, hud.EnergyScale);
                // NEnergyCounter.AnimIn slides its own Position from (-480,128) to zero after Activate; the container is
                // already on the rail, so keep the counter itself at the origin for every drawn frame.
                energyCounter.Position = Vector2.Zero;
            }
        }

        if (refl.StarCounterField?.GetValue(ui) is Control starCounter && starCounter.IsVisibleInTree() &&
            refl.EnergyCounterField?.GetValue(ui) is Control starOrbCounter)
            PlaceStarCounter(starCounter, starOrbCounter);

        if (refl.FoolCombatUiField?.GetValue(ui) is Control foolUi && foolUi.IsVisibleInTree())
        {
            foolUi.AnchorLeft = 0f;
            foolUi.AnchorRight = 0f;
            foolUi.AnchorTop = 0f;
            foolUi.AnchorBottom = 0f;
            foolUi.Scale = new Vector2(0.72f, 0.72f);
            foolUi.Position = new Vector2(14f, hud.RailTopY - 190f);
        }

        // End Turn button: kill the desktop ShowPos/HidePos tween and keep the button in its rail place. It grows about
        // its centre (44 points tall), so the target is clamped to keep its right edge on the canvas.
        if ((ui.GetNodeOrNull<Control>("%EndTurnButton") ?? ui.GetNodeOrNull<Control>("EndTurnButton")) is { } endTurn)
        {
            endTurn.PivotOffset = new Vector2(110f, 45f);
            endTurn.Scale = new Vector2(hud.EndTurnScale, hud.EndTurnScale);

            if (refl.EndTurnPosTweenField?.GetValue(endTurn) is Tween posTween)
            {
                if (posTween.IsRunning()) posTween.Kill();
                refl.EndTurnPosTweenField.SetValue(endTurn, null);
            }

            // The rail is fixed: the button takes its place at once, so no native show/hide or layout tween can leave it
            // somewhere else when a turn ends (a scaled control's pivot would also shift the visible rect). The native
            // state is Hidden until the player's first turn starts (SetState calls AnimIn before it records Enabled), and
            // the combat UI is drawn from CombatSetUp, before the fight counts as in progress: while the fight is being
            // set up or is running the button stays on the rail, so its first drawn frame is already settled. It leaves
            // only when the fight is over and the Proceed button takes its place.
            endTurn.Position = EndTurnRailPosition(endTurn, refl, hud);
        }

        if ((ui.GetNodeOrNull<Control>("%PingButton") ?? ui.GetNodeOrNull<Control>("PingButton")) is { } pingBtn && pingBtn.Visible)
        {
            if (refl.PingPosTweenField?.GetValue(pingBtn) is Tween pingTween)
            {
                if (pingTween.IsRunning()) pingTween.Kill();
                refl.PingPosTweenField.SetValue(pingBtn, null);
            }
            int pingState = Convert.ToInt32(refl.PingStateField?.GetValue(pingBtn) ?? 2);
            bool pingHidden = pingState == 2;
            pingBtn.Scale = new Vector2(0.85f, 0.85f);
            // 150 by 70 about its centre (75, 35): it floats over the floor above the rail, between End Turn and the
            // exhaust pile, and its touch area reaches 40 units above and below the picture (44 points tall).
            pingBtn.Position = new Vector2(pingHidden ? 1140f : hud.PingShowX, hud.PingShowY);
            PhoneHitArea.Grow(pingBtn, 0f, 40f, 0f, 40f);
        }

        // Proceed button at end of fight: it takes End Turn's place in the rail, so the discard pile stays clear.
        if ((room.GetNodeOrNull<Control>("%ProceedButton") ?? room.GetNodeOrNull<Control>("ProceedButton")) is { } proceedBtn)
        {
            bool enabled = (bool)(refl.ButtonIsEnabledProp?.GetValue(proceedBtn) ?? false);
            if (refl.ProceedAnimTweenField?.GetValue(proceedBtn) is Tween procTween && procTween.IsRunning())
            {
                procTween.Kill();
                refl.ProceedAnimTweenField.SetValue(proceedBtn, null);
            }
            float procW = Math.Max(200f, proceedBtn.Size.X);
            float targetX = enabled ? hud.EndTurnShowX + 110f - procW * 0.5f : 1140f;
            proceedBtn.Position = new Vector2(targetX, hud.EndTurnShowY);
        }

        // Draw, Discard, and Exhaust piles: 80 by 80 about their centre, drawn 44 points across.
        if ((ui.GetNodeOrNull<Control>("%CombatPileContainer") ?? ui.GetNodeOrNull<Control>("CombatPileContainer")) is { } pileContainer)
        {
            pileContainer.AnchorLeft = 0f;
            pileContainer.AnchorTop = 0f;
            pileContainer.AnchorRight = 1f;
            pileContainer.AnchorBottom = 1f;
            pileContainer.OffsetLeft = 0f;
            pileContainer.OffsetTop = 0f;
            pileContainer.OffsetRight = 0f;
            pileContainer.OffsetBottom = 0f;

            if ((pileContainer.GetNodeOrNull<Control>("%DrawPile") ?? pileContainer.GetNodeOrNull<Control>("DrawPile")) is { } drawPile)
            {
                LayoutPileButton(
                    drawPile,
                    refl,
                    new Vector2(hud.DrawShowX, hud.DrawShowY),
                    new Vector2(hud.DrawHideX, hud.DrawHideY),
                    hud.PileScale);
            }

            if ((pileContainer.GetNodeOrNull<Control>("%DiscardPile") ?? pileContainer.GetNodeOrNull<Control>("DiscardPile")) is { } discardPile)
            {
                LayoutPileButton(
                    discardPile,
                    refl,
                    new Vector2(hud.DiscardShowX, hud.DiscardShowY),
                    new Vector2(hud.DiscardHideX, hud.DiscardHideY),
                    hud.PileScale);
            }

            if ((pileContainer.GetNodeOrNull<Control>("%ExhaustPile") ?? pileContainer.GetNodeOrNull<Control>("ExhaustPile")) is { } exhaustPile)
            {
                LayoutPileButton(
                    exhaustPile,
                    refl,
                    new Vector2(hud.ExhaustShowX, hud.ExhaustShowY),
                    new Vector2(hud.ExhaustHideX, hud.ExhaustShowY),
                    hud.PileScale);
            }
        }
    }

    private static void LayoutPileButton(Control pile, CombatReflection refl, Vector2 showPos, Vector2 hidePos, float scale)
    {
        pile.PivotOffset = new Vector2(40f, 40f);
        pile.Scale = new Vector2(scale, scale);
        // The pile's 80-unit art drawn at 1.543 is 123.44 canvas units = 42.9 points, a little short of the 44-point
        // minimum target, so the touch area is grown back around it on all four sides; the drawn pile keeps its size and
        // its badge stays where it is.
        float pileGrow = 80f * scale < PortraitBands.MinTarget ? (PortraitBands.MinTarget - 80f * scale) * 0.5f + 2f : 0f;
        if (pileGrow > 0f)
            PhoneHitArea.Grow(pile, pileGrow, pileGrow, pileGrow, pileGrow);

        refl.PileShowPosField?.SetValue(pile, showPos);
        refl.PileHidePosField?.SetValue(pile, hidePos);

        Tween? tween = refl.PilePosTweenField?.GetValue(pile) as Tween;
        if (tween != null && tween.IsRunning())
            return;

        // When combat is active and the pile is visible, keep it anchored cleanly on showPos.
        if (pile.Visible && refl.IsCombatInProgress())
            pile.Position = showPos;
    }

    internal readonly struct DesktopState
    {
        private readonly float _energyAnchorTop, _energyAnchorBottom, _energyOffLeft, _energyOffTop, _energyOffRight, _energyOffBottom;
        private readonly Vector2 _energyScale;
        private readonly Vector2 _starScale, _starSize;
        private readonly float _starAnchorLeft, _starAnchorTop, _starAnchorRight, _starAnchorBottom;
        private readonly float _starOffLeft, _starOffTop, _starOffRight, _starOffBottom;
        private readonly Vector2 _foolScale, _foolPos;
        private readonly Vector2 _drawScale, _discardScale, _exhaustScale;

        public DesktopState(Control room, CombatReflection refl)
        {
            Control? ui = room.GetNodeOrNull<Control>("%CombatUi") ?? room.GetNodeOrNull<Control>("CombatUi");
            Control? energyCont = ui?.GetNodeOrNull<Control>("%EnergyCounterContainer") ?? ui?.GetNodeOrNull<Control>("EnergyCounterContainer");
            Control? energy = (ui != null ? refl.EnergyCounterField?.GetValue(ui) as Control : null);
            Control? star = (ui != null ? refl.StarCounterField?.GetValue(ui) as Control : null);
            Control? fool = (ui != null ? refl.FoolCombatUiField?.GetValue(ui) as Control : null);
            Control? piles = ui?.GetNodeOrNull<Control>("%CombatPileContainer") ?? ui?.GetNodeOrNull<Control>("CombatPileContainer");
            Control? draw = piles?.GetNodeOrNull<Control>("%DrawPile") ?? piles?.GetNodeOrNull<Control>("DrawPile");
            Control? discard = piles?.GetNodeOrNull<Control>("%DiscardPile") ?? piles?.GetNodeOrNull<Control>("DiscardPile");
            Control? exhaust = piles?.GetNodeOrNull<Control>("%ExhaustPile") ?? piles?.GetNodeOrNull<Control>("ExhaustPile");

            _energyAnchorTop = energyCont?.AnchorTop ?? 1f;
            _energyAnchorBottom = energyCont?.AnchorBottom ?? 1f;
            _energyOffLeft = energyCont?.OffsetLeft ?? 100f;
            _energyOffTop = energyCont?.OffsetTop ?? -252f;
            _energyOffRight = energyCont?.OffsetRight ?? 100f;
            _energyOffBottom = energyCont?.OffsetBottom ?? -252f;
            _energyScale = energy?.Scale ?? Vector2.One;
            _starScale = star?.Scale ?? new Vector2(0.8f, 0.8f);
            _starSize = star?.Size ?? new Vector2(128f, 128f);
            _starAnchorLeft = star?.AnchorLeft ?? 0f;
            _starAnchorTop = star?.AnchorTop ?? 1f;
            _starAnchorRight = star?.AnchorRight ?? 0f;
            _starAnchorBottom = star?.AnchorBottom ?? 1f;
            _starOffLeft = star?.OffsetLeft ?? 64f;
            _starOffTop = star?.OffsetTop ?? -212f;
            _starOffRight = star?.OffsetRight ?? 192f;
            _starOffBottom = star?.OffsetBottom ?? -84f;
            _foolScale = fool?.Scale ?? Vector2.One;
            _foolPos = fool?.Position ?? Vector2.Zero;

            _drawScale = draw?.Scale ?? Vector2.One;
            _discardScale = discard?.Scale ?? Vector2.One;
            _exhaustScale = exhaust?.Scale ?? Vector2.One;
        }

        public void RestoreUi(Control room, Control ui, CombatReflection refl)
        {
            if ((ui.GetNodeOrNull<Control>("%EnergyCounterContainer") ?? ui.GetNodeOrNull<Control>("EnergyCounterContainer")) is { } energyCont)
            {
                energyCont.AnchorLeft = 0f;
                energyCont.AnchorRight = 0f;
                energyCont.AnchorTop = _energyAnchorTop;
                energyCont.AnchorBottom = _energyAnchorBottom;
                energyCont.OffsetLeft = _energyOffLeft;
                energyCont.OffsetTop = _energyOffTop;
                energyCont.OffsetRight = _energyOffRight;
                energyCont.OffsetBottom = _energyOffBottom;
            }
            if (refl.EnergyCounterField?.GetValue(ui) is Control energy)
                energy.Scale = _energyScale;
            if (refl.StarCounterField?.GetValue(ui) is Control star)
            {
                star.AnchorLeft = _starAnchorLeft;
                star.AnchorTop = _starAnchorTop;
                star.AnchorRight = _starAnchorRight;
                star.AnchorBottom = _starAnchorBottom;
                star.OffsetLeft = _starOffLeft;
                star.OffsetTop = _starOffTop;
                star.OffsetRight = _starOffRight;
                star.OffsetBottom = _starOffBottom;
                star.Size = _starSize;
                star.Scale = _starScale;
            }
            if (refl.FoolCombatUiField?.GetValue(ui) is Control fool)
            {
                fool.Scale = _foolScale;
                fool.Position = _foolPos;
            }

            if ((ui.GetNodeOrNull<Control>("%EndTurnButton") ?? ui.GetNodeOrNull<Control>("EndTurnButton")) is { } endTurn)
            {
                endTurn.Scale = Vector2.One;
                int btnState = Convert.ToInt32(refl.EndTurnStateField?.GetValue(endTurn) ?? 2);
                PropertyInfo? posProp = btnState == 2 ? refl.EndTurnHidePosProp : refl.EndTurnShowPosProp;
                if (posProp?.GetValue(endTurn) is Vector2 landscapePos)
                    endTurn.Position = landscapePos;
            }

            if ((ui.GetNodeOrNull<Control>("%PingButton") ?? ui.GetNodeOrNull<Control>("PingButton")) is { } pingBtn)
            {
                PhoneHitArea.Remove(pingBtn);
                pingBtn.Scale = Vector2.One;
                int pState = Convert.ToInt32(refl.PingStateField?.GetValue(pingBtn) ?? 2);
                PropertyInfo? pPos = pState == 2 ? refl.PingHidePosProp : refl.PingShowPosProp;
                if (pPos?.GetValue(pingBtn) is Vector2 lp)
                    pingBtn.Position = lp;
            }

            if ((ui.GetNodeOrNull<Control>("%CombatPileContainer") ?? ui.GetNodeOrNull<Control>("CombatPileContainer")) is { } piles)
            {
                Vector2 vpSize = room.GetViewportRect().Size;
                if ((piles.GetNodeOrNull<Control>("%DrawPile") ?? piles.GetNodeOrNull<Control>("DrawPile")) is { } draw)
                {
                    PhoneHitArea.Remove(draw);
                    draw.Scale = _drawScale;
                    Vector2 show = new(15f, vpSize.Y - 95f);
                    Vector2 hide = show + new Vector2(-150f, 100f);
                    refl.PileShowPosField?.SetValue(draw, show);
                    refl.PileHidePosField?.SetValue(draw, hide);
                    if (draw.Visible) draw.Position = show;
                }
                if ((piles.GetNodeOrNull<Control>("%DiscardPile") ?? piles.GetNodeOrNull<Control>("DiscardPile")) is { } discard)
                {
                    PhoneHitArea.Remove(discard);
                    discard.Scale = _discardScale;
                    Vector2 show = new(vpSize.X - 94f, vpSize.Y - 95f);
                    Vector2 hide = show + new Vector2(150f, 100f);
                    refl.PileShowPosField?.SetValue(discard, show);
                    refl.PileHidePosField?.SetValue(discard, hide);
                    if (discard.Visible) discard.Position = show;
                }
                if ((piles.GetNodeOrNull<Control>("%ExhaustPile") ?? piles.GetNodeOrNull<Control>("ExhaustPile")) is { } exhaust)
                {
                    PhoneHitArea.Remove(exhaust);
                    exhaust.Scale = _exhaustScale;
                    refl.ExhaustSetAnimPositionsMethod?.Invoke(exhaust, null);
                    if (exhaust.Visible && refl.PileShowPosField?.GetValue(exhaust) is Vector2 show)
                        exhaust.Position = show;
                }
            }
        }
    }

    public static void RestoreProceed(Control room, CombatReflection refl)
    {
        if ((room.GetNodeOrNull<Control>("%ProceedButton") ?? room.GetNodeOrNull<Control>("ProceedButton")) is { } proceedBtn)
        {
            bool enabled = (bool)(refl.ButtonIsEnabledProp?.GetValue(proceedBtn) ?? false);
            PropertyInfo? prop = enabled ? refl.ProceedShowPosProp : refl.ProceedHidePosProp;
            if (prop?.GetValue(proceedBtn) is Vector2 p)
                proceedBtn.Position = p;
        }
    }
}
