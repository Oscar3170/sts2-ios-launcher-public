using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using STS2Mobile.Platform;

namespace STS2Mobile.GameHost;

/// <summary>
/// Portrait fixes for in-run overlays not covered elsewhere: tutorial popups kept inside the screen, card selection
/// previews scaled to the screen width, the ancients' art centred, and the Crystal Sphere minigame stacked as sphere over controls. Every anchored edit is
/// captured first so turning Portrait Mode off restores the desktop layout live.
/// <para>Choices sized for a finger: the Skip button and the card reward's alternatives of every choice screen at 44 points
/// tall, the choose a card screen laid out two cards to a row as the card reward is, the relic choice drawn larger, the potion
/// popup with 44 point buttons and kept inside the screen, and a 44 point touch area for the card inspect screen's upgrade
/// tickbox and the card grids' confirm button. Portrait only, and all of it is put back when Portrait Mode is turned off.</para>
/// </summary>
internal sealed class PortraitOverlayLayout
{
    public const string CrystalSphereTypeName = "MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere.NCrystalSphereScreen";
    private const string FtueNamespace = "MegaCrit.Sts2.Core.Nodes.Ftue";
    private const string MapFtueTypeName = FtueNamespace + ".NMapSelectFtue";
    private const float EdgeMargin = 16f;
    private const float PreviewMargin = 40f;

    public const string GameOverTypeName = PortraitGameOverLayout.TypeName;

    public const string DreadDisclaimerTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.Dread.NDreadInfoDisclaimer";
    private const float DreadListWidth = 480f;
    private const float DreadListHeight = 600f;

    public const string VerticalPopupTypeName = "MegaCrit.Sts2.Core.Nodes.CommonUi.NVerticalPopup";
    private const float PopupMaxScale = 1.8f;
    private const float PopupMargin = 40f;
    /// <summary>A touch target is grown past the minimum, so a measured 127 is not a rounding away from 126.9.</summary>
    private const float PopupPadMargin = 3f;

    // The overlay stack's shared backstop, behind every overlay screen and modal, is a solid 0.85 black; in portrait it is
    // tinted so the room art stays visible around the screen instead of the view becoming a black cut-out.
    private const string OverlayStackTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.Overlays.NOverlayStack";
    private static readonly Color OverlayBackstopTint = new(0.015f, 0.02f, 0.03f, 0.55f);

    public static readonly HashSet<string> WatchedTypeNames = new(StringComparer.Ordinal)
    {
        CrystalSphereTypeName, GameOverTypeName, DreadDisclaimerTypeName, VerticalPopupTypeName, OverlayStackTypeName,
    };

    /// <summary>Everything about the run's end screens (defeat, victory, a boss win) lives in its own file.</summary>
    private readonly PortraitGameOverLayout _gameOver = new();

    private readonly Dictionary<ulong, PortraitControlSnapshot> _snapshots = new();
    private readonly HashSet<ulong> _logged = new();
    private ulong _backstopId;
    private Color _backstopColor;
    private bool _backstopTinted;
    private readonly HashSet<ulong> _popupPads = new();

    public void ApplyNode(Control control, string typeName, float capHeight, Vector2 canvas)
    {
        if (typeName == CrystalSphereTypeName) LayoutCrystalSphere(control, capHeight, canvas);
        else if (typeName == GameOverTypeName) _gameOver.Apply(control, capHeight, canvas);
        else if (typeName == DreadDisclaimerTypeName) LayoutDreadDisclaimer(control);
        else if (typeName == VerticalPopupTypeName) LayoutVerticalPopup(control, canvas);
        else if (typeName == OverlayStackTypeName) LayoutOverlayBackstop(control);
    }

    /// <summary>
    /// The game's shared yes/no popup (abandoning a run, from the main menu or the pause menu, and the multiplayer, mod and error
    /// popups) is 522 x 600 units with 180 x 72 unit Yes and No buttons: 25 points tall on the phone. In portrait it is
    /// drawn larger about its centre, up to 1.8 (the buttons 45 points, its text about 16), as far as the screen allows. The
    /// popups that hold it animate only their own position, so the scale holds.
    /// </summary>
    private void LayoutVerticalPopup(Control popup, Vector2 canvas)
    {
        PortraitControlSnapshot snap = Capture(popup);
        float width = snap.InitialOffsetRight - snap.InitialOffsetLeft;
        float height = snap.InitialOffsetBottom - snap.InitialOffsetTop;
        if (width <= 1f || height <= 1f) return;
        float parentScale = popup.GetParent() is Control p && p.IsInsideTree() ? Math.Max(p.GetGlobalTransformWithCanvas().Scale.X, 0.01f) : 1f;
        float onScreen = Math.Min(PopupMaxScale, Math.Min((canvas.X - 2f * PopupMargin) / width, (canvas.Y - 2f * PopupMargin) / height));
        float scale = onScreen / parentScale;
        popup.PivotOffset = new Vector2(width, height) / 2f;
        popup.Scale = new Vector2(scale, scale);
        SizePopupButtons(popup);
        // The plate is not centred by its own anchors: at the width fit it draws 1000 wide from a 522 unit box and lands
        // 25.5 units left of the canvas's middle. Move the popup so its drawn plate is centred (both shapes).
        Rect2 drawn = popup.GetGlobalRect();
        float shift = canvas.X / 2f - drawn.GetCenter().X;
        if (Math.Abs(shift) > 2f) popup.GlobalPosition += new Vector2(shift, 0f);
        if (_logged.Add(popup.GetInstanceId()))
            GD.Print($"Portrait overlay: {popup.GetParent()?.Name}/{popup.Name} drawn at {onScreen:F2}, centred by {shift:F1}.");
    }

    /// <summary>
    /// Yes and No are 180 x 72 unit buttons drawn at the popup's scale, which is not quite enough on every shape (the audit
    /// found 314 x 125.65): each gets a touch area of 44 points as drawn, measured from its own drawn rect so the button's
    /// picture and the popup's scale stay as they are.
    /// </summary>
    private void SizePopupButtons(Control popup)
    {
        foreach (string name in new[] { "YesButton", "NoButton" })
        {
            if (popup.GetNodeOrNull<Control>(name) is not { } button || !button.Visible) continue;
            Rect2 drawn = button.GetGlobalRect();
            Vector2 local = button.Size;
            if (local.X <= 1f || local.Y <= 1f) continue;
            float scaleX = Math.Max(drawn.Size.X / local.X, 0.01f);
            float scaleY = Math.Max(drawn.Size.Y / local.Y, 0.01f);
            float padX = Math.Max(0f, ((PortraitBands.MinTarget + PopupPadMargin) / scaleX - local.X) / 2f);
            float padY = Math.Max(0f, ((PortraitBands.MinTarget + PopupPadMargin) / scaleY - local.Y) / 2f);
            if (padX <= 0f && padY <= 0f) continue;
            PhoneHitArea.Grow(button, padX, padY, padX, padY);
            _popupPads.Add(button.GetInstanceId());
            if (_logged.Add(button.GetInstanceId()))
                GD.Print($"Portrait overlay: {name} drawn {drawn.Size.X:F0}x{drawn.Size.Y:F0}, touch area +{padX:F1},+{padY:F1}.");
        }
    }

    /// <summary>
    /// The shared backstop over the room behind every overlay screen (card selections and the rewards) and modal: 0.85 black,
    /// which would turn the whole portrait view into a black cut-out. It is tinted instead, as the reference layout does, so
    /// the room art stays visible around the screen. The game tweens the backstop's modulate, which keeps working; its colour
    /// is captured the first time and put back in landscape.
    /// </summary>
    private void LayoutOverlayBackstop(Control stack)
    {
        if (stack.GetNodeOrNull<ColorRect>("OverlayBackstop") is not { } backstop) return;
        if (!_backstopTinted)
        {
            _backstopId = backstop.GetInstanceId();
            _backstopColor = backstop.Color;
            _backstopTinted = true;
        }
        if (backstop.Color != OverlayBackstopTint) backstop.Color = OverlayBackstopTint;
        if (_logged.Add(stack.GetInstanceId()))
            GD.Print($"Portrait overlay: overlay backstop tinted {OverlayBackstopTint} (was {_backstopColor}).");
    }

    private void RestoreOverlayBackstop()
    {
        if (!_backstopTinted) return;
        if (GodotObject.InstanceFromId(_backstopId) is ColorRect backstop && GodotObject.IsInstanceValid(backstop))
            backstop.Color = _backstopColor;
        _backstopTinted = false;
        _backstopId = 0;
    }

    /// <summary>
    /// Dread's rules popup lists the ascension modifiers in two columns either side of it, 1616 units across in all: past both
    /// edges of the narrow screen, and they fade in after the popup. In portrait the two columns sit side by side under the popup.
    /// </summary>
    private void LayoutDreadDisclaimer(Control disclaimer)
    {
        if (disclaimer.GetNodeOrNull<Control>("Image") is not { } image) return;
        float below = image.CustomMinimumSize.Y / 2f + 20f;
        foreach ((string name, float left) in new[] { ("LeftList", -DreadListWidth - 8f), ("RightList", 8f) })
        {
            if (image.GetNodeOrNull<Control>(name) is not { } list) continue;
            Capture(list);
            list.OffsetLeft = left;
            list.OffsetRight = left + DreadListWidth;
            list.OffsetTop = below;
            list.OffsetBottom = below + DreadListHeight;
        }
        if (_logged.Add(disclaimer.GetInstanceId())) GD.Print("Portrait overlay: Dread modifier lists moved under the popup.");
    }

    private readonly HashSet<ulong> _ftues = new();
    private readonly HashSet<ulong> _previewRows = new();
    private readonly HashSet<ulong> _peekButtons = new();
    private readonly HashSet<ulong> _inspectScreens = new();
    private readonly Dictionary<ulong, InspectUpgradeState> _inspectUpgrades = new();
    private readonly HashSet<ulong> _inspectArranged = new();
    private readonly Dictionary<ulong, ulong> _choicePiles = new();
    private readonly Dictionary<ulong, PortraitCombatRail.ChoicePileDesktop> _choicePileStates = new();
    private bool _inspectFrameBound;
    private bool _handPeekFrameBound;
    private const string PeekButtonTypeName = "MegaCrit.Sts2.Core.Nodes.Combat.NPeekButton";
    private const string HandNodeTypeName = "MegaCrit.Sts2.Core.Nodes.Combat.NPlayerHand";
    private const string CardSelectionNamespace = "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection";
    private const float PeekSideMargin = 32f;
    private const float PeekBottomMargin = 150f;
    // The hand's own peek (the eye in a selection) moves out of the rail entirely; the margin keeps its 128-unit (44
    // point) picture inside the canvas while clear of every pile, preview and the hand.
    private const float HandPeekSideMargin = 20f;

    /// <summary>Called for every node the tree adds: tutorial popups and card preview rows are followed from then on.</summary>
    public void Track(Node node)
    {
        if (node is not Control control) return;
        Type type = control.GetType();
        if (type.Namespace == FtueNamespace && type.FullName != MapFtueTypeName && IsFtueRoot(type))
            _ftues.Add(control.GetInstanceId());
        else if (type.FullName == PeekButtonTypeName)
            _peekButtons.Add(control.GetInstanceId());
        else if (type.FullName == InspectCardScreenTypeName)
            RememberInspectScreen(control);
        else if (control.Name == "Cards" && control.GetParent() is Control parent &&
                 parent.Name.ToString().Contains("PreviewContainer", StringComparison.Ordinal) &&
                 !PortraitSelectionPreview.Owns(control))
            _previewRows.Add(control.GetInstanceId());
        else
        {
            TrackChoice(control, type.FullName);
            // A card choice screen in a fight carries its own copy of the draw, discard and exhaust piles for Peek mode.
            // They are laid out like the fight's own piles so peeking never shows them at their desktop corners.
            if (control.GetNodeOrNull<Control>("%CombatPiles") is { } combatPiles)
                _choicePiles[control.GetInstanceId()] = combatPiles.GetInstanceId();
        }
    }

    private static bool IsFtueRoot(Type type)
    {
        for (Type? t = type; t != null; t = t.BaseType)
            if (t.Name == "NFtue") return true;
        return false;
    }

    /// <summary>Per frame: popups and previews are placed by the game as they open, so they are kept in each frame.</summary>
    public void OnProcessFrame(Window root)
    {
        Vector2 canvas = root.GetVisibleRect().Size;
        foreach (ulong id in _ftues)
            if (GodotObject.InstanceFromId(id) is Control ftue && GodotObject.IsInstanceValid(ftue) && ftue.IsVisibleInTree())
                KeepFtueOnScreen(ftue, canvas);
        foreach (ulong id in _previewRows)
            if (GodotObject.InstanceFromId(id) is Control row && GodotObject.IsInstanceValid(row) && row.IsVisibleInTree())
                FitPreviewRow(row, canvas);
        bool handPeekVisible = false;
        foreach (ulong id in _peekButtons)
        {
            if (GodotObject.InstanceFromId(id) is not Control peek || !GodotObject.IsInstanceValid(peek) ||
                !peek.IsInsideTree())
                continue;
            PlacePeekButton(peek);
            if (peek.Visible && peek.GetParent()?.GetType().FullName == HandNodeTypeName) handPeekVisible = true;
        }
        // The hand pass re-places its peek in FramePreDraw (after this process pass), so the hand's eye is put back above
        // the rail from our own pre-draw handler, connected later and therefore after the hand's.
        if (handPeekVisible) BindHandPeekFrame();
        else UnbindHandPeekFrame();
        UpdateChoices(canvas);
        if (_inspectScreens.Count > 0)
        {
            bool anyVisible = false;
            foreach (ulong id in _inspectScreens)
            {
                if (Live(id) is not { } screen || !screen.IsVisibleInTree()) continue;
                anyVisible = true;
                LayoutInspectUpgrade(screen, canvas);
            }
            // The tip pass is only worth its per-frame callback while an inspector is on screen; when the reused screen is
            // closed it stays in the tree, so visibility, not the tracked set, decides.
            if (anyVisible) BindInspectFrame();
            else UnbindInspectFrame();
        }
        _gameOver.OnProcessFrame();
    }

    /// <summary>
    /// Every card choice screen shown in a fight has a peek button (to look at the fight behind it) at the left middle, which in
    /// portrait sits on the first card: a touch there hit the button and the card did not pick. In portrait it moves to
    /// the bottom left corner, under the cards' row and beside the screen's own prompt, clear of the rail's piles. The
    /// hand's own peek in a selection moves just above the rail's left end, off the draw pile it used to cover.
    /// </summary>
    private void PlacePeekButton(Control peek)
    {
        if (peek.GetParent()?.GetType().FullName == HandNodeTypeName)
        {
            PlaceHandPeek(peek);
            return;
        }
        if (peek.GetParent()?.GetType().Namespace != CardSelectionNamespace) return;
        // The bundle choice places it in its own footer beside Cancel and Confirm.
        if (peek.GetParent()?.GetType().FullName == ChooseBundleTypeName) return;
        // The hand's select mode has its own rule above; no snapshot is taken here, so its native restore is the hand's.
        Capture(peek);
        float size = peek.OffsetRight - peek.OffsetLeft;
        if (size < 1f) size = 128f;
        if (peek.AnchorTop == 1f && Math.Abs(peek.OffsetBottom + PeekBottomMargin) < 0.5f) return;
        peek.AnchorLeft = peek.AnchorRight = 0f;
        peek.AnchorTop = peek.AnchorBottom = 1f;
        peek.OffsetLeft = PeekSideMargin;
        peek.OffsetRight = PeekSideMargin + size;
        peek.OffsetBottom = -PeekBottomMargin;
        peek.OffsetTop = -PeekBottomMargin - size;
        if (_logged.Add(peek.GetInstanceId())) GD.Print($"Portrait overlay: peek button of {peek.GetParent()!.GetType().Name} moved to the bottom left.");
    }

    /// <summary>
    /// The hand's select-mode eye used to sit at the rail's left end, on top of the fight's draw pile. It moves to the free
    /// top-left of the run content: a 44-point picture inside the safe area, clear of the piles, End Turn, the energy orb,
    /// the hand and the selection previews (which can fill the whole band above the rail). The hand layout keeps its own
    /// capture/restore: this pass only moves it while it is visible.
    /// </summary>
    private void BindHandPeekFrame()
    {
        if (_handPeekFrameBound) return;
        _handPeekFrameBound = true;
        RenderingServer.FramePreDraw += PlaceHandPeeksFrame;
    }

    private void UnbindHandPeekFrame()
    {
        if (!_handPeekFrameBound) return;
        _handPeekFrameBound = false;
        RenderingServer.FramePreDraw -= PlaceHandPeeksFrame;
    }

    private void PlaceHandPeeksFrame()
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        foreach (ulong id in _peekButtons)
        {
            if (GodotObject.InstanceFromId(id) is not Control peek || !GodotObject.IsInstanceValid(peek) ||
                !peek.IsInsideTree() || !peek.Visible)
                continue;
            if (peek.GetParent()?.GetType().FullName == HandNodeTypeName) PlaceHandPeek(peek);
        }
    }

    private void PlaceHandPeek(Control peek)
    {
        if (!peek.Visible) return;
        Vector2 canvas = CanvasOf(peek);
        float size = Math.Max(PortraitBands.MinTarget, Math.Max(peek.Size.X, peek.Size.Y));
        // A hand selection fills the band above the rail with previews (the whole-hand and Purity picks do, and the
        // single-card Armaments preview reaches up to the top-left), so the eye goes to the free top-right corner right
        // under the stone header: inside the safe area, above every preview and pile, and a 44-point target of its own.
        float headerBottom = peek.GetTree() is { } tree
            ? PortraitLayout.ComputeCapHeight(tree.Root) + PortraitBands.TopBarHeight
            : 0f;
        float left = canvas.X - HandPeekSideMargin - size;
        float top = headerBottom;
        Vector2 parentScale = peek.GetParent() is Control parent
            ? parent.GetGlobalTransformWithCanvas().Scale.Abs()
            : Vector2.One;
        Vector2 localSize = new(size / Math.Max(parentScale.X, 0.01f), size / Math.Max(parentScale.Y, 0.01f));
        Vector2 local = peek.GetParent() is Control owner
            ? owner.GetGlobalTransformWithCanvas().AffineInverse() * new Vector2(left, top)
            : new Vector2(left, top);
        if (peek.AnchorLeft != 0f || peek.AnchorTop != 0f || peek.AnchorRight != 0f || peek.AnchorBottom != 0f)
        {
            peek.AnchorLeft = 0f;
            peek.AnchorTop = 0f;
            peek.AnchorRight = 0f;
            peek.AnchorBottom = 0f;
        }
        if (peek.PivotOffset != Vector2.Zero) peek.PivotOffset = Vector2.Zero;
        if (peek.Scale != Vector2.One) peek.Scale = Vector2.One;
        if (peek.Size.DistanceSquaredTo(localSize) > 0.25f) peek.Size = localSize;
        if (peek.Position.DistanceSquaredTo(local) > 0.25f) peek.Position = local;
        if (_logged.Add(peek.GetInstanceId()))
            GD.Print($"Portrait overlay: the hand's peek button moved above the rail at {left:F0},{top:F0}.");
    }

    private void KeepFtueOnScreen(Control ftue, Vector2 canvas)
    {
        foreach (Control popup in ftue.GetChildren().OfType<Control>())
        {
            if (!popup.Visible || popup.Name == "Arrow") continue;
            Rect2 rect = popup.GetGlobalRect();
            if (rect.Size.X >= canvas.X * 0.95f && rect.Size.Y >= canvas.Y * 0.95f) continue;
            float dx = 0f, dy = 0f;
            if (rect.End.X > canvas.X - EdgeMargin) dx = canvas.X - EdgeMargin - rect.End.X;
            if (rect.Position.X + dx < EdgeMargin) dx = EdgeMargin - rect.Position.X;
            if (rect.End.Y > canvas.Y - EdgeMargin) dy = canvas.Y - EdgeMargin - rect.End.Y;
            if (rect.Position.Y + dy < EdgeMargin) dy = EdgeMargin - rect.Position.Y;
            if (Math.Abs(dx) < 0.5f && Math.Abs(dy) < 0.5f) continue;
            Vector2 parentScale = popup.GetParent() is Control p ? p.GetGlobalTransformWithCanvas().Scale : Vector2.One;
            popup.Position += new Vector2(dx / Math.Max(parentScale.X, 0.01f), dy / Math.Max(parentScale.Y, 0.01f));
            if (_logged.Add(popup.GetInstanceId()))
                GD.Print($"Portrait overlay: kept {ftue.GetType().Name}/{popup.Name} on screen (moved {dx:F0},{dy:F0}).");
        }
    }

    /// <summary>
    /// The game scales its preview row by card count (1, 0.8, 0.55) for a 1920-wide screen; three or more cards run off the
    /// portrait screen, so the row's scale is capped at what fits. The game sets its pivot to the row's centre.
    /// </summary>
    private static void FitPreviewRow(Control row, Vector2 canvas)
    {
        if (row.Size.X <= 1f) return;
        float fit = (canvas.X - 2f * PreviewMargin) / row.Size.X;
        if (row.Scale.X > fit + 0.001f) row.Scale = new Vector2(fit, fit);
    }

    // ---- Crystal Sphere -------------------------------------------------------------------------------------------------

    private const float TopBarRowHeight = PortraitBands.TopBarHeight;
    private const float SphereRadius = 430f;

    private void LayoutCrystalSphere(Control screen, float capHeight, Vector2 canvas)
    {
        // The sphere sits left of centre in 2560 x 1200 art (x 1086, y 674 of the art, scaled 1.04 about 1280, 600). Bring it
        // to the middle of the screen under the top bar, and the controls under it.
        float sphereY = capHeight + TopBarRowHeight + 24f + SphereRadius;
        if (screen.GetNodeOrNull<Control>("Bg") is { } bg)
        {
            PortraitControlSnapshot snap = Capture(bg);
            float centreX = canvas.X / 2f, centreY = canvas.Y / 2f;
            float sphereNowX = centreX + (1086.5f - 1280f) * 1.04f;
            float sphereNowY = centreY - 588f + 600f + (674f - 600f) * 1.04f;
            float dx = centreX - sphereNowX, dy = sphereY - sphereNowY;
            bg.OffsetLeft = snap.InitialOffsetLeft + dx;
            bg.OffsetRight = snap.InitialOffsetRight + dx;
            bg.OffsetTop = snap.InitialOffsetTop + dy;
            bg.OffsetBottom = snap.InitialOffsetBottom + dy;
        }
        if (screen.GetNodeOrNull<Control>("Ui/RightUi") is { } rightUi)
        {
            Capture(rightUi);
            float width = rightUi.OffsetRight - rightUi.OffsetLeft;
            float height = rightUi.OffsetBottom - rightUi.OffsetTop;
            rightUi.AnchorLeft = rightUi.AnchorRight = 0.5f;
            rightUi.AnchorTop = rightUi.AnchorBottom = 0f;
            rightUi.OffsetLeft = -width / 2f;
            rightUi.OffsetRight = width / 2f;
            rightUi.OffsetTop = sphereY + SphereRadius + 40f;
            rightUi.OffsetBottom = rightUi.OffsetTop + height;
        }
        if (screen.GetNodeOrNull<Control>("Ui") is { } ui)
        {
            // The Ui layer has a 1544-wide minimum; on the narrow screen it would grow past the right edge.
            Capture(ui);
            ui.CustomMinimumSize = Vector2.Zero;
        }
        if (screen.GetNodeOrNull<Control>("Dialogue") is { } dialogue)
        {
            PortraitControlSnapshot snap = Capture(dialogue);
            // The speech bubble keeps its distance from the right edge, nudged in, and drops below the top bar.
            dialogue.OffsetLeft = snap.InitialOffsetLeft + (canvas.X - 1920f) + 200f;
            dialogue.OffsetRight = snap.InitialOffsetRight + (canvas.X - 1920f) + 200f;
            dialogue.OffsetTop = snap.InitialOffsetTop + capHeight;
            dialogue.OffsetBottom = snap.InitialOffsetBottom + capHeight;
        }
        if (_logged.Add(screen.GetInstanceId())) GD.Print($"Portrait overlay: crystal sphere laid out (sphere centre y {sphereY:F0}).");
    }

    // ---- Choices sized for a finger ---------------------------------------------------------------------------------------

    private const string AlternativeButtonTypeName = CardSelectionNamespace + ".NCardRewardAlternativeButton";
    private const string SkipButtonTypeName = CardSelectionNamespace + ".NChoiceSelectionSkipButton";
    private const string ChooseCardTypeName = CardSelectionNamespace + ".NChooseACardSelectionScreen";
    private const string ChooseRelicTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.NChooseARelicSelection";
    private const string ChooseBundleTypeName = CardSelectionNamespace + ".NChooseABundleSelectionScreen";
    private const string PotionPopupTypeName = "MegaCrit.Sts2.Core.Nodes.Potions.NPotionPopup";
    private const string UpgradeTickboxTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.InspectScreens.NUpgradePreviewTickbox";
    private const string InspectCardScreenTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.NInspectCardScreen";
    private const string ConfirmButtonTypeName = "MegaCrit.Sts2.Core.Nodes.CommonUi.NConfirmButton";
    private const BindingFlags InstAll = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    // The Skip button and the card reward's alternatives: 276 x 73 in the game, 44 points tall here with the picture kept in
    // proportion where the row has room, and the text grown with it up to this much.
    private static readonly Vector2 ChoiceButtonBaseSize = new(276f, 73f);
    private const float ChoiceSideMargin = 24f;
    private const float ChoiceMaxFontGrowth = 1.5f;

    // Choose a card: the card reward's own portrait numbers (PortraitRewardsLayout), so both screens read alike. Its footer
    // row holds the peek button at the left and Skip at the right, below the cards; the row is as tall as the larger control.
    private const float ChoiceCardWidth = 300f;
    private const float ChoiceCardHeight = 422f;
    private const float ChoiceCardGapX = 48f;
    private const float ChoiceCardGapY = 32f;
    private const float ChoiceMaxCardScale = 1.5f;
    private const float ChoiceHolderScale = 0.8f;
    private const float ChoiceCardSideMargin = 44f;
    private const float ChoiceBannerBottom = 172f;
    private const float ChoiceBannerGap = 8f;
    private const float ChoiceFooterGap = 8f;
    private const float ChoiceBottomMargin = 24f;
    private const float ChoiceCardFloorScale = 289f / 300f;
    private const float ChoiceRightMargin = PortraitBetweenMath.ActionRightMargin;
    private const float ChoiceCardRowSpacing = 340f;
    private const float PeekSize = 128f;
    private const float PeekScale = 1.6f;
    private const float PeekLeft = 46f;

    // The relic choice: 68 unit holders drawn at twice their size about their middle, 200 units apart.
    private const float RelicHolderSize = 68f;
    private const float RelicHolderScale = 2f;
    private const float RelicSpacing = 200f;
    private const float RelicMaxRowScale = 1.6f;
    private const float RelicTipRoom = 230f;

    // The bundle choice (the ScrollBoxes relic): two packs of three cards fanned 45 apart. Each pack's card is 300 x 422,
    // the fan 390 x 512; in portrait the packs are drawn at the card reward's size, the chosen pack's cards in one row, and
    // Cancel/Confirm are right-aligned in the footer with Peek at its left.
    private const float BundleCardWidth = 300f;
    private const float BundleCardHeight = 422f;
    private const float BundleFanWidth = 390f;
    private const float BundleFanHeight = 512f;
    private const float BundleGapX = 48f;
    private const float BundleHitboxCentreX = 5.5f;
    private const float BundleHitboxCentreY = -27f;
    private const float BundlePreviewMaxScale = 1.5f;
    private const float BundleFooterGap = 24f;
    private const float BundleCardFloorScale = 289f / BundleCardWidth;

    private const float PotionPopupGap = 12f;
    private const float PotionPopupMaxScale = 1.6f;
    private const float InspectTickboxRaise = 10f;

    private sealed class ChoiceButton(PortraitControlSnapshot snapshot, Vector2 minSize, Vector2 pivot)
    {
        public readonly PortraitControlSnapshot Snapshot = snapshot;
        public readonly Vector2 MinSize = minSize;
        public readonly Vector2 Pivot = pivot;
        public float LabelLeft, LabelRight;
        public int LabelMaxFont;
        public Vector2 Applied;

        public Vector2 BaseSize => MinSize is { X: > 1f, Y: > 1f } ? MinSize : ChoiceButtonBaseSize;
    }

    private readonly HashSet<ulong> _choiceButtonIds = new();
    private readonly Dictionary<ulong, ChoiceButton> _choiceButtons = new();
    private readonly HashSet<ulong> _chooseCardScreens = new();
    private readonly HashSet<ulong> _chooseRelicScreens = new();
    private readonly HashSet<ulong> _chooseBundleScreens = new();
    private readonly Dictionary<ulong, Vector2> _bundleButtonSizes = new();
    private readonly Dictionary<ulong, (Vector2 Centre, float Width)> _buttonRows = new();
    private readonly Dictionary<ulong, List<ulong>> _cardOrders = new();
    private readonly HashSet<ulong> _choiceSnapshots = new();
    private readonly HashSet<ulong> _potionPopups = new();
    private readonly Dictionary<ulong, (Vector2 Position, Vector2 Scale, bool TopLevel)> _fittedPopups = new();
    private readonly HashSet<ulong> _inspectTickboxes = new();
    private readonly HashSet<ulong> _liftedChoiceBanners = new();
    private readonly HashSet<ulong> _gridConfirms = new();
    private readonly Dictionary<ulong, float> _confirmPads = new();
    private bool _choicesFailed;

    internal static PortraitOverlayLayout? Instance { get; private set; }

    public PortraitOverlayLayout() => Instance = this;

    private void TrackChoice(Control control, string? typeName)
    {
        ulong id = control.GetInstanceId();
        switch (typeName)
        {
            case AlternativeButtonTypeName or SkipButtonTypeName:
                _choiceButtonIds.Add(id);
                break;
            case ChooseCardTypeName:
                _chooseCardScreens.Add(id);
                break;
            case ChooseRelicTypeName:
                _chooseRelicScreens.Add(id);
                break;
            case ChooseBundleTypeName:
                _chooseBundleScreens.Add(id);
                break;
            case PotionPopupTypeName:
                _potionPopups.Add(id);
                return;
            case UpgradeTickboxTypeName:
                // The deck and deck-selection screens' View Upgrades box belongs to the card grid layout (PortraitCardGridLayout).
                if (control.GetParent()?.GetType().FullName == InspectCardScreenTypeName) _inspectTickboxes.Add(id);
                return;
            case ConfirmButtonTypeName:
                // The card selection screens' own confirm, and the one in their preview panels. The deck's upgrade, enchant,
                // transform and deck-select screens place and draw theirs themselves (PortraitSelectionPreview).
                if (!PortraitSelectionPreview.Owns(control) && control.GetParent() is { } holder &&
                    (holder.GetType().Namespace == CardSelectionNamespace ||
                     holder.GetParent()?.GetType().Namespace == CardSelectionNamespace))
                    _gridConfirms.Add(id);
                return;
            default:
                return;
        }
        // A choice screen and its buttons take their portrait places as they enter the tree, before the game's _Ready: the Skip
        // button slides in to where it stands then, so it must already stand in its portrait place.
        if (_choicesFailed || !SafeAreaCanvas.IsPortrait) return;
        try
        {
            Vector2 canvas = CanvasOf(control);
            if (typeName is ChooseCardTypeName or ChooseRelicTypeName) LayoutChoiceScreen(control, canvas);
            else if (typeName == ChooseBundleTypeName) LayoutChooseBundle(control, canvas);
            else GrowChoiceButton(control, canvas);
        }
        catch (Exception error)
        {
            FailChoices(error);
        }
    }

    private void UpdateChoices(Vector2 canvas)
    {
        if (_choicesFailed) return;
        try
        {
            foreach (ulong id in _chooseCardScreens)
                if (Live(id) is { } screen) LayoutChooseCard(screen, canvas);
            foreach (ulong id in _chooseRelicScreens)
                if (Live(id) is { } screen) LayoutChooseRelic(screen, canvas);
            foreach (ulong id in _chooseBundleScreens)
                if (Live(id) is { } screen) LayoutChooseBundle(screen, canvas);
            foreach (ulong id in _choiceButtonIds)
                if (Live(id) is { } button) GrowChoiceButton(button, canvas);
            foreach (ulong id in _potionPopups)
                if (Live(id) is { } popup) FitPotionPopup(popup, canvas);
            foreach (ulong id in _inspectTickboxes)
                if (Live(id) is { } tickbox) GrowInspectTickbox(tickbox);
            foreach (ulong id in _gridConfirms)
                if (Live(id) is { } confirm) GrowGridConfirm(confirm);
            LayoutChoicePiles(canvas);
        }
        catch (Exception error)
        {
            FailChoices(error);
        }
    }

    /// <summary>A failure leaves the choices as the game lays them out: small, but all of them working.</summary>
    private void FailChoices(Exception error)
    {
        if (_choicesFailed) return;
        _choicesFailed = true;
        GD.PrintErr("Portrait overlay: the larger choices were turned off after an error: ", error);
        try
        {
            RestoreChoices();
        }
        catch (Exception again)
        {
            GD.PrintErr("Portrait overlay: the choices could not be put back: ", again.Message);
        }
    }

    private static Control? Live(ulong id) =>
        GodotObject.InstanceFromId(id) is Control control && GodotObject.IsInstanceValid(control) && control.IsInsideTree() &&
        !control.IsQueuedForDeletion() ? control : null;

    /// <summary>The canvas a node is drawn on; a node that is still entering the tree is asked through its parent.</summary>
    private static Vector2 CanvasOf(Node node)
    {
        Node? inTree = node;
        while (inTree != null && !inTree.IsInsideTree()) inTree = inTree.GetParent();
        return inTree?.GetViewport()?.GetVisibleRect().Size ?? new Vector2(1080f, SafeAreaCanvas.MinPortraitHeight);
    }

    /// <summary>A full-screen choice screen's size; before its first layout that is its parent's, or the canvas.</summary>
    private static Vector2 ScreenSize(Control screen, Vector2 canvas)
    {
        if (screen.Size is { X: > 1f, Y: > 1f }) return screen.Size;
        return screen.GetParent() is Control { Size: { X: > 1f, Y: > 1f } } parent ? parent.Size : canvas;
    }

    private static int CountOf(Control screen, string field) =>
        screen.GetType().GetField(field, InstAll)?.GetValue(screen) is IEnumerable list ? list.Cast<object>().Count() : 0;

    private PortraitControlSnapshot CaptureChoice(Control control)
    {
        _choiceSnapshots.Add(control.GetInstanceId());
        return Capture(control);
    }

    // ---- Skip and the card reward's alternatives ---------------------------------------------------------------------------

    /// <summary>
    /// The choice buttons (the card reward's alternatives and the choice screens' Skip) are 96 x 25 points in the game. In
    /// portrait each is 44 points tall and as wide as its picture then is, less when its row has no room for that: the picture
    /// keeps its shape, so the button is then taller than its picture. The game animates the button's scale (hover and press),
    /// so the button is given a larger minimum size, not a scale; the picture follows the size by its anchors, and the text is
    /// given a larger font limit.
    /// </summary>
    private Vector2 GrowChoiceButton(Control button, Vector2 canvas)
    {
        ulong id = button.GetInstanceId();
        if (!_choiceButtons.TryGetValue(id, out ChoiceButton? state))
        {
            state = new ChoiceButton(new PortraitControlSnapshot(button, captureVisible: false), button.CustomMinimumSize, button.PivotOffset);
            if (button.GetNodeOrNull<Control>("Label") is { } text)
            {
                state.LabelLeft = text.OffsetLeft;
                state.LabelRight = text.OffsetRight;
                state.LabelMaxFont = text.GetType().GetProperty("MaxFontSize", InstAll)?.GetValue(text) is int max ? max : 0;
            }
            _choiceButtons[id] = state;
        }

        Vector2 size = ChoiceButtonSize(button, state, canvas);
        if (button.CustomMinimumSize != size) button.CustomMinimumSize = size;
        if (button.PivotOffset != size / 2f) button.PivotOffset = size / 2f;
        if (state.Applied == size) return size;
        state.Applied = size;
        if (button.GetNodeOrNull<Control>("Label") is { } label)
        {
            float wider = size.X / state.BaseSize.X;
            int font = state.LabelMaxFont > 0 ? (int)MathF.Round(state.LabelMaxFont * Math.Min(wider, ChoiceMaxFontGrowth)) : 0;
            SetButtonLabel(label, font, state.LabelLeft * wider, state.LabelRight * wider);
        }
        if (_logged.Add(id))
            GD.Print($"Portrait overlay: {button.GetType().Name} {button.Name} of {button.GetParent()?.Name} is {size.X:F0}x{size.Y:F0} (was {state.BaseSize.X:F0}x{state.BaseSize.Y:F0}).");
        return size;
    }

    private Vector2 ChoiceButtonSize(Control button, ChoiceButton state, Vector2 canvas)
    {
        Control? parent = button.GetParent() as Control;
        Vector2 scale = parent != null && parent.IsInsideTree() ? parent.GetGlobalTransformWithCanvas().Scale : Vector2.One;
        float scaleX = Math.Max(scale.X, 0.01f), scaleY = Math.Max(scale.Y, 0.01f);
        Vector2 baseSize = state.BaseSize;
        float height = Math.Max(baseSize.Y, PortraitBands.MinTarget / scaleY);
        float width = baseSize.X * height / baseSize.Y;
        float room;
        if (parent is HBoxContainer box)
        {
            int count = Math.Max(1, box.GetChildren().OfType<Control>().Count(c => c.Visible && !c.IsQueuedForDeletion()));
            room = (canvas.X / scaleX - 2f * ChoiceSideMargin - (count - 1) * box.GetThemeConstant("separation")) / count;
        }
        else
        {
            // Skip alone in the row its screen gives it (its parent is the screen, not a container).
            room = parent != null && _buttonRows.TryGetValue(parent.GetInstanceId(), out (Vector2 Centre, float Width) given)
                ? given.Width
                : canvas.X / scaleX - 2f * ChoiceSideMargin;
        }
        width = Math.Max(baseSize.X, Math.Min(width, room));
        return new Vector2(MathF.Floor(width), MathF.Round(height));
    }

    /// <summary>The game's label fits its text between a smallest and a largest font size, whenever its size changes.</summary>
    private static void SetButtonLabel(Control label, int maxFont, float left, float right)
    {
        Type type = label.GetType();
        if (maxFont > 0) type.GetProperty("MaxFontSize", InstAll)?.SetValue(label, maxFont);
        label.OffsetLeft = left;
        label.OffsetRight = right;
        if (maxFont > 0 && label.IsInsideTree()) type.GetMethod("AdjustFontSize", InstAll, Type.EmptyTypes)?.Invoke(label, null);
    }

    private static void RestoreChoiceButton(Control button, ChoiceButton state)
    {
        if (state.Applied == default) return;
        state.Applied = default;
        if (button.GetParent() is Container container)
        {
            // A container places its own children: only the size goes back, and it lays them out again.
            button.CustomMinimumSize = state.MinSize;
            button.PivotOffset = state.Pivot;
            container.QueueSort();
        }
        else
        {
            state.Snapshot.Restore(button);
        }
        if (button.GetNodeOrNull<Control>("Label") is { } label)
            SetButtonLabel(label, state.LabelMaxFont, state.LabelLeft, state.LabelRight);
    }

    // ---- Choose a card and choose a relic --------------------------------------------------------------------------------

    /// <summary>
    /// Skip stands in the middle of its screen's portrait row, at the size a finger needs. It is placed at once when the screen
    /// enters the tree, before the game's <c>_Ready</c> reads where it stands: the game slides it in to that place.
    /// </summary>
    private void PlaceSkip(Control screen, Vector2 centre, Vector2 canvas)
    {
        if (screen.GetNodeOrNull<Control>("SkipButton") is not { } skip) return;
        Vector2 size = GrowChoiceButton(skip, canvas);
        float left = centre.X - size.X / 2f, top = centre.Y - size.Y / 2f;
        if (Math.Abs(skip.OffsetLeft - left) < 0.25f && Math.Abs(skip.OffsetTop - top) < 0.25f &&
            Math.Abs(skip.OffsetRight - (left + size.X)) < 0.25f && Math.Abs(skip.OffsetBottom - (top + size.Y)) < 0.25f)
            return;
        skip.OffsetLeft = left;
        skip.OffsetTop = top;
        skip.OffsetRight = left + size.X;
        skip.OffsetBottom = top + size.Y;
    }

    private void LayoutChoiceScreen(Control screen, Vector2 canvas)
    {
        string? typeName = screen.GetType().FullName;
        if (typeName == ChooseCardTypeName) LayoutChooseCard(screen, canvas);
        else if (typeName == ChooseRelicTypeName) LayoutChooseRelic(screen, canvas);
    }

    /// <summary>
    /// The choose a card screen puts its cards in one row at 0.8 of their size, 340 units apart: three cards are small on the
    /// tall screen and four run off it. In portrait they stand as the card reward's do (two to a row, an odd last card centred)
    /// under the banner, or three in one row on the shorter canvas where two rows would push a focused card into the footer;
    /// the footer row holds the peek button at the left and Skip at the right, its top kept an eighth of a card clear of the
    /// focused bottom card (the game grows a focused holder by a quarter about its middle).
    /// </summary>
    private void LayoutChooseCard(Control screen, Vector2 canvas)
    {
        // Without the phone's selection pass the game's own layout stays: its Skip button is not placed by anyone here.
        if (!PhoneSelectConfirm.Installed || screen.GetNodeOrNull<Control>("CardRow") is not { } row) return;
        ulong screenId = screen.GetInstanceId();
        List<Control> holders = row.GetChildren().OfType<Control>().Where(h => !h.IsQueuedForDeletion()).ToList();
        int count = holders.Count > 0 ? holders.Count : CountOf(screen, "_cards");
        if (count <= 0) return;

        Vector2 size = ScreenSize(screen, canvas);
        Vector2 centre = size / 2f;
        // The banner rises to just under the relic strip, as on the card reward, so the cards can grow.
        float lift = (Engine.GetMainLoop() as SceneTree)?.Root is { } root
            ? PortraitBetweenLayout.BannerLift(size, PortraitLayout.ComputeCapHeight(root)) : 0f;
        PortraitBetweenLayout.LiftBanner(screen.GetNodeOrNull<Control>("Banner"), size, lift, _liftedChoiceBanners);

        bool peekShown = PeekShown(screen);
        float peekScale = PeekScale;
        float footerHeight = peekShown ? Math.Max(PortraitBands.MinTarget, PeekSize * peekScale) : PortraitBands.MinTarget;
        float bandTop = centre.Y - ChoiceBannerBottom - lift + ChoiceBannerGap;
        float footerTopMax = size.Y - ChoiceBottomMargin - footerHeight;
        float bandBottom = footerTopMax - ChoiceFooterGap;
        // One to three cards are one row like the reference's card row; four or more keep the widening grid.
        CardGridFit grid = count <= 3
            ? PortraitBetweenMath.CardRow(
                count, size.X, bandTop, bandBottom, ChoiceCardWidth, ChoiceCardHeight)
            : PortraitBetweenMath.CardGrid(
                count, size.X, bandTop, bandBottom, ChoiceCardWidth, ChoiceCardHeight,
                ChoiceCardGapX, ChoiceCardGapY, ChoiceMaxCardScale, ChoiceCardSideMargin, ChoiceCardFloorScale);
        if (peekShown && count > 3 && ChoiceCardWidth * grid.Scale < ChoiceCardFloorScale * ChoiceCardWidth)
        {
            // Four or more cards cannot take one row: at the peek's 1.6 scale its footer is taller than a finger needs, and
            // it pushes the second row under the phone's floor. The peek keeps its own 128 units (a 44-point target) and
            // the cards get the row back.
            peekScale = Math.Min(PeekScale, PortraitBands.MinTarget / PeekSize);
            footerHeight = PortraitBands.MinTarget;
            footerTopMax = size.Y - ChoiceBottomMargin - footerHeight;
            bandBottom = footerTopMax - ChoiceFooterGap;
            grid = PortraitBetweenMath.CardGrid(
                count, size.X, bandTop, bandBottom, ChoiceCardWidth, ChoiceCardHeight,
                ChoiceCardGapX, ChoiceCardGapY, ChoiceMaxCardScale, ChoiceCardSideMargin, ChoiceCardFloorScale);
        }
        float cardScale = grid.Scale;
        float rowScale = cardScale / ChoiceHolderScale;
        float focusGrow = PortraitBetweenMath.CardFocusGrowth * ChoiceCardHeight * cardScale;
        float blockHeight = grid.BlockHeight;
        // One row of cards sits under the banner like the reference's row; a taller widening grid fills the band.
        float blockTop = bandTop + focusGrow;
        float blockCentreY = blockTop + blockHeight / 2f;

        CaptureChoice(row);
        var scale = new Vector2(rowScale, rowScale);
        if (row.Scale != scale) row.Scale = scale;
        float rowY = blockCentreY - centre.Y;
        if (Math.Abs(row.OffsetTop - rowY) > 0.25f || Math.Abs(row.OffsetBottom - rowY) > 0.25f)
        {
            row.OffsetTop = rowY;
            row.OffsetBottom = rowY;
        }

        float footerTop = blockCentreY + blockHeight / 2f + focusGrow + ChoiceFooterGap;
        float footerRight = size.X - ChoiceRightMargin;
        float footerLeft = peekShown ? PeekLeft + PeekSize * peekScale + ChoiceSideMargin : ChoiceSideMargin;
        _buttonRows[screenId] = (
            new Vector2((footerLeft + footerRight) / 2f - centre.X, footerTop + footerHeight / 2f - centre.Y),
            footerRight - footerLeft);
        if (screen.GetNodeOrNull<Control>("SkipButton") is { } skip)
        {
            Vector2 skipSize = GrowChoiceButton(skip, canvas);
            PlaceSkip(
                screen,
                new Vector2(footerRight - skipSize.X / 2f - centre.X, footerTop + skipSize.Y / 2f - centre.Y),
                canvas);
        }
        // The choose screen's PeekButton is placed by PlacePeekButton in the clear bottom-left corner, not this footer:
        // at the footer's left it covered the fight's own draw pile whenever the player peeked.
        if (holders.Count == 0) return;
        // A card keeps its slot: the game moves a hovered card to the end of the row, so the row's order is not the cards'.
        if (!_cardOrders.TryGetValue(screenId, out List<ulong>? order)) _cardOrders[screenId] = order = new List<ulong>();
        order.RemoveAll(id => !holders.Any(h => h.GetInstanceId() == id));
        bool fresh = false;
        foreach (Control holder in holders)
        {
            if (order.Contains(holder.GetInstanceId())) continue;
            order.Add(holder.GetInstanceId());
            fresh = true;
        }
        IReadOnlyList<GridSlot> slots = PortraitBetweenMath.RewardSlots(
            order.Count, grid.Columns, ChoiceCardWidth * cardScale, ChoiceCardHeight * cardScale, grid.GapX, ChoiceCardGapY);
        FieldInfo? tweenField = screen.GetType().GetField("_cardTween", InstAll);
        if (fresh)
        {
            // New cards: the game's slide to its single row becomes a slide to the portrait slots, with its fade in.
            if (tweenField?.GetValue(screen) is Tween { } old && old.IsValid()) old.Kill();
            Tween tween = screen.CreateTween().SetParallel(true);
            foreach (Control holder in holders)
            {
                GridSlot slot = slots[order.IndexOf(holder.GetInstanceId())];
                tween.TweenProperty(holder, "position", new Vector2(slot.CenterX / rowScale, slot.CenterY / rowScale), 0.5)
                    .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Expo);
                tween.TweenProperty(holder, "modulate", Colors.White, 1.0).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic);
            }
            if (tweenField != null && tweenField.FieldType.IsAssignableFrom(typeof(Tween))) tweenField.SetValue(screen, tween);
            if (_logged.Add(screenId))
                GD.Print($"Portrait overlay: choose a card laid out, {order.Count} cards at {cardScale:F2} under the banner, " +
                         $"skip right-aligned at y {footerTop:F0}{(peekShown ? ", peek at the left" : "")}.");
            return;
        }
        if (tweenField?.GetValue(screen) is Tween { } running && running.IsValid() && running.IsRunning()) return;
        foreach (Control holder in holders)
        {
            GridSlot slot = slots[order.IndexOf(holder.GetInstanceId())];
            var position = new Vector2(slot.CenterX / rowScale, slot.CenterY / rowScale);
            if (holder.Position.DistanceSquaredTo(position) > 0.25f) holder.Position = position;
        }
    }

    /// <summary>
    /// The peek button in a fight: drawn at 1.6 of its 128 units (the reference's phone size) in the footer row's left end,
    /// clear of Skip at the right. Its control keeps the scene's 128 units; the scale grows its picture and its touch area.
    /// </summary>
    private void PlaceCombatPeek(Control peek, float left, float top, float scale = PeekScale)
    {
        if (!GodotObject.IsInstanceValid(peek)) return;
        Capture(peek);
        var targetScale = new Vector2(scale, scale);
        if (peek.PivotOffset != Vector2.Zero) peek.PivotOffset = Vector2.Zero;
        if (peek.Scale != targetScale) peek.Scale = targetScale;
        if (peek.AnchorLeft != 0f || peek.AnchorRight != 0f || peek.AnchorTop != 0f || peek.AnchorBottom != 0f)
        {
            peek.AnchorLeft = 0f;
            peek.AnchorRight = 0f;
            peek.AnchorTop = 0f;
            peek.AnchorBottom = 0f;
        }
        if (Math.Abs(peek.OffsetLeft - left) < 0.25f && Math.Abs(peek.OffsetTop - top) < 0.25f &&
            Math.Abs(peek.OffsetRight - (left + PeekSize)) < 0.25f && Math.Abs(peek.OffsetBottom - (top + PeekSize)) < 0.25f)
            return;
        peek.OffsetLeft = left;
        peek.OffsetRight = left + PeekSize;
        peek.OffsetTop = top;
        peek.OffsetBottom = top + PeekSize;
        if (_logged.Add(peek.GetInstanceId()))
            GD.Print($"Portrait overlay: peek button of {peek.GetParent()?.GetType().Name} at the footer's left, {PeekScale:F1}x.");
    }

    /// <summary>
    /// Whether the screen's peek button shows (it does in a fight). Before the screen is ready the button still has its scene's
    /// visibility, so the fight is asked.
    /// </summary>
    private static bool PeekShown(Control screen)
    {
        if (screen.GetNodeOrNull<Control>("%PeekButton") is not { } peek) return false;
        if (screen.IsNodeReady()) return peek.Visible;
        try
        {
            Type? manager = typeof(NGame).Assembly.GetType("MegaCrit.Sts2.Core.Combat.CombatManager");
            object? instance = manager?.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
            if (instance != null && manager!.GetProperty("IsInProgress", InstAll)?.GetValue(instance) is bool fighting) return fighting;
        }
        catch (Exception)
        {
            // Not known: keep the button's corner free.
        }
        return true;
    }

    private void RestoreChooseCard(Control screen)
    {
        if (!_cardOrders.TryGetValue(screen.GetInstanceId(), out List<ulong>? order)) return;
        if (screen.GetType().GetField("_cardTween", InstAll)?.GetValue(screen) is Tween { } tween && tween.IsValid()) tween.Kill();
        for (int i = 0; i < order.Count; i++)
        {
            if (GodotObject.InstanceFromId(order[i]) is not Control holder || !GodotObject.IsInstanceValid(holder)) continue;
            // The game's own row.
            holder.Position = new Vector2((i - (order.Count - 1) / 2f) * ChoiceCardRowSpacing, 0f);
            holder.Modulate = Colors.White;
        }
    }

    /// <summary>
    /// The bundle choice's preview holders were drawn at the phone's card size; the desktop layout draws them at their own
    /// (1.0), so the screen's cards come back before the layout's captured places are restored.
    /// </summary>
    private static void RestoreChooseBundle(Control screen)
    {
        if (screen.GetNodeOrNull<Control>("%Cards") is not { } cards) return;
        foreach (Control holder in cards.GetChildren().OfType<Control>())
        {
            MethodInfo? setCardScale = holder.GetType().GetMethod("SetCardScale", InstAll);
            if (setCardScale != null) setCardScale.Invoke(holder, new object[] { Vector2.One });
            else holder.Scale = Vector2.One;
        }
    }

    /// <summary>
    /// The relic choice shows its relics 47 points across in one row in the middle of the screen. In portrait the row is drawn
    /// larger (up to 1.6 times, less when more relics are offered) about the middle, and Skip keeps its distance under it,
    /// where the picked relic's tip opens.
    /// </summary>
    private void LayoutChooseRelic(Control screen, Vector2 canvas)
    {
        if (!PhoneSelectConfirm.Installed || screen.GetNodeOrNull<Control>("RelicRow") is not { } row) return;
        int count = row.GetChildren().OfType<Control>().Count(h => !h.IsQueuedForDeletion());
        if (count == 0) count = CountOf(screen, "_relics");
        if (count <= 0) return;

        Vector2 size = ScreenSize(screen, canvas);
        float drawn = RelicHolderSize * RelicHolderScale;
        float span = (count - 1) * RelicSpacing + drawn;
        float rowScale = Math.Clamp((size.X - 2f * PreviewMargin) / span, 0.6f, RelicMaxRowScale);
        PortraitControlSnapshot snap = CaptureChoice(row);
        var scale = new Vector2(rowScale, rowScale);
        if (row.Scale != scale) row.Scale = scale;
        // The relics' middle is half a holder right of the row's origin: bring it to the middle of the screen.
        float rowX = -RelicHolderSize / 2f * rowScale;
        if (Math.Abs(row.OffsetLeft - rowX) > 0.25f || Math.Abs(row.OffsetRight - rowX) > 0.25f)
        {
            row.OffsetLeft = rowX;
            row.OffsetRight = rowX;
        }
        float relicsBottom = snap.InitialOffsetTop + (RelicHolderSize + drawn) / 2f * rowScale;
        ulong screenId = screen.GetInstanceId();
        _buttonRows[screenId] = (new Vector2(0f, relicsBottom + RelicTipRoom + PortraitBands.MinTarget / 2f), size.X - 2f * ChoiceSideMargin);
        PlaceSkip(screen, _buttonRows[screenId].Centre, canvas);
        if (_logged.Add(screenId))
            GD.Print($"Portrait overlay: relic choice laid out, {count} relics at {rowScale:F2} of the game's size.");
    }

    // ---- The bundle choice (ScrollBoxes) -----------------------------------------------------------------------------------

    /// <summary>
    /// The bundle choice is 1160 wide, so the fallback fit draws it at about 0.93 and its 240-unit pack cards come out 223
    /// wide (the phone's floor is 289); Confirm and Cancel stay desktop-small too. In portrait the packs are laid out on the
    /// screen's own virtual canvas at the card reward's size (the fit stays as it is, so the screen still fills the phone),
    /// with two to a row, the chosen pack's cards in one row under the banner, and Peek, Cancel and Confirm in a footer row.
    /// All of it is captured first, so turning Portrait Mode off restores the desktop layout.
    /// </summary>
    private void LayoutChooseBundle(Control screen, Vector2 canvas)
    {
        if (screen.GetNodeOrNull<Control>("%BundleRow") is not { } row || screen.GetNodeOrNull<Control>("%BundlePreviewContainer") is not { } preview)
            return;
        Vector2 size = ScreenSize(screen, canvas);
        if (size.X <= 1f || size.Y <= 1f) return;
        // The screen is fitted by its width: its own units are this many canvas units. Constants that the launcher states
        // in canvas units are converted before they are used on it.
        float screenScale = Math.Max(canvas.X / size.X, 0.01f);
        float Local(float units) => units / screenScale;
        float centreY = size.Y / 2f;

        // The banner rises under the relic strip, as on the other choice screens, measured from this screen's canvas top.
        float canvasTop = screen.GetGlobalTransformWithCanvas().Origin.Y;
        float capHeight = PortraitLayout.ComputeCapHeight(screen.GetTree().Root);
        float bannerTopLocal = (capHeight + PortraitBands.RunContentTop + PortraitBetweenLayout.BannerContentGap - canvasTop) / screenScale;
        float lift = Math.Max(0f, centreY - PortraitBetweenLayout.BannerTopOffset - bannerTopLocal);
        PortraitBetweenLayout.LiftBanner(screen.GetNodeOrNull<Control>("Banner"), size, lift, _liftedChoiceBanners);

        float side = Local(ChoiceCardSideMargin);
        float gapX = Local(BundleGapX);
        float gapY = Local(ChoiceCardGapY);
        float footerHeight = Local(PortraitBands.MinTarget);
        float footerTop = size.Y - Local(ChoiceBottomMargin) - footerHeight;
        float bannerBottom = centreY - PortraitBetweenLayout.BannerBottomOffset - lift;
        float bandTop = bannerBottom + Local(ChoiceBannerGap);
        float bandBottom = footerTop - Local(ChoiceFooterGap);

        // The packs, two to a row (or one pack, centred), each at the card reward's drawn size.
        List<Control> bundles = row.GetChildren().OfType<Control>()
            .Where(child => !child.IsQueuedForDeletion() && child.GetType().Name == "NCardBundle").ToList();
        int bundleCount = bundles.Count > 0 ? bundles.Count : CountOf(screen, "_bundles");
        if (bundleCount > 0)
        {
            CardGridFit grid = PortraitBetweenMath.CardGrid(
                bundleCount, size.X, bandTop, bandBottom, BundleFanWidth, BundleFanHeight,
                gapX, gapY, ChoiceMaxCardScale, side, BundleCardFloorScale);
            float scale = grid.Scale;
            float focusGrow = PortraitBetweenMath.CardFocusGrowth * BundleFanHeight * scale;
            float blockTop = bandTop + focusGrow +
                             Math.Max(0f, (bandBottom - bandTop - 2f * focusGrow - grid.BlockHeight) / 2f);
            float blockCentreY = blockTop + grid.BlockHeight / 2f;

            CaptureChoice(row);
            if (row.Scale != Vector2.One) row.Scale = Vector2.One;
            float rowY = blockCentreY - centreY;
            if (Math.Abs(row.OffsetTop - rowY) > 0.25f || Math.Abs(row.OffsetBottom - rowY) > 0.25f)
            {
                row.OffsetTop = rowY;
                row.OffsetBottom = rowY;
            }
            if (Math.Abs(row.OffsetLeft) > 0.25f || Math.Abs(row.OffsetRight) > 0.25f)
            {
                row.OffsetLeft = 0f;
                row.OffsetRight = 0f;
            }

            IReadOnlyList<GridSlot> slots = PortraitBetweenMath.RewardSlots(
                bundleCount, grid.Columns, BundleFanWidth * scale, BundleFanHeight * scale, gapX, gapY);
            for (int i = 0; i < bundles.Count && i < slots.Count; i++)
            {
                Control bundle = bundles[i];
                CaptureChoice(bundle);
                var bundleScale = new Vector2(scale, scale);
                if (bundle.Scale != bundleScale) bundle.Scale = bundleScale;
                // The pack's touch box is centred just off its fan's middle; place that centre on the slot's.
                var position = new Vector2(
                    slots[i].CenterX - scale * BundleHitboxCentreX,
                    slots[i].CenterY - scale * BundleHitboxCentreY);
                if (bundle.Position.DistanceSquaredTo(position) > 0.25f) bundle.Position = position;
            }
            if (_logged.Add(screen.GetInstanceId()))
                GD.Print($"Portrait overlay: bundle choice laid out, {bundleCount} packs at {scale:F2}, cards " +
                         $"{BundleCardWidth * scale:F0} wide.");
        }

        // The chosen pack's cards: one row under the banner, at the card reward's size, the screen's slide taken over.
        if (preview.Visible && screen.GetNodeOrNull<Control>("%Cards") is { } previewCards)
        {
            List<Control> holders = previewCards.GetChildren().OfType<Control>()
                .Where(holder => !holder.IsQueuedForDeletion()).ToList();
            if (holders.Count > 0)
            {
                if (screen.GetType().GetField("_cardTween", InstAll)?.GetValue(screen) is Tween { } tween &&
                    tween.IsValid() && tween.IsRunning())
                    tween.Kill();
                int columns = Math.Min(holders.Count, 4);
                int rows = (holders.Count + columns - 1) / columns;
                float byWidth = (size.X - 2f * side - (columns - 1) * gapX) / (columns * BundleCardWidth);
                float byHeight = (bandBottom - bandTop - (rows - 1) * gapY) /
                                 (rows * BundleCardHeight * (1f + 2f * PortraitBetweenMath.CardFocusGrowth));
                float cardScale = Math.Clamp(Math.Min(BundlePreviewMaxScale, Math.Min(byWidth, byHeight)),
                    0.2f, BundlePreviewMaxScale);
                float focusGrow = PortraitBetweenMath.CardFocusGrowth * BundleCardHeight * cardScale;
                float blockHeight = rows * BundleCardHeight * cardScale + (rows - 1) * gapY;
                float blockTop = bandTop + focusGrow +
                                 Math.Max(0f, (bandBottom - bandTop - 2f * focusGrow - blockHeight) / 2f);
                float blockCentreY = blockTop + blockHeight / 2f;

                CaptureChoice(previewCards);
                float cardsY = blockCentreY - centreY;
                if (Math.Abs(previewCards.OffsetTop - cardsY) > 0.25f || Math.Abs(previewCards.OffsetBottom - cardsY) > 0.25f)
                {
                    previewCards.OffsetTop = cardsY;
                    previewCards.OffsetBottom = cardsY;
                }
                if (Math.Abs(previewCards.OffsetLeft) > 0.25f || Math.Abs(previewCards.OffsetRight) > 0.25f)
                {
                    previewCards.OffsetLeft = 0f;
                    previewCards.OffsetRight = 0f;
                }

                IReadOnlyList<GridSlot> slots = PortraitBetweenMath.RewardSlots(
                    holders.Count, columns, BundleCardWidth * cardScale, BundleCardHeight * cardScale, gapX, gapY);
                var cardHolderScale = new Vector2(cardScale, cardScale);
                MethodInfo? setCardScale = null;
                for (int i = 0; i < holders.Count && i < slots.Count; i++)
                {
                    Control holder = holders[i];
                    CaptureChoice(holder);
                    // SetCardScale keeps the holder's own hover/small scale in step: a focus grows it only a tenth.
                    setCardScale ??= holder.GetType().GetMethod("SetCardScale", InstAll);
                    if (setCardScale != null) setCardScale.Invoke(holder, new object[] { cardHolderScale });
                    else if (holder.Scale != cardHolderScale) holder.Scale = cardHolderScale;
                    var position = new Vector2(slots[i].CenterX, slots[i].CenterY);
                    if (holder.Position.DistanceSquaredTo(position) > 0.25f) holder.Position = position;
                }
                if (_logged.Add(previewCards.GetInstanceId()))
                    GD.Print($"Portrait overlay: bundle preview cards laid out, {holders.Count} cards at {cardScale:F2}.");
            }
        }

        // The footer: Peek at the left, Skip (or the preview's Cancel and Confirm) right-aligned and grown to a finger.
        if (screen.GetNodeOrNull<Control>("%SkipButton") is { } skip && skip.Visible)
        {
            Vector2 skipSize = GrowChoiceButton(skip, canvas);
            PlaceSkip(screen, new Vector2(
                size.X - Local(ChoiceRightMargin) - skipSize.X / 2f - size.X / 2f,
                footerTop + skipSize.Y / 2f - centreY), canvas);
        }
        if (screen.GetNodeOrNull<Control>("%PeekButton") is { } peek)
            PlaceCombatPeek(peek, PeekLeft, footerTop, Math.Min(PeekScale, PortraitBands.MinTarget / (PeekSize * screenScale)));
        float rightEdge = size.X - Local(ChoiceRightMargin);
        if (screen.GetNodeOrNull<Control>("%Confirm") is { } confirm)
        {
            Vector2 confirmSize = PlaceBundleButton(confirm, rightEdge, footerTop, footerHeight);
            rightEdge -= confirmSize.X + Local(BundleFooterGap);
        }
        if (screen.GetNodeOrNull<Control>("%Cancel") is { } cancel)
            PlaceBundleButton(cancel, rightEdge, footerTop, footerHeight);
    }

    /// <summary>
    /// A preview's Confirm or Cancel: its desktop picture stays, placed in the portrait footer row, with a touch area of 44
    /// points added around it where the picture is smaller. Its place is captured first for the landscape restore. Returns the
    /// control's own width (the touch area can reach past it).
    /// </summary>
    private Vector2 PlaceBundleButton(Control button, float right, float footerTop, float footerHeight)
    {
        ulong id = button.GetInstanceId();
        if (!_bundleButtonSizes.TryGetValue(id, out Vector2 native))
        {
            native = button.Size;
            if (native.X <= 1f || native.Y <= 1f) return native;
            _bundleButtonSizes[id] = native;
        }
        CaptureChoice(button);
        float top = footerTop + (footerHeight - native.Y) / 2f;
        float left = right - native.X;
        if (button.AnchorLeft != 0f || button.AnchorTop != 0f || button.AnchorRight != 0f || button.AnchorBottom != 0f ||
            Math.Abs(button.OffsetLeft - left) > 0.25f || Math.Abs(button.OffsetTop - top) > 0.25f ||
            Math.Abs(button.OffsetRight - right) > 0.25f || Math.Abs(button.OffsetBottom - (top + native.Y)) > 0.25f)
        {
            button.AnchorLeft = 0f;
            button.AnchorTop = 0f;
            button.AnchorRight = 0f;
            button.AnchorBottom = 0f;
            button.OffsetLeft = left;
            button.OffsetTop = top;
            button.OffsetRight = right;
            button.OffsetBottom = top + native.Y;
        }
        GrowGridConfirm(button);
        return native;
    }

    // ---- Potion popup -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A potion's popup (Use and Discard) opens from its slot. In portrait the slots sit in a scrolling row inside the stone
    /// header, so the popup would be clipped with them: it becomes top level (its own draw and input root, still a child of
    /// its holder for the game's cleanup and focus), drops below the relic band, is centred on the visible part of its holder
    /// and kept inside the safe frame, drawn large enough for the buttons to be 44 points. A swipe of the potion row, which
    /// moves the slot under the popup, takes it down through the game's own close. A holder outside that row keeps the
    /// original behaviour: a larger popup just under the slot, moved inside the screen.
    /// </summary>
    private void FitPotionPopup(Control popup, Vector2 canvas)
    {
        ulong id = popup.GetInstanceId();
        if (_fittedPopups.ContainsKey(id) || !popup.IsNodeReady()) return;
        Control? parent = popup.GetParent() as Control;
        if (parent == null) return;
        ScrollContainer? row = null;
        for (Node? node = parent; node != null; node = node.GetParent())
        {
            if (node is ScrollContainer clip && clip.Name == PortraitRunHeader.PotionRowScrollName)
            {
                row = clip;
                break;
            }
        }
        if (row == null)
        {
            FitPotionPopupBesideHolder(popup, parent, canvas);
            return;
        }

        float capHeight = PortraitLayout.ComputeCapHeight(popup.GetTree().Root);
        Rect2 holder = parent.GetGlobalRect();
        Rect2 visible = holder.Intersection(row.GetGlobalRect());
        float anchorX = (visible.HasArea() ? visible : holder).GetCenter().X;
        float buttonHeight = popup.GetNodeOrNull<Control>("%UseButton") is { Size.Y: > 1f } use ? use.Size.Y : 86f;
        PopupPlacement placement = PortraitHeaderMath.PotionPopup(EdgeMargin, capHeight, canvas.X - EdgeMargin, canvas.Y - EdgeMargin,
            anchorX, PortraitBands.RelicBandBottom(capHeight), popup.Size.X, popup.Size.Y,
            PortraitBands.MinTarget / Math.Max(buttonHeight, 1f), PotionPopupMaxScale, PotionPopupGap + 12f);
        if (!placement.Visible)
        {
            FitPotionPopupBesideHolder(popup, parent, canvas);
            return;
        }

        _fittedPopups[id] = (popup.Position, popup.Scale, popup.TopLevel);
        popup.TopLevel = true;
        popup.PivotOffset = Vector2.Zero;
        popup.Scale = new Vector2(placement.Scale, placement.Scale);
        popup.GlobalPosition = new Vector2(placement.X, placement.Y);
        PlacePopupTips(popup);
        DismissPopupOnScroll(popup, row);
        GD.Print($"Portrait overlay: potion popup drawn at {placement.Scale:F2} below the relic band at " +
            $"({placement.X:F0},{placement.Y:F0}).");
    }

    /// <summary>A scrolling potion row moves the slot the popup belongs to: the game's own close takes the popup down.</summary>
    private static void DismissPopupOnScroll(Control popup, ScrollContainer row)
    {
        ScrollBar bar = row.GetHScrollBar();
        void OnScroll(double _)
        {
            if (GodotObject.IsInstanceValid(popup) && popup.IsInsideTree()) popup.Call("Remove");
        }
        bar.ValueChanged += OnScroll;
        popup.TreeExiting += () =>
        {
            if (GodotObject.IsInstanceValid(bar)) bar.ValueChanged -= OnScroll;
        };
    }

    /// <summary>
    /// The open potion popup's Use or Discard button under this canvas point, if any. The popup is top level so it can be
    /// drawn past the scrolling potion row; Godot's GUI picking skips top-level children (`Viewport::_gui_find_control_at_pos`),
    /// so its buttons never receive the press. The header input forwards the press and release to the button it lands on.
    /// </summary>
    internal static Control? PotionActionAt(Vector2 point)
    {
        if (Instance is not { } layout || !SafeAreaCanvas.IsPortrait) return null;
        foreach (ulong id in layout._potionPopups)
        {
            if (Live(id) is not Control popup || popup.GetType().FullName != PotionPopupTypeName) continue;
            foreach (string name in new[] { "%UseButton", "%DiscardButton" })
            {
                if (popup.GetNodeOrNull<Control>(name) is { } button && button.IsVisibleInTree() &&
                    button.GetGlobalRect().Grow(2f).HasPoint(point))
                    return button;
            }
        }
        return null;
    }

    /// <summary>The original portrait fit: a larger popup just under the slot, moved inside the screen.</summary>
    private void FitPotionPopupBesideHolder(Control popup, Control parent, Vector2 canvas)
    {
        ulong id = popup.GetInstanceId();
        Vector2 parentScale = parent.GetGlobalTransformWithCanvas().Scale;
        float buttonHeight = popup.GetNodeOrNull<Control>("%UseButton") is { Size.Y: > 1f } use ? use.Size.Y : 86f;
        float grow = Math.Clamp(PortraitBands.MinTarget / (buttonHeight * Math.Max(parentScale.Y, 0.01f)), 1f, PotionPopupMaxScale);
        _fittedPopups[id] = (popup.Position, popup.Scale, popup.TopLevel);

        popup.Scale = new Vector2(grow, grow);
        if (parent is { Size.Y: > 1f }) popup.Position = new Vector2(popup.Position.X, parent.Size.Y + PotionPopupGap);
        Rect2 rect = popup.GetGlobalRect();
        float dx = 0f;
        if (rect.End.X > canvas.X - EdgeMargin) dx = canvas.X - EdgeMargin - rect.End.X;
        if (rect.Position.X + dx < EdgeMargin) dx = EdgeMargin - rect.Position.X;
        if (Math.Abs(dx) > 0.5f) popup.Position += new Vector2(dx / Math.Max(parentScale.X, 0.01f), 0f);
        PlacePopupTips(popup);
        GD.Print($"Portrait overlay: potion popup drawn at {grow:F2} and moved {dx:F0} to stay inside the screen.");
    }

    /// <summary>The potion's tip set beside the popup as it now stands, by the game's own placement.</summary>
    private static void PlacePopupTips(Control popup)
    {
        try
        {
            if (popup.GetNodeOrNull<Control>("%HoverTipBounds") is not { } bounds || !bounds.IsInsideTree()) return;
            PropertyInfo? instance = typeof(NGame).GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (instance?.GetValue(null) is not Node game || !GodotObject.IsInstanceValid(game)) return;
            if (typeof(NGame).GetProperty("HoverTipsContainer", InstAll)?.GetValue(game) is not Node container) return;
            foreach (Node set in container.GetChildren())
            {
                Type type = set.GetType();
                if (set.IsQueuedForDeletion() || !ReferenceEquals(type.GetField("_owner", InstAll)?.GetValue(set), bounds)) continue;
                MethodInfo? align = type.GetMethods(InstAll).FirstOrDefault(m => m.Name == "SetAlignment" &&
                    m.GetParameters() is { Length: 2 } p && p[0].ParameterType == typeof(Rect2) && p[1].ParameterType.IsEnum);
                if (align == null) return;
                Transform2D xform = bounds.GetGlobalTransform();
                align.Invoke(set, [new Rect2(xform.Origin, bounds.Size * xform.Scale), Enum.Parse(align.GetParameters()[1].ParameterType, "Right")]);
                return;
            }
        }
        catch (Exception)
        {
            // The tip stays where the game put it.
        }
    }

    // ---- The inspect screen's upgrade tickbox and the card grids' confirm button ----------------------------------------

    /// <summary>
    /// The inspect screen's upgrade tickbox is 22 points tall. This is the deck/grid rule: the row is made 44 points tall
    /// (the picture stays as it is) and the scene's own centred offset is raised ten units, so it sits just under the card.
    /// <see cref="LayoutInspectUpgrade"/> applies the same rule to every inspector, including the in-hand hold, and wins
    /// over this pass on the drawn frame.
    /// </summary>
    private void GrowInspectTickbox(Control tickbox)
    {
        // The combat hold-inspect marks its box; the shared inspect layout places it on the same rule, so skip the duplicate.
        if (tickbox.HasMeta(PhoneTouchCards.CombatInspectMeta)) return;
        PortraitControlSnapshot snap = CaptureChoice(tickbox);
        float parentScale = tickbox.GetParent() is Control parent ? Math.Max(parent.GetGlobalTransformWithCanvas().Scale.Y, 0.01f) : 1f;
        float height = MathF.Round(PortraitBands.MinTarget / parentScale);
        if (height <= snap.InitialOffsetBottom - snap.InitialOffsetTop) return;
        var minimum = new Vector2(snap.InitialCustomMinimumSize.X, height);
        if (tickbox.CustomMinimumSize != minimum) tickbox.CustomMinimumSize = minimum;
        float top = snap.InitialOffsetTop - InspectTickboxRaise;
        if (Math.Abs(tickbox.OffsetTop - top) > 0.25f || Math.Abs(tickbox.OffsetBottom - (top + height)) > 0.25f)
        {
            tickbox.OffsetTop = top;
            tickbox.OffsetBottom = top + height;
        }
        if (_logged.Add(tickbox.GetInstanceId())) GD.Print($"Portrait overlay: the inspect screen's upgrade tickbox is {height:F0} tall.");
    }

    /// <summary>
    /// The confirm button of the card selection screens is 38 points tall and slides in and out by its position, which the game
    /// works out from its size: its touch area grows above and below it instead (<see cref="PhoneHitArea"/>).
    /// </summary>
    private void GrowGridConfirm(Control confirm)
    {
        if (confirm.Size.Y < 1f) return;
        float parentScale = confirm.GetParent() is Control parent ? Math.Max(parent.GetGlobalTransformWithCanvas().Scale.Y, 0.01f) : 1f;
        float pad = (PortraitBands.MinTarget / parentScale - confirm.Size.Y) / 2f;
        if (pad < 0.5f) return;
        ulong id = confirm.GetInstanceId();
        if (_confirmPads.TryGetValue(id, out float applied) && Math.Abs(applied - pad) < 0.5f) return;
        _confirmPads[id] = pad;
        PhoneHitArea.Grow(confirm, 0f, pad, 0f, pad);
        if (_logged.Add(id)) GD.Print($"Portrait overlay: confirm button of {confirm.GetParent()?.GetType().Name} takes touches {pad:F0} above and below it.");
    }


    // ---- A choice screen's own combat piles (Peek) ------------------------------------------------------------------

    /// <summary>
    /// The choose-a-card, simple-select and combat-pile grids carry a private copy of the fight's three piles, shown only
    /// while the player peeks. Native leaves them at the desktop corners, so in portrait the player saw the draw, discard
    /// and exhaust piles jump back to their old places and the eye button cover the moved draw pile. Every copy is put on
    /// the same portrait rail as the fight's own piles, before its first visible frame; the eye buttons then sit clear of
    /// them (PlacePeekButton/PlaceHandPeek above).
    /// </summary>
    private void LayoutChoicePiles(Vector2 canvas)
    {
        if (_choicePiles.Count == 0) return;
        PortraitCombatBands bands = PortraitCombatMath.ComputeCombatBands(canvas.Y, 0f);
        PortraitHudLayout hud = PortraitCombatMath.ComputeHudLayout(canvas.Y, bands);
        foreach ((ulong screenId, ulong pilesId) in _choicePiles)
        {
            if (GodotObject.InstanceFromId(screenId) is not Control screen || !GodotObject.IsInstanceValid(screen) ||
                !screen.IsInsideTree())
                continue;
            if (GodotObject.InstanceFromId(pilesId) is not Control piles || !GodotObject.IsInstanceValid(piles) ||
                !piles.IsInsideTree())
                continue;
            Place(piles, "%DrawPile", "DrawPile", new Vector2(hud.DrawShowX, hud.DrawShowY),
                new Vector2(hud.DrawHideX, hud.DrawHideY));
            Place(piles, "%DiscardPile", "DiscardPile", new Vector2(hud.DiscardShowX, hud.DiscardShowY),
                new Vector2(hud.DiscardHideX, hud.DiscardHideY));
            Place(piles, "%ExhaustPile", "ExhaustPile", new Vector2(hud.ExhaustShowX, hud.ExhaustShowY),
                new Vector2(hud.ExhaustHideX, hud.ExhaustShowY));

            void Place(Control container, string unique, string name, Vector2 show, Vector2 hide)
            {
                if ((container.GetNodeOrNull<Control>(unique) ?? container.GetNodeOrNull<Control>(name)) is not { } pile ||
                    !GodotObject.IsInstanceValid(pile))
                    return;
                ulong id = pile.GetInstanceId();
                if (!_choicePileStates.ContainsKey(id))
                    _choicePileStates[id] = PortraitCombatRail.CaptureChoicePile(pile);
                PortraitCombatRail.PlaceChoicePile(pile, show, hide, hud.PileScale);
                if (_logged.Add(id))
                    GD.Print($"Portrait overlay: {screen.GetType().Name}'s {name} on the portrait rail at {show}.");
            }
        }
    }

    private void RestoreChoicePiles()
    {
        foreach ((ulong id, PortraitCombatRail.ChoicePileDesktop desktop) in _choicePileStates)
        {
            if (GodotObject.InstanceFromId(id) is Control pile && GodotObject.IsInstanceValid(pile))
                PortraitCombatRail.RestoreChoicePile(pile, desktop);
        }
        _choicePileStates.Clear();
    }

    // ---- The card inspect screen: tips, previews and the View Upgrades box ------------------------------------------

    private const string TipSetTypeName = "MegaCrit.Sts2.Core.Nodes.HoverTips.NHoverTipSet";
    private static Type? _tipSetType;
    private static FieldInfo? _tipOwnerField, _tipTextField, _tipCardsField;
    private static PropertyInfo? _gameInstanceProperty, _hoverTipsContainerProperty;
    private static bool _tipReflectionResolved, _tipReflectionOk;

    /// <summary>
    /// Tracks a card inspect screen and captures its native View Upgrades box before the game or the combat hold touches
    /// it. The screen is reused for every inspection, so this is the one moment the scene's own offsets are known.
    /// </summary>
    private void RememberInspectScreen(Control screen)
    {
        ulong id = screen.GetInstanceId();
        if (!_inspectScreens.Add(id)) return;
        if (screen.GetNodeOrNull<Control>("%Upgrade") is { } upgrade)
            _inspectUpgrades[id] = InspectUpgradeState.Capture(upgrade);
        if (_logged.Add(id)) GD.Print($"Portrait overlay: inspect card screen tracked ({screen.GetType().Name}).");
    }

    /// <summary>
    /// The inspect screen's own composition in portrait: the card keeps its centre and size, keyword tips are packed into
    /// rows above it (or below when above cannot hold them at a readable scale), card previews stack in the free column
    /// beside it, and the View Upgrades box is centred under the card exactly as on the deck and card-grid inspectors.
    /// Every block is kept clear of the card; this runs after the game's own tip pass and before the frame is drawn, so
    /// neither the game nor PhoneHoverTips can put a tip or preview back over the card.
    /// </summary>
    private void LayoutInspectTips(Control screen)
    {
        if (!TryInspectCardRect(screen, out InspectCardMath.Rect card)) return;
        if (!TryFindTipColumns(screen, out Control? text, out Control? cards)) return;
        if (!TryCollectTipSizes(text, out List<InspectCardMath.Size> textSizes)) return;
        if (!TryCollectTipSizes(cards, out List<InspectCardMath.Size> cardSizes)) return;
        if (textSizes.Count == 0 && cardSizes.Count == 0) return;

        _inspectUpgrades.TryGetValue(screen.GetInstanceId(), out InspectUpgradeState? state);
        Control? upgrade = state != null && GodotObject.InstanceFromId(state.Id) is Control liveUpgrade &&
                           GodotObject.IsInstanceValid(liveUpgrade) && liveUpgrade.IsVisibleInTree()
            ? liveUpgrade
            : null;
        Vector2 canvas = screen.GetViewport()?.GetVisibleRect().Size ?? CanvasOf(screen);
        InspectCardMath.Rect upgradeRect = default;
        if (upgrade != null)
        {
            Rect2 drawn = upgrade.GetGlobalRect();
            if (drawn.Size.X >= 1f && drawn.Size.Y >= 1f)
                upgradeRect = new InspectCardMath.Rect(drawn.Position.X, drawn.Position.Y, drawn.Size.X, drawn.Size.Y);
        }
        InspectCardMath.Layout layout = InspectCardMath.Compute(new InspectCardMath.Request(
            CanvasWidth: canvas.X,
            CanvasHeight: canvas.Y,
            CardCentreX: (card.X + card.Width * 0.5f),
            CardCentreY: (card.Y + card.Height * 0.5f),
            CardScale: card.Width / InspectCardMath.CardWidth,
            TextTips: textSizes,
            CardPreviews: cardSizes,
            Upgrade: upgradeRect,
            PreferredScale: 1.4f,
            MinScale: PhoneHoverTips.MinFitScale,
            LeftArrow: ArrowRect(screen, "LeftArrow"),
            RightArrow: ArrowRect(screen, "RightArrow")));

        ApplyTipBlock(text, layout.Text);
        ApplyTipBlock(cards, layout.Cards);
        _inspectArranged.Add(screen.GetInstanceId());
        if (_logged.Add(screen.GetInstanceId()))
            GD.Print($"Portrait overlay: inspect card tips {Describe(layout.Text)} previews {Describe(layout.Cards)} " +
                     $"card={card.X:F0},{card.Y:F0} {card.Width:F0}x{card.Height:F0}.");

        static string Describe(InspectCardMath.Block? block) => block is { } b
            ? $"{b.Rect.X:F0},{b.Rect.Y:F0} {b.Rect.Width:F0}x{b.Rect.Height:F0}@{b.Scale:F2}"
            : "none";
    }

    /// <summary>
    /// The inspect screen's View Upgrades box: exactly the deck/grid inspector's row. That layout makes it 44 points tall
    /// and raises the scene's own centred offset by ten units (<see cref="GrowInspectTickbox"/>); the same native offset,
    /// centred on the screen and with the same 44-point height, is applied here to the in-hand hold as well.
    /// </summary>
    private void LayoutInspectUpgrade(Control screen, Vector2 canvas)
    {
        ulong screenId = screen.GetInstanceId();
        if (!_inspectUpgrades.TryGetValue(screenId, out InspectUpgradeState? state))
        {
            if (screen.GetNodeOrNull<Control>("%Upgrade") is not { } found) return;
            _inspectUpgrades[screenId] = state = InspectUpgradeState.Capture(found);
        }
        if (GodotObject.InstanceFromId(state.Id) is not Control upgrade || !GodotObject.IsInstanceValid(upgrade) ||
            !upgrade.IsInsideTree())
            return;

        Vector2 screenScale = screen.GetGlobalTransformWithCanvas().Scale.Abs();
        // The HBox grows to its content (the icon and label are wider than the scene's offset box); take that width so
        // setting the size cannot grow the box off centre.
        float width = Math.Max(state.Width, upgrade.GetCombinedMinimumSize().X);
        float localHeight = Math.Max(state.Height, PortraitBands.MinTarget / Math.Max(screenScale.Y, 0.01f));
        var localSize = new Vector2(width, localHeight);
        var local = new Vector2(
            screen.Size.X * 0.5f - width * 0.5f,
            screen.Size.Y * 0.5f + state.OffsetTop - InspectTickboxRaise);

        if (upgrade.AnchorLeft != 0f || upgrade.AnchorTop != 0f || upgrade.AnchorRight != 0f || upgrade.AnchorBottom != 0f)
        {
            upgrade.AnchorLeft = 0f;
            upgrade.AnchorTop = 0f;
            upgrade.AnchorRight = 0f;
            upgrade.AnchorBottom = 0f;
        }
        if (upgrade.PivotOffset != Vector2.Zero) upgrade.PivotOffset = Vector2.Zero;
        if (upgrade.Scale != Vector2.One) upgrade.Scale = Vector2.One;
        if (upgrade.CustomMinimumSize != localSize) upgrade.CustomMinimumSize = localSize;
        if (upgrade.Size.DistanceSquaredTo(localSize) > 0.25f) upgrade.Size = localSize;
        if (upgrade.Position.DistanceSquaredTo(local) > 0.25f) upgrade.Position = local;
    }


    private void BindInspectFrame()
    {
        if (_inspectFrameBound) return;
        _inspectFrameBound = true;
        RenderingServer.FramePreDraw += LayoutInspectFrame;
    }

    private void UnbindInspectFrame()
    {
        if (!_inspectFrameBound) return;
        _inspectFrameBound = false;
        RenderingServer.FramePreDraw -= LayoutInspectFrame;
    }

    /// <summary>
    /// The tip placement pass. It runs from FramePreDraw so it is after the game's own tip process and after
    /// PhoneHoverTips' fit; that pass subscribed on its first frame, long before any inspect screen exists.
    /// </summary>
    private void LayoutInspectFrame()
    {
        if (!SafeAreaCanvas.IsPortrait || _inspectScreens.Count == 0) return;
        using (PortraitFrameTiming.Begin(PortraitFrameTiming.Hover))
        {
            try
            {
                foreach (ulong id in _inspectScreens)
                    if (Live(id) is { } screen && screen.IsVisibleInTree()) LayoutInspectTips(screen);
            }
            catch (Exception error)
            {
                if (++_inspectErrors <= 3)
                    GD.PrintErr("Portrait overlay: an inspect screen's tips were left as the game placed them: ", error);
                if (_inspectErrors >= 30) UnbindInspectFrame();
            }
        }
    }

    private int _inspectErrors;

    private static bool EnsureTipReflection()
    {
        if (_tipReflectionResolved) return _tipReflectionOk;
        _tipReflectionResolved = true;
        const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        _tipSetType = typeof(NGame).Assembly.GetType(TipSetTypeName);
        _tipOwnerField = _tipSetType?.GetField("_owner", inst);
        _tipTextField = _tipSetType?.GetField("_textHoverTipContainer", inst);
        _tipCardsField = _tipSetType?.GetField("_cardHoverTipContainer", inst);
        _gameInstanceProperty = typeof(NGame).GetProperty("Instance", stat);
        _hoverTipsContainerProperty = typeof(NGame).GetProperty("HoverTipsContainer", inst);
        _tipReflectionOk = _tipSetType != null && _tipOwnerField != null && _tipTextField != null &&
                           _tipCardsField != null && _gameInstanceProperty != null && _hoverTipsContainerProperty != null;
        if (!_tipReflectionOk)
            GD.PrintErr("Portrait overlay: the game's tip set no longer has the expected members; inspect tips stay as placed.");
        return _tipReflectionOk;
    }

    private static bool TryFindTipColumns(Control screen, out Control? text, out Control? cards)
    {
        text = null;
        cards = null;
        if (!EnsureTipReflection()) return false;
        if (_gameInstanceProperty!.GetValue(null) is not Node game || !GodotObject.IsInstanceValid(game)) return false;
        if (_hoverTipsContainerProperty!.GetValue(game) is not Node container || !GodotObject.IsInstanceValid(container))
            return false;
        foreach (Node child in container.GetChildren())
        {
            if (child is not Control set || child.GetType().FullName != TipSetTypeName || !set.Visible ||
                set.IsQueuedForDeletion())
                continue;
            if (!ReferenceEquals(_tipOwnerField!.GetValue(set), screen)) continue;
            text = _tipTextField!.GetValue(set) as Control;
            cards = _tipCardsField!.GetValue(set) as Control;
            return text != null || cards != null;
        }
        return false;
    }

    private static bool TryCollectTipSizes(Control? column, out List<InspectCardMath.Size> sizes)
    {
        sizes = new List<InspectCardMath.Size>();
        if (column == null) return true;
        if (!GodotObject.IsInstanceValid(column) || !column.IsVisibleInTree()) return true;
        foreach (Node child in column.GetChildren())
        {
            if (child is not Control tip || !tip.Visible || tip.IsQueuedForDeletion()) continue;
            Vector2 size = tip.Size;
            if (size.X < 2f || size.Y < 2f) size = tip.GetCombinedMinimumSize();
            if (size.X < 2f || size.Y < 2f) return false;
            sizes.Add(new InspectCardMath.Size(size.X, size.Y));
        }
        return true;
    }

    private static void ApplyTipBlock(Control? column, InspectCardMath.Block? block)
    {
        if (column == null || !GodotObject.IsInstanceValid(column) || !column.IsInsideTree()) return;
        if (block is not { } placed || placed.Rect.Width < 2f || placed.Rect.Height < 2f || placed.Scale <= 0.01f) return;
        var scale = new Vector2(placed.Scale, placed.Scale);
        if (column.PivotOffset != Vector2.Zero) column.PivotOffset = Vector2.Zero;
        if (!column.Scale.IsEqualApprox(scale)) column.Scale = scale;
        var localSize = new Vector2(placed.Rect.Width / placed.Scale, placed.Rect.Height / placed.Scale);
        if (!column.Size.IsEqualApprox(localSize)) column.Size = localSize;
        var origin = new Vector2(placed.Rect.X, placed.Rect.Y);
        if (column.GlobalPosition.DistanceSquaredTo(origin) > 0.25f) column.GlobalPosition = origin;
        int index = 0;
        foreach (Node child in column.GetChildren())
        {
            if (child is not Control tip || !tip.Visible || tip.IsQueuedForDeletion()) continue;
            if (index >= placed.Offsets.Count) break;
            InspectCardMath.Point offset = placed.Offsets[index++];
            var local = new Vector2(offset.X, offset.Y);
            if (tip.Position.DistanceSquaredTo(local) > 0.25f) tip.Position = local;
        }
    }

    private static InspectCardMath.Rect? ArrowRect(Control screen, string name)
    {
        if (screen.GetNodeOrNull<Control>(name) is not { } arrow || !GodotObject.IsInstanceValid(arrow) ||
            !arrow.IsVisibleInTree() || arrow.Modulate.A < 0.05f)
            return null;
        Rect2 rect = arrow.GetGlobalRect();
        return new InspectCardMath.Rect(rect.Position.X, rect.Position.Y, rect.Size.X, rect.Size.Y);
    }

    /// <summary>The drawn card: an NCard root is a point at the card's centre and its visual size is 300x422 at its scale.</summary>
    private static bool TryInspectCardRect(Control screen, out InspectCardMath.Rect rect)
    {
        rect = default;
        if (screen.GetNodeOrNull<Control>("Card") is not { } card || !GodotObject.IsInstanceValid(card) ||
            !card.IsVisibleInTree())
            return false;
        Vector2 size = new Vector2(InspectCardMath.CardWidth, InspectCardMath.CardHeight) *
                       card.GetGlobalTransformWithCanvas().Scale.Abs();
        if (size.X < 10f || size.Y < 10f) return false;
        Vector2 centre = card.GlobalPosition;
        rect = new InspectCardMath.Rect(centre.X - size.X * 0.5f, centre.Y - size.Y * 0.5f, size.X, size.Y);
        return true;
    }

    /// <summary>Puts the tips back the way the game lays them out when Portrait Mode is turned off mid-inspection.</summary>
    private void RestoreInspectTips()
    {
        UnbindInspectFrame();
        if (_inspectArranged.Count == 0) return;
        foreach (ulong id in _inspectArranged)
        {
            if (GodotObject.InstanceFromId(id) is not Control screen || !GodotObject.IsInstanceValid(screen) ||
                !screen.IsVisibleInTree())
                continue;
            try
            {
                screen.GetType().GetMethod("UpdateCardDisplay", InstAll)?.Invoke(screen, null);
            }
            catch (Exception error)
            {
                GD.PrintErr("Portrait overlay: the inspect tips could not be put back: ", error.Message);
            }
        }
        _inspectArranged.Clear();
    }

    /// <summary>
    /// Puts back the two things only this pass changed for every inspect screen: the box's anchors (it is a centre-anchored
    /// scene control) and its minimum size. The offsets, scale and pivot are the combat hold's or the choice layout's to
    /// restore, so the deck/grid pre-hold state is kept exactly.
    /// </summary>
    private void RestoreInspectUpgrades()
    {
        foreach ((ulong id, InspectUpgradeState state) in _inspectUpgrades)
        {
            if (GodotObject.InstanceFromId(id) is Control screen && GodotObject.IsInstanceValid(screen) &&
                GodotObject.InstanceFromId(state.Id) is Control upgrade && GodotObject.IsInstanceValid(upgrade))
                state.RestoreFrame(upgrade);
        }
    }

    private sealed class InspectUpgradeState
    {
        public readonly ulong Id;
        private readonly float _anchorLeft, _anchorTop, _anchorRight, _anchorBottom;
        private readonly float _offsetLeft, _offsetTop, _offsetRight, _offsetBottom;
        private readonly Vector2 _minimum;

        private InspectUpgradeState(Control control)
        {
            Id = control.GetInstanceId();
            _anchorLeft = control.AnchorLeft;
            _anchorTop = control.AnchorTop;
            _anchorRight = control.AnchorRight;
            _anchorBottom = control.AnchorBottom;
            _offsetLeft = control.OffsetLeft;
            _offsetTop = control.OffsetTop;
            _offsetRight = control.OffsetRight;
            _offsetBottom = control.OffsetBottom;
            _minimum = control.CustomMinimumSize;
        }

        public float Width => Math.Max(_offsetRight - _offsetLeft, 1f);
        public float Height => Math.Max(_offsetBottom - _offsetTop, 1f);
        public float OffsetTop => _offsetTop;

        public static InspectUpgradeState Capture(Control control) => new(control);

        public void RestoreFrame(Control control)
        {
            control.AnchorLeft = _anchorLeft;
            control.AnchorTop = _anchorTop;
            control.AnchorRight = _anchorRight;
            control.AnchorBottom = _anchorBottom;
            control.CustomMinimumSize = _minimum;
        }
    }

    // ---- Putting the choices back ----------------------------------------------------------------------------------------

    private void RestoreChoices()
    {
        RestoreChoicePiles();
        foreach ((ulong id, ChoiceButton state) in _choiceButtons)
            if (GodotObject.InstanceFromId(id) is Control button && GodotObject.IsInstanceValid(button))
                RestoreChoiceButton(button, state);
        foreach (ulong id in _chooseCardScreens)
            if (GodotObject.InstanceFromId(id) is Control screen && GodotObject.IsInstanceValid(screen))
                RestoreChooseCard(screen);
        foreach (ulong id in _chooseBundleScreens)
            if (GodotObject.InstanceFromId(id) is Control screen && GodotObject.IsInstanceValid(screen))
                RestoreChooseBundle(screen);
        _bundleButtonSizes.Clear();
        _buttonRows.Clear();
        PortraitBetweenLayout.RestoreBanners(_liftedChoiceBanners);
        foreach (ulong id in _choiceSnapshots)
            if (_snapshots.TryGetValue(id, out PortraitControlSnapshot? snapshot) && GodotObject.InstanceFromId(id) is Control control &&
                GodotObject.IsInstanceValid(control))
                snapshot.Restore(control);
        foreach ((ulong id, (Vector2 position, Vector2 scale, bool topLevel)) in _fittedPopups)
        {
            if (GodotObject.InstanceFromId(id) is not Control popup || !GodotObject.IsInstanceValid(popup)) continue;
            popup.TopLevel = topLevel;
            popup.Scale = scale;
            popup.Position = position;
            PlacePopupTips(popup);
        }
        _fittedPopups.Clear();
        foreach (ulong id in _confirmPads.Keys)
            if (GodotObject.InstanceFromId(id) is Control confirm && GodotObject.IsInstanceValid(confirm))
                PhoneHitArea.Remove(confirm);
        _confirmPads.Clear();
    }

    private void PruneChoices()
    {
        static bool Dead(ulong id) => !GodotObject.IsInstanceIdValid(id);
        _choiceButtonIds.RemoveWhere(Dead);
        _chooseCardScreens.RemoveWhere(Dead);
        _chooseRelicScreens.RemoveWhere(Dead);
        _chooseBundleScreens.RemoveWhere(Dead);
        _choiceSnapshots.RemoveWhere(Dead);
        _potionPopups.RemoveWhere(Dead);
        _inspectTickboxes.RemoveWhere(Dead);
        _liftedChoiceBanners.RemoveWhere(Dead);
        _gridConfirms.RemoveWhere(Dead);
        _inspectScreens.RemoveWhere(Dead);
        _inspectArranged.RemoveWhere(Dead);
        foreach (ulong id in _inspectUpgrades.Keys.Where(Dead).ToList()) _inspectUpgrades.Remove(id);
        foreach (ulong id in _choicePiles.Keys.Where(Dead).ToList()) _choicePiles.Remove(id);
        foreach (ulong id in _choicePileStates.Keys.Where(Dead).ToList()) _choicePileStates.Remove(id);
        foreach (ulong id in _choiceButtons.Keys.Where(Dead).ToList()) _choiceButtons.Remove(id);
        foreach (ulong id in _buttonRows.Keys.Where(Dead).ToList()) _buttonRows.Remove(id);
        foreach (ulong id in _bundleButtonSizes.Keys.Where(Dead).ToList()) _bundleButtonSizes.Remove(id);
        foreach (ulong id in _cardOrders.Keys.Where(Dead).ToList()) _cardOrders.Remove(id);
        foreach (ulong id in _fittedPopups.Keys.Where(Dead).ToList()) _fittedPopups.Remove(id);
        foreach (ulong id in _confirmPads.Keys.Where(Dead).ToList()) _confirmPads.Remove(id);
    }

    // ---- shared -----------------------------------------------------------------------------------------------------------

    public void RestoreLandscape()
    {
        UnbindHandPeekFrame();
        RestoreChoices();
        RestoreInspectTips();
        RestoreInspectUpgrades();
        RestoreOverlayBackstop();
        foreach (ulong id in _popupPads)
            if (GodotObject.InstanceFromId(id) is Control button && GodotObject.IsInstanceValid(button))
                PhoneHitArea.Remove(button);
        _popupPads.Clear();
        _gameOver.RestoreLandscape();
        foreach ((ulong id, PortraitControlSnapshot snapshot) in _snapshots)
            if (GodotObject.InstanceFromId(id) is Control control && GodotObject.IsInstanceValid(control))
                snapshot.Restore(control);
    }

    public void PruneDead()
    {
        foreach (ulong id in _snapshots.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _snapshots.Remove(id);
        _ftues.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        _previewRows.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        _peekButtons.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        _gameOver.PruneDead();
        PruneChoices();
    }

    private PortraitControlSnapshot Capture(Control control)
    {
        if (!_snapshots.TryGetValue(control.GetInstanceId(), out PortraitControlSnapshot? snap))
            _snapshots[control.GetInstanceId()] = snap = new PortraitControlSnapshot(control, captureVisible: false);
        return snap;
    }
}
