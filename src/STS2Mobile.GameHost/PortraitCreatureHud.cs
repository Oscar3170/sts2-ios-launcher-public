using System;
using System.Collections.Generic;
using Godot;
using STS2Mobile.Platform;
using CombatReflection = STS2Mobile.GameHost.PortraitCombatLayout.CombatReflection;

namespace STS2Mobile.GameHost;

/// <summary>Owns creature health displays, intents, power rows, and their native restore state.</summary>
internal sealed class PortraitCreatureHud
{
    // Text: a creature is drawn at 0.42 to 0.94 of its size to fit the stage, which left its health, block and
    // name text (24 units), power amounts (18) and intent values (22) at about 6 points. The health display and the
    // intents are drawn at one fixed size on the canvas instead, whatever the creature's own scale: 24 units become 11
    // points at 1.33, 22 units at 1.45, and power amounts are set in 24 units.
    private const float StateDisplayCanvasScale = 1.33f;
    internal const float IntentCanvasScale = 1.45f;
    // The clearance the HP text keeps after the block shield and inside the bar's right end, and the smallest the digits
    // may shrink to when a bar is too narrow for the shield and the text together. The floor is low because the rule is
    // hard (the digits never pass the bar's end): the worst real case is the fixture's 4/4-digit hero HP on a 176 bar,
    // which needs 0.62, while real HP numbers are far shorter.
    private const float HpTextClearance = 4f;
    private const float HpTextMinScale = 0.50f;
    private const int PowerAmountFontSize = 24;

    private const float MinHealthBarWidth = PortraitCombatStage.MinHealthBarWidth;
    private const float MinNameplateWidth = 250f;
    private const float StageSideMargin = PortraitCombatStage.StageSideMargin;
    private const float PowerIconPitch = 48f;
    private readonly Dictionary<ulong, CreatureDesktopState> _creatures = new();
    private readonly Dictionary<ulong, int> _enlargedFonts = new();
    // Power icons whose amount label already got its portrait size (a power keeps its icon node for its whole life).
    private readonly HashSet<ulong> _enlargedPowerAmounts = new();
    // The frame's label requests, refilled rather than allocated every frame (the batch only reads the list).
    private readonly List<LabelRequest> _labelRequests = new();
    // The status batch's planning collections, refilled per frame instead of allocated (see the batch).
    private readonly List<(LabelRequest Request, StatusIconLayout? Layout, int Rank, float RowY)> _plannedLabels = new();
    private readonly Dictionary<(int Rank, float RowY), List<int>> _statusRows = new();
    private readonly List<List<int>> _statusRowIndexLists = new();
    private int _statusRowListsUsed;
    private readonly Dictionary<ulong, StatusIconLayout> _limitedStatus = new();
    private readonly List<StatusRowEntry> _statusRowEntries = new();
    private readonly List<(StatusRowEntry Entry, StatusIconLayout Layout)> _statusRowPlan = new();
    // The hit-box rects one status-planning pass reads (see StatusBodyRect): cleared when a new pass begins.
    private readonly Dictionary<ulong, Rect2> _statusBodyRects = new();
    private int _statusBodyEpoch;
    private int _statusBodyRectsEpoch = -1;
    private readonly Dictionary<ulong, (Control Container, Callable Handler)> _wiredPowerContainers = new();
    public void BeginFrame() => _labelRequests.Clear();

    public void Restore(CombatReflection refl, PortraitCombatStage stage, Action<Control, CombatReflection> restoreOrbs)
    {
        foreach ((ulong labelId, int fontSize) in _enlargedFonts)
        {
            if (GodotObject.InstanceFromId(labelId) is Label label && GodotObject.IsInstanceValid(label))
                label.AddThemeFontSizeOverride("font_size", fontSize);
        }
        _enlargedFonts.Clear();
        _enlargedPowerAmounts.Clear();

        foreach ((ulong creatureId, CreatureDesktopState state) in _creatures)
        {
            if (GodotObject.InstanceFromId(creatureId) is Control creature && GodotObject.IsInstanceValid(creature))
                state.Restore(creature, refl, stage, restoreOrbs);
        }
        _creatures.Clear();

        foreach ((ulong containerId, (Control container, Callable handler)) in _wiredPowerContainers)
        {
            if (GodotObject.InstanceFromId(containerId) is Control live && GodotObject.IsInstanceValid(live) &&
                live.HasSignal("child_entered_tree"))
                live.Disconnect("child_entered_tree", handler);
        }
        _wiredPowerContainers.Clear();
    }

    public void PruneDead(List<ulong> dead)
    {
        dead.Clear();
        foreach (ulong id in _creatures.Keys)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _creatures.Remove(id);

        dead.Clear();
        foreach (ulong id in _enlargedFonts.Keys)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _enlargedFonts.Remove(id);

        dead.Clear();
        foreach (ulong id in _enlargedPowerAmounts)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _enlargedPowerAmounts.Remove(id);

        dead.Clear();
        foreach (ulong id in _wiredPowerContainers.Keys)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _wiredPowerContainers.Remove(id);

        dead.Clear();
        foreach (ulong id in _statusBodyRects.Keys)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _statusBodyRects.Remove(id);
    }

    /// <summary>
    /// Sets a label's font size up to <paramref name="fontSize"/>, keeping the size it had so landscape gets it back.
    /// For labels the game does not size itself (auto size off): it would put its own size back on the next text change.
    /// </summary>
    private void EnlargeLabelFont(Label? label, int fontSize)
    {
        if (label == null || !GodotObject.IsInstanceValid(label)) return;
        int current = label.GetThemeFontSize("font_size");
        if (current >= fontSize) return;
        _enlargedFonts.TryAdd(label.GetInstanceId(), current);
        label.AddThemeFontSizeOverride("font_size", fontSize);
    }
    /// <summary>
    /// A creature's intents and its health display (bar, block, name, powers) are drawn at one size on the canvas
    /// (<see cref="IntentCanvasScale"/>, <see cref="StateDisplayCanvasScale"/>) whatever scale the creature has, so their
    /// text is 11 points. The game lays the health display out from the creature's hit box, as wide as the creature;
    /// scaled up, that would be up to twice as wide and start at the hit box's left edge. So the display is laid out again
    /// from a stand-in for the hit box (a hidden control) sized for a bar of the creature's width on the canvas, centred
    /// under it, no wider than <paramref name="maxBarWidth"/> and inside the canvas. This runs after the game's own layout
    /// (<c>NCreature.UpdateBounds</c>), and again whenever the game has laid the display out since.
    /// </summary>
    private void ApplyCreatureLabelsPortrait(
        Control creature,
        CreatureDesktopState cState,
        CombatReflection refl,
        float maxBarWidth,
        bool isAlly,
        StatusIconLayout? plannedStatus = null)
    {
        float creatureScale = creature.GetGlobalTransform().Scale.X;
        if (creatureScale < 0.05f)
            return;

        if (!isAlly && cState.Intents is { } intents && GodotObject.IsInstanceValid(intents))
        {
            // Grown from the bottom centre, so the intents rise above the head instead of sinking into it. The game owns
            // the row's position; the row only needs laying out again when its icons change or the creature's scale does,
            // and enumerating it every frame was one Godot array per creature per frame.
            float intentScale = IntentCanvasScale / creatureScale;
            int intentCount = intents.GetChildCount();
            float intentWidth = intents.Size.X;
            if (intentCount != cState.LastIntentCount || MathF.Abs(cState.LastIntentScale - intentScale) > 0.002f ||
                MathF.Abs(cState.LastIntentWidth - intentWidth) > 0.25f)
            {
                cState.LastIntentCount = intentCount;
                cState.LastIntentScale = intentScale;
                cState.LastIntentWidth = intentWidth;
                LayoutCombatIntents(creature, intents, creatureScale, intentScale);
            }
        }

        if (!refl.CanLayOutStateDisplay || cState.StateDisplayLayoutFailed ||
            cState.Display is not { } display || !GodotObject.IsInstanceValid(display) ||
            cState.Hitbox is not { } hitbox || !GodotObject.IsInstanceValid(hitbox) ||
            cState.Nameplate is not { } nameplate || !GodotObject.IsInstanceValid(nameplate) ||
            cState.HpBar is not { } hpBar || !GodotObject.IsInstanceValid(hpBar))
            return;

        float displayScale = StateDisplayCanvasScale / creatureScale;
        if (display.PivotOffset != Vector2.Zero)
            display.PivotOffset = Vector2.Zero;
        if (MathF.Abs(display.Scale.X - displayScale) > 0.002f)
            display.Scale = new Vector2(displayScale, displayScale);

        Control? powerContainer = cState.PowerContainer is { } p && GodotObject.IsInstanceValid(p) ? p : null;
        if (powerContainer != null)
        {
            // Each icon's amount label is enlarged once, when the icon first appears; a power's icon keeps its node and its
            // font override, so looking the label up again every frame was two Godot calls per power per creature. The
            // walk itself is a Godot array per creature per frame, and an icon can only appear with a child-count change
            // or the container's own add signal, so it is only taken then.
            int powerChildren = powerContainer.GetChildCount();
            if (powerChildren != cState.LastPowerChildCount || cState.PowerChildrenDirty)
            {
                cState.LastPowerChildCount = powerChildren;
                cState.PowerChildrenDirty = false;
                for (int i = 0; i < powerChildren; i++)
                {
                    Node power = powerContainer.GetChild(i);
                    if (!_enlargedPowerAmounts.Add(power.GetInstanceId()))
                        continue;
                    EnlargeLabelFont(power.GetNodeOrNull<Label>("%AmountLabel") ?? power.GetNodeOrNull<Label>("AmountLabel"),
                        PowerAmountFontSize);
                }
            }
        }

        // One unit of the display on the canvas, and the bar's place there: as wide as the game draws it (the hit box and
        // what the game adds to it), centred under the hit box.
        float unit = creatureScale * display.Scale.X;
        float hitWidth = hitbox.Size.X * creatureScale;
        float barWidth = Math.Clamp(
            hitWidth + cState.BarExtraWidth * creatureScale, Math.Min(MinHealthBarWidth, maxBarWidth), maxBarWidth);
        float barLeft = LeftEdgeOnCanvas(hitbox.GlobalPosition.X + hitWidth * 0.5f, barWidth);
        // The name may be wider than the bar, but never wider than the room the slot gives it: the game shrinks a name
        // that does not fit, and only one name shows at a time.
        float nameWidth = Math.Max(barWidth, Math.Min(MinNameplateWidth, maxBarWidth));
        float nameLeft = LeftEdgeOnCanvas(barLeft + barWidth * 0.5f, nameWidth);
        float nameLocalX = (nameLeft - display.GlobalPosition.X) / unit;
        float nameLocalWidth = nameWidth / unit;

        // The name plate is the marker for the game's own bar layout: when it is already where this layout puts it, only
        // the power icons (which change with the status count, not the name) are laid out again below.
        bool nameSynced = MathF.Abs(nameplate.Position.X - nameLocalX) <= 0.75f &&
            MathF.Abs(nameplate.Size.X - nameLocalWidth) <= 0.75f;
        float boundsY = hitbox.GlobalPosition.Y;
        Control? bounds = null;
        if (!nameSynced)
        {
            bounds = cState.EnsureBoundsStandIn(creature);
            if (bounds == null)
                return;

            // The bar: the game makes it wider than the bounds by its extra width in display units, and starts it half
            // that many canvas units left of them.
            bounds.Size = new Vector2(Math.Max(8f, barWidth / unit - cState.BarExtraWidth), 8f);
            bounds.GlobalPosition = new Vector2(barLeft + cState.BarExtraWidth * 0.5f, boundsY);
            if (!refl.LayOutStateDisplay(display, bounds))
            {
                // Scaled up with the game's own layout the display would be far wider than its creature: this creature
                // keeps the game's size and layout from here on.
                cState.GiveUpStateDisplay(creature, refl);
                return;
            }
        }

        // The powers: the game's own two-row layout (container from the hit box, columns from its width and the count)
        // with the portrait icon size and the neighbour-limited block placed left of it.
        if (powerContainer != null)
        {
            // The batch already counted the block's icons to plan it; only a creature the batch did not plan needs a count.
            int powerCount = plannedStatus is { Count: > 0 } knownCount ? knownCount.Count : CountVisiblePowers(powerContainer);
            if (powerCount > 0)
            {
                StatusIconLayout icons = plannedStatus is { Count: > 0 } planned && planned.Count == powerCount
                    ? planned
                    : ComputeStatusLayout(cState.Hitbox, powerCount);
                ApplyStatusBlock(creature, cState, refl, icons, powerCount);
            }
            // Wire the container even while it holds no powers: the FIRST power a creature gains is added inside
            // NPowerContainer.Add, and a container that was only wired once it already had an icon drew that first icon
            // for one frame at the native size and the native row pitch.
            EnsurePowerContainerWired(powerContainer, creature, cState, refl);
        }

        // The bar band hangs under the creature's OWN foot line, never under another row's: a back-rank peer or a flyer
        // stands on its slot's feet, and pinning every bar to the front foot line put its bar across the front row's
        // bodies. The band also clears the foot line by a few units instead of starting exactly on it, because every body
        // ends on that line and a bar touching it is reported as overlapping the body standing beside it.
        float ownFeet = OwnFeetLine(creature, cState);
        // The label the band and the block-shield rule use, remembered on the state while it stays valid: this pass asks for
        // it on every frame of the fight, and a % path resolves through the unique-name map.
        Label? hpLabel = cState.HpRuleLabel is { } knownLabel && GodotObject.IsInstanceValid(knownLabel)
            ? knownLabel
            : hpBar.GetNodeOrNull<Label>("%HpLabel") ?? hpBar.GetNodeOrNull<Label>("HpLabel");
        Control bandNode = hpLabel ?? hpBar;
        float bandTop = bandNode.GetGlobalRect().Position.Y;
        if (ownFeet > 1f)
        {
            float target = ownFeet + BarBandClearance;
            float shift = target - bandTop;
            if (MathF.Abs(shift) > 0.25f)
            {
                hpBar.GlobalPosition = new Vector2(hpBar.GlobalPosition.X, hpBar.GlobalPosition.Y + shift);
                if (powerContainer != null)
                    powerContainer.GlobalPosition = new Vector2(powerContainer.GlobalPosition.X,
                        powerContainer.GlobalPosition.Y + shift);
            }
        }
        // The block shield is drawn at the bar's left end; on a narrow bar it covers the first digit of the centred HP
        // text (a 7 shield over "12/29" shows "2/29"). The native bar never moves the label, so the digit is moved clear
        // here — only while a block is shown, only as far as it must, and never past the bar's right end.
        if (hpLabel != null)
            using (PortraitFrameTiming.Begin(PortraitFrameTiming.HpText))
                KeepHpTextClearOfBlockBand(creature, cState, hpBar, hpLabel);

        if (nameSynced)
            return;
        nameplate.Position = new Vector2(nameLocalX, nameplate.Position.Y);
        nameplate.Size = new Vector2(nameLocalWidth, nameplate.Size.Y);

        // The bar's touch area (258 units wide in the scene) follows the bar, so it does not reach over a neighbour's bar.
        if (cState.HpBarHitbox is { } barHitbox && GodotObject.IsInstanceValid(barHitbox))
        {
            barHitbox.OffsetLeft = hpBar.Position.X - 8f;
            barHitbox.OffsetRight = hpBar.Position.X + barWidth / unit + 8f;
        }
    }

    /// <summary>
    /// Keeps the HP text clear of the block shield the game draws at the bar's left end. The native bar toggles the
    /// shield (and slides it in on a block gain) but never moves <c>%HpLabel</c>, so its place is ours to set: the label
    /// is moved right by the overlap the shield has with the centred text, capped inside the bar's right end, and it goes
    /// back to its native X as soon as the shield is hidden again. Everything is measured in canvas units and the write
    /// is converted through the label's parent scale, so a nested or scaled bar behaves the same.
    /// <para>The expensive part is the glyph metrics — a text shape plus the font lookups for the digits and for the
    /// block number — and they only change with the text, the label's rect or its theme font size, so they are cached on
    /// the creature's state. The bar, the shield and the label are still measured live on every frame, so the native block
    /// slide is followed exactly; and a bar with no shield shown is put back and left alone before any of it is touched.</para>
    /// </summary>
    private static void KeepHpTextClearOfBlockBand(Control creature, CreatureDesktopState state, Control hpBar,
        Label hpLabel)
    {
        if (hpLabel == hpBar)
            return;   // no HP label in this bar's scene: the bar itself is never moved sideways
        if (state.HpRuleLabel != hpLabel)
        {
            // The first pass on this label (or a new one): its native place is captured once, and the metas keep it for
            // the landscape restore. The measured inks belong to this label, so they are dropped with it.
            state.HpRuleLabel = hpLabel;
            if (!hpLabel.HasMeta("portrait_hp_x"))
                hpLabel.SetMeta("portrait_hp_x", hpLabel.Position.X);
            if (!hpLabel.HasMeta("portrait_hp_scale"))
                hpLabel.SetMeta("portrait_hp_scale", hpLabel.Scale.X);
            state.HpNativeX = hpLabel.GetMeta("portrait_hp_x").AsSingle();
            state.HpNativeScale = hpLabel.GetMeta("portrait_hp_scale").AsSingle();
            state.HpInk = default;
            state.HpBlockInk = default;
            // Whatever the creature already carries (a report written before a portrait toggle) is taken as the known
            // state: the first frame that computes the same report writes nothing, and the first frame without a block
            // removes it.
            state.HpCapped = creature.HasMeta("portrait_hp_shift_capped")
                ? creature.GetMeta("portrait_hp_shift_capped").ToString()
                : null;
        }
        float nativeX = state.HpNativeX;
        float nativeScale = state.HpNativeScale;

        Control? shield = state.HpBlockShield;
        if (shield == null || !GodotObject.IsInstanceValid(shield))
        {
            shield = hpBar.GetNodeOrNull<Control>("%BlockContainer");
            state.HpBlockShield = shield;
            state.HpBlockIcon = null;
            state.HpBlockLabel = null;
        }
        if (shield == null || !shield.IsVisibleInTree())
        {
            // No block: the digits go back to the native place and size, so a bar the game draws cleanly is untouched.
            if (MathF.Abs(hpLabel.Scale.X - nativeScale) > 0.001f)
                hpLabel.Scale = new Vector2(nativeScale, nativeScale);
            if (MathF.Abs(hpLabel.Position.X - nativeX) > 0.01f)
                hpLabel.Position = new Vector2(nativeX, hpLabel.Position.Y);
            if (state.HpCapped is not null)
            {
                if (creature.HasMeta("portrait_hp_shift_capped"))
                    creature.RemoveMeta("portrait_hp_shift_capped");
                state.HpCapped = null;
            }
            return;
        }
        if (hpLabel.GetParent() is not Control parent)
            return;
        Transform2D parentTransform = parent.GetGlobalTransform();
        float parentScale = MathF.Max(0.0001f, parentTransform.Scale.X);
        // The ink as the native bar lays it out (the label's own scale is 1 there), mapped onto the canvas.
        Vector2 nativeOrigin = new(nativeX, hpLabel.Position.Y);
        Rect2 localInk = LabelInkLocal(hpLabel, hpLabel.Text, ref state.HpInk);
        Rect2 nativeInk = parentTransform * new Rect2(localInk.Position + nativeOrigin, localInk.Size);
        float pivotX = (parentTransform * (nativeOrigin + hpLabel.PivotOffset)).X;
        Rect2 barRect = hpBar.GetGlobalRect();
        Rect2 shieldInk = ShieldInk(shield, state);
        PortraitCombatMath.HpTextBlockFit fit = PortraitCombatMath.FitHpTextBesideBlock(
            nativeInk.Position.X, nativeInk.Size.X, pivotX, shieldInk.End.X, barRect.End.X, HpTextClearance,
            HpTextMinScale);
        float targetScale = fit.Scale * nativeScale;
        if (MathF.Abs(hpLabel.Scale.X - targetScale) > 0.001f)
            hpLabel.Scale = new Vector2(targetScale, targetScale);
        float targetX = nativeX + fit.Shift / parentScale;
        if (MathF.Abs(hpLabel.Position.X - targetX) > 0.01f)
            hpLabel.Position = new Vector2(targetX, hpLabel.Position.Y);
        // The floor: a bar too narrow for the shield and the smallest allowed digits still overflows its right end; keep
        // the legibility floor, say so on the creature, and let the stage report take it to the user. The string is only
        // written when it changes (the report reads it, and the fixture's 4-digit hero HP is capped for whole fights).
        string? capped = null;
        float inkRight = nativeInk.Position.X + fit.Shift + nativeInk.Size.X * fit.Scale;
        if (fit.Scale <= HpTextMinScale + 0.001f && inkRight > barRect.End.X - HpTextClearance + 0.5f)
            capped = $"need={fit.Shift:F1} scale={fit.Scale:F3} inkRight={inkRight:F1} barRight={barRect.End.X:F1}";
        if (!string.Equals(capped, state.HpCapped, StringComparison.Ordinal))
        {
            if (capped is null)
                creature.RemoveMeta("portrait_hp_shift_capped");
            else
                creature.SetMeta("portrait_hp_shift_capped", capped);
            state.HpCapped = capped;
        }
    }

    /// <summary>
    /// A label's drawn glyph ink in its own units, as the stage checks measure it: the text's advance at the label's own
    /// theme font size, its cap height, and the outline grown on every side, placed by the label's alignment inside its
    /// rect. The control's rect is not the ink (the game's HP label is wider and taller than the digits), and the metrics
    /// are scale-free so the caller can place the ink at the native scale itself.
    /// <para>The measurement is cached per label by its text, rect and theme font size: a text shape and the font
    /// metrics are the two expensive calls here, and they only change with those three (the game's auto-sized labels
    /// adjust the size from the rect when the text changes, and again if the rect resizes).</para>
    /// </summary>
    private static Rect2 LabelInkLocal(Label label, string text, ref CreatureDesktopState.GlyphInk cache)
    {
        int fontSize = label.GetThemeFontSize("font_size");
        Vector2 box = label.Size;
        if (cache.Known && cache.FontSize == fontSize && box == cache.Box &&
            string.Equals(text, cache.Text, StringComparison.Ordinal))
            return cache.Ink;
        Rect2 ink = MeasureLabelInk(label, text, box, fontSize);
        cache.Text = text;
        cache.Box = box;
        cache.FontSize = fontSize;
        cache.Ink = ink;
        cache.Known = true;
        return ink;
    }

    /// <summary>The uncached part of <see cref="LabelInkLocal"/>: the font metrics and the text's advance.</summary>
    private static Rect2 MeasureLabelInk(Label label, string text, Vector2 box, int fontSize)
    {
        Font? font = label.GetThemeFont("font");
        if (font == null || string.IsNullOrWhiteSpace(text))
            return new Rect2(Vector2.Zero, box);
        int outline = Math.Max(0, label.GetThemeConstant("outline_size"));
        Vector2 advance = font.GetStringSize(text, label.HorizontalAlignment, -1f, fontSize);
        float ascent = font.GetAscent(fontSize);
        Vector2 textSize = new(advance.X, ascent > 0f ? ascent : advance.Y);
        float x = label.HorizontalAlignment switch
        {
            HorizontalAlignment.Center => (box.X - textSize.X) * 0.5f,
            HorizontalAlignment.Right => box.X - textSize.X,
            _ => 0f,
        };
        float y = label.VerticalAlignment switch
        {
            VerticalAlignment.Center => (box.Y - textSize.Y) * 0.5f,
            VerticalAlignment.Bottom => box.Y - textSize.Y,
            _ => 0f,
        };
        return new Rect2(new Vector2(x - outline, y - outline),
            textSize + new Vector2(outline * 2f, outline * 2f));
    }

    /// <summary>
    /// The block shield's drawn ink: its icon merged with the block number's glyph ink when it shows one. The two nodes and
    /// the number's measurement are cached on the creature's state; the number's text is what changes them, and the shield's
    /// own place (which the native block slide tweens) is still read live.
    /// </summary>
    private static Rect2 ShieldInk(Control shield, CreatureDesktopState state)
    {
        Rect2? ink = null;
        TextureRect? icon = state.HpBlockIcon;
        if (icon == null || !GodotObject.IsInstanceValid(icon))
        {
            icon = shield.GetNodeOrNull<TextureRect>("BlockIcon");
            state.HpBlockIcon = icon;
        }
        if (icon != null && icon.IsVisibleInTree())
            ink = icon.GetGlobalRect();
        Label? blockLabel = state.HpBlockLabel;
        if (blockLabel == null || !GodotObject.IsInstanceValid(blockLabel))
        {
            blockLabel = shield.GetNodeOrNull<Label>("BlockLabel");
            state.HpBlockLabel = blockLabel;
        }
        if (blockLabel != null && blockLabel.IsVisibleInTree())
        {
            string blockText = blockLabel.Text;
            if (!string.IsNullOrWhiteSpace(blockText))
            {
                Rect2 number = blockLabel.GetGlobalTransform() *
                    LabelInkLocal(blockLabel, blockText, ref state.HpBlockInk);
                ink = ink is { } known ? known.Merge(number) : number;
            }
        }
        return ink ?? shield.GetGlobalRect();
    }

    /// <summary>
    /// The intents above a creature's head, at one size on the canvas whatever the body's scale: a row of several that
    /// would pass a side of the canvas is moved back in through its pivot (a pivot d units right of the centre moves the
    /// scaled row d * (scale - 1) left). Run only when the row changed (see last intent count/scale on the state).
    /// </summary>
    private static void LayoutCombatIntents(Control creature, Control intents, float creatureScale, float intentScale)
    {
        float pivotX = intents.Size.X * 0.5f;
        int shown = 0;
        foreach (Node intent in intents.GetChildren())
        {
            if (intent is Control { Visible: true } && !intent.IsQueuedForDeletion())
                shown++;
        }
        if (shown > 1 && intentScale > 1.05f)
        {
            float rowHalf = (shown * 64f - (shown - 1) * 10f) * IntentCanvasScale * 0.5f;
            float center = creature.GlobalPosition.X + (intents.Position.X + intents.Size.X * 0.5f) * creatureScale;
            float clamped = LeftEdgeOnCanvas(center, rowHalf * 2f) + rowHalf;
            pivotX += (center - clamped) / (creatureScale * (intentScale - 1f));
        }
        var intentPivot = new Vector2(pivotX, intents.Size.Y);
        if ((intents.PivotOffset - intentPivot).LengthSquared() > 0.25f)
            intents.PivotOffset = intentPivot;
        if (MathF.Abs(intents.Scale.X - intentScale) > 0.002f)
            intents.Scale = new Vector2(intentScale, intentScale);
    }

    /// <summary>The clearance a health bar keeps below its own foot line (the harness/collision margin is 2 units).</summary>
    internal const float BarBandClearance = 3f;

    /// <summary>
    /// A creature's own foot line: the line its slot assigned (<c>portrait_feet_y</c>), not the front row's and not the
    /// hit box bottom, which some scenes lay a few dozen units above the feet. Falls back to the body bottom, then to the
    /// front foot line before the creature has been placed.
    /// </summary>
    private static float OwnFeetLine(Control creature, CreatureDesktopState cState)
    {
        if (creature.HasMeta("portrait_feet_y"))
        {
            float feet = creature.GetMeta("portrait_feet_y").AsSingle();
            if (feet > 1f)
                return feet;
        }
        if (cState.Hitbox is { } hit && GodotObject.IsInstanceValid(hit))
        {
            Rect2 rect = hit.GetGlobalRect();
            if (rect.Size.Y > 1f && rect.End.Y > 1f)
                return rect.End.Y;
        }
        return 0f;
    }

    /// <summary>
    /// A creature's label pass, deferred until every creature of the room is placed so status blocks can see their
    /// neighbours. A struct: one is queued per creature on every frame of the fight.
    /// </summary>
    private readonly record struct LabelRequest(Control Creature, CreatureDesktopState State,
        float MaxBarWidth, bool IsAlly);

    /// <summary>
    /// Lays out the health displays and status blocks for every creature of the room. The blocks are planned first from the
    /// final hit boxes, then neighbours on the same status row (same rank and foot line, split at their midpoints) cap each
    /// other's overhang and a neighbour body standing in the block's own band is a hard edge, so the result does not depend
    /// on the order the creatures were laid out in.
    /// </summary>
    public void Apply(Control room, CombatReflection refl, PortraitCombatStage stage)
    {
        List<LabelRequest> requests = _labelRequests;
        if (requests.Count == 0)
            return;

        // The planning collections are refilled, not reallocated: this batch runs on every frame of the fight.
        _statusBodyEpoch++;
        List<(LabelRequest Request, StatusIconLayout? Layout, int Rank, float RowY)> planned = _plannedLabels;
        planned.Clear();
        Dictionary<(int Rank, float RowY), List<int>> rows = _statusRows;
        rows.Clear();
        _statusRowListsUsed = 0;
        foreach (LabelRequest request in requests)
        {
            if (!GodotObject.IsInstanceValid(request.Creature))
                continue;
            StatusIconLayout? layout = null;
            if (request.State.PowerContainer is { } powers && GodotObject.IsInstanceValid(powers))
            {
                int count = CountVisiblePowers(powers);
                if (count > 0)
                    layout = ComputeStatusLayout(request.State.Hitbox, count);
            }

            // The row key is the foot line the stage assigned (portrait_feet_y), not the hit box bottom: two peers of
            // different native heights can carry hit boxes 50 units apart while standing on the same line, and a split
            // row silently skipped the neighbour limits between them.
            int rank = 0;
            float rowY = OwnFeetLine(request.Creature, request.State);
            if (stage.TallSlot(room, request.Creature) is { } slot)
            {
                rank = slot.RowDepth;
                if (rowY <= 0f)
                    rowY = slot.CanvasFeetY;
            }
            else if (rowY <= 0f && request.State.Hitbox is { } hit && GodotObject.IsInstanceValid(hit))
            {
                Transform2D t = hit.GetGlobalTransformWithCanvas();
                rowY = (t * new Vector2(0f, hit.Size.Y)).Y;
            }
            rowY = MathF.Round(rowY / 16f) * 16f;
            planned.Add((request, layout, rank, rowY));
            if (layout is { Count: > 0 })
            {
                var key = (rank, rowY);
                if (!rows.TryGetValue(key, out List<int>? row))
                {
                    row = NextStatusRowIndexList();
                    rows[key] = row;
                }
                row.Add(planned.Count - 1);
            }
        }

        // Gaps and body edges are read from every creature's untouched base block before any block is written, so writing
        // one block never becomes the next block's limit (that order-dependence made this pass disagree with the power
        // add's own plan, and the icons already on screen moved for one frame when it caught up).
        Dictionary<ulong, StatusIconLayout> limited = _limitedStatus;
        limited.Clear();
        foreach (List<int> row in rows.Values)
        {
            List<StatusRowEntry> entries = _statusRowEntries;
            entries.Clear();
            foreach (int index in row)
            {
                (LabelRequest request, StatusIconLayout? layout, _, _) = planned[index];
                entries.Add(new StatusRowEntry(request.Creature, request.State, layout!.Value));
            }
            // PlanStatusRow fills _statusRowPlan; it is folded into `limited` before the next row asks for it.
            foreach ((StatusRowEntry entry, StatusIconLayout plannedLayout) in PlanStatusRow(entries))
                limited[entry.Creature.GetInstanceId()] = plannedLayout;
        }

        foreach ((LabelRequest request, StatusIconLayout? layout, _, _) in planned)
        {
            StatusIconLayout? plannedLayout = limited.TryGetValue(request.Creature.GetInstanceId(), out StatusIconLayout found)
                ? found
                : layout;
            ApplyCreatureLabelsPortrait(request.Creature, request.State, refl, request.MaxBarWidth, request.IsAlly, plannedLayout);
        }
    }

    /// <summary>One index list per status row, reused across frames (the dictionary that holds them is cleared first).</summary>
    private List<int> NextStatusRowIndexList()
    {
        if (_statusRowListsUsed == _statusRowIndexLists.Count)
            _statusRowIndexLists.Add(new List<int>());
        List<int> list = _statusRowIndexLists[_statusRowListsUsed++];
        list.Clear();
        return list;
    }

    /// <summary>One creature on a status row: its node and state, and the untouched base block the native rule gives it.</summary>
    private readonly record struct StatusRowEntry(Control Creature, CreatureDesktopState State, StatusIconLayout Base);

    /// <summary>
    /// Plans one status row: the base blocks are sorted once, every gap and every neighbour body is read from those
    /// untouched blocks, and only then are the limited blocks returned. Keeping the plan separate from the write is what
    /// makes the result order-independent — a limited block must never become the limit of the next one.
    /// </summary>
    private List<(StatusRowEntry Entry, StatusIconLayout Layout)> PlanStatusRow(List<StatusRowEntry> row)
    {
        // Refilled per row (see NextStatusRowIndexList): the caller folds it before the next row plans.
        List<(StatusRowEntry Entry, StatusIconLayout Layout)> planned = _statusRowPlan;
        planned.Clear();
        if (row.Count == 0)
            return planned;
        row.Sort((a, b) => a.Base.Left.CompareTo(b.Base.Left));
        for (int i = 0; i < row.Count; i++)
        {
            StatusRowEntry entry = row[i];
            float leftGap = i > 0
                ? entry.Base.Left - (row[i - 1].Base.Left + row[i - 1].Base.RowWidth)
                : float.PositiveInfinity;
            float rightGap = i + 1 < row.Count
                ? row[i + 1].Base.Left - (entry.Base.Left + entry.Base.RowWidth)
                : float.PositiveInfinity;
            if (entry.State.Hitbox is not { } hitbox || !GodotObject.IsInstanceValid(hitbox))
            {
                planned.Add((entry, entry.Base));
                continue;
            }
            Transform2D t = hitbox.GetGlobalTransformWithCanvas();
            Vector2 tl = t * Vector2.Zero;
            Vector2 br = t * hitbox.Size;
            float hitLeft = MathF.Min(tl.X, br.X);
            float hitWidth = MathF.Abs(br.X - tl.X);
            (float bodyLeft, float bodyRight) = StatusBodyEdges(entry);
            planned.Add((entry, StatusIconMath.ApplyNeighbourLimits(entry.Base, hitLeft, hitWidth,
                SafeAreaCanvas.PortraitWidth, leftGap, rightGap, bodyLeft, bodyRight)));
        }
        return planned;
    }

    /// <summary>
    /// The bodies standing in this block's own icon band, as the hard edges <see cref="StatusIconMath.ApplyNeighbourLimits"/>
    /// takes: <c>BodyLeft</c> is the nearest body edge on the block's right, <c>BodyRight</c> the nearest on its left. A body on
    /// another foot line counts too — a front- or back-rank body taller than its row reaches under this row's icons (the
    /// 9:19.5 enemy pair) — and an icon may never be drawn over it. The band is the power container's line plus the
    /// block's rows, the band the icons are drawn in.
    /// </summary>
    private (float BodyLeft, float BodyRight) StatusBodyEdges(StatusRowEntry entry)
    {
        float bodyLeft = float.PositiveInfinity;
        float bodyRight = float.NegativeInfinity;
        if (entry.State.PowerContainer is not { } container || !GodotObject.IsInstanceValid(container))
            return (bodyLeft, float.PositiveInfinity);
        float top = container.GetGlobalRect().Position.Y;
        float bottom = top + Math.Max(1f, entry.Base.Rows * entry.Base.IconWidth * 5f / 6f);
        float centre = entry.Base.Left + entry.Base.RowWidth * 0.5f;
        ulong selfId = entry.Creature.GetInstanceId();
        foreach ((ulong id, CreatureDesktopState other) in _creatures)
        {
            if (id == selfId)
                continue;
            if (GodotObject.InstanceFromId(id) is not Control node || !GodotObject.IsInstanceValid(node) ||
                !node.IsVisibleInTree())
                continue;
            if (other.Hitbox is not { } hitbox || !GodotObject.IsInstanceValid(hitbox))
                continue;
            Rect2 body = StatusBodyRect(id, hitbox);
            if (body.End.Y <= top || body.Position.Y >= bottom)
                continue;
            if (body.Position.X + body.Size.X * 0.5f >= centre)
                bodyLeft = MathF.Min(bodyLeft, body.Position.X);
            else
                bodyRight = MathF.Max(bodyRight, body.End.X);
        }
        return (bodyLeft, float.IsNegativeInfinity(bodyRight) ? float.PositiveInfinity : bodyRight);
    }

    /// <summary>
    /// A creature's hit box on the canvas, measured once per status-planning pass. One row entry asks for every other
    /// creature's rect, and a row with n blocks asks n times each: no game code runs while a pass plans, so the rects
    /// cannot move between the reads (the pass that writes them runs after the plan). The epoch is bumped where a plan
    /// begins — the deferred batch and a power add's own replan.
    /// </summary>
    private Rect2 StatusBodyRect(ulong id, Control hitbox)
    {
        if (_statusBodyRectsEpoch != _statusBodyEpoch)
        {
            _statusBodyRects.Clear();
            _statusBodyRectsEpoch = _statusBodyEpoch;
        }
        if (_statusBodyRects.TryGetValue(id, out Rect2 known))
            return known;
        Rect2 rect = hitbox.GetGlobalRect();
        _statusBodyRects[id] = rect;
        return rect;
    }

    /// <summary>Plans and writes one status row: the deferred pass's rows and a power add's own row both come here.</summary>
    private void ApplyStatusRow(List<StatusRowEntry> row, CombatReflection refl)
    {
        foreach ((StatusRowEntry entry, StatusIconLayout layout) in PlanStatusRow(row))
            ApplyStatusBlock(entry.Creature, entry.State, refl, layout, entry.Base.Count);
    }

    private static StatusIconLayout ComputeStatusLayout(Control? hitbox, int count)
    {
        if (hitbox == null || !GodotObject.IsInstanceValid(hitbox) || hitbox.Size.X < 1f)
            return StatusIconMath.Compute(count, SafeAreaCanvas.PortraitWidth * 0.5f, 200f, SafeAreaCanvas.PortraitWidth);
        Transform2D t = hitbox.GetGlobalTransformWithCanvas();
        Vector2 tl = t * Vector2.Zero;
        Vector2 br = t * hitbox.Size;
        float left = MathF.Min(tl.X, br.X);
        float width = MathF.Abs(br.X - tl.X);
        return StatusIconMath.Compute(count, left, width, SafeAreaCanvas.PortraitWidth);
    }

    /// <summary>
    /// Applies a planned status block to a creature's power container. The native layout call gets the planned left as the
    /// bounds X, so the container's <c>_originalPosition</c> is our position and the game's own <c>UpdatePositions</c> (on
    /// every power add) rewrites the same place instead of the canvas edge.
    /// </summary>
    private void ApplyStatusBlock(Control creature, CreatureDesktopState cState, CombatReflection refl,
        StatusIconLayout icons, int powerCount)
    {
        using (PortraitFrameTiming.Begin(PortraitFrameTiming.Status))
            ApplyStatusBlockCore(creature, cState, refl, icons, powerCount);
    }

    /// <summary>
    /// See <see cref="ApplyStatusBlock"/>. The container is only laid out again when the block it carries is not already
    /// the one this pass would write: the native layout is interpreted game code that repositions every icon, and a
    /// settled fight asks for the same block on every frame. Everything the native call reads is in the signature.
    /// </summary>
    private void ApplyStatusBlockCore(Control creature, CreatureDesktopState cState, CombatReflection refl,
        StatusIconLayout icons, int powerCount)
    {
        if (cState.PowerContainer is not { } powerContainer || !GodotObject.IsInstanceValid(powerContainer) ||
            cState.Hitbox is not { } hitbox || !GodotObject.IsInstanceValid(hitbox))
            return;
        if (cState.StatusBlockIsApplied(powerContainer, icons, powerCount))
            return;
        float creatureScale = creature.GetGlobalTransform().Scale.X;
        if (creatureScale < 0.05f || cState.Display is not { } display || !GodotObject.IsInstanceValid(display))
            return;
        float unit = creatureScale * display.Scale.X;
        float nativeIcon = PowerIconPitch;
        foreach (Node power in powerContainer.GetChildren())
        {
            if (power is Control { Visible: true } iconNode && iconNode.Size.X > 1f)
            {
                nativeIcon = iconNode.Size.X;
                break;
            }
        }
        Control? bounds = cState.EnsureBoundsStandIn(creature);
        if (bounds == null)
            return;
        bounds.Size = new Vector2(nativeIcon * icons.Columns - 25f, 8f);
        bounds.GlobalPosition = new Vector2(icons.Left, hitbox.GlobalPosition.Y);
        refl.LayOutPowerContainer(powerContainer, bounds);
        float iconScale = icons.IconWidth / (nativeIcon * unit);
        if (powerContainer.PivotOffset != Vector2.Zero)
            powerContainer.PivotOffset = Vector2.Zero;
        if (MathF.Abs(powerContainer.Scale.X - iconScale) > 0.002f)
            powerContainer.Scale = new Vector2(iconScale, iconScale);
        // The native layout centres only when the row is wider than the container it was given; here it is not, so this is
        // a no-op safety net rather than the place that positions the block.
        if (MathF.Abs(powerContainer.GlobalPosition.X - icons.Left) > 0.01f)
            powerContainer.GlobalPosition = new Vector2(icons.Left, powerContainer.GlobalPosition.Y);
        cState.RememberStatusBlock(powerContainer, icons, powerCount);
    }

    /// <summary>
    /// Connects one handler per power container. It runs synchronously while <c>NPowerContainer.Add</c> is adding the new
    /// icon (before that method's own <c>UpdatePositions</c>) and replans the whole status row through
    /// <see cref="ReapplyStatusRowNeighbours"/> — the same plan the deferred batch makes — so the add's final write, the
    /// icons already drawn beside it and the next pass all agree.
    /// </summary>
    private void EnsurePowerContainerWired(Control powerContainer, Control creature, CreatureDesktopState cState,
        CombatReflection refl)
    {
        ulong containerId = powerContainer.GetInstanceId();
        if (_wiredPowerContainers.ContainsKey(containerId))
            return;
        ulong creatureId = creature.GetInstanceId();
        Callable handler = Callable.From<Node>(_ =>
        {
            if (!SafeAreaCanvas.IsPortrait)
                return;
            if (GodotObject.InstanceFromId(creatureId) is not Control liveCreature ||
                !GodotObject.IsInstanceValid(liveCreature) ||
                !_creatures.TryGetValue(creatureId, out CreatureDesktopState? state))
                return;
            // An icon just appeared: the next labels pass must walk the row and enlarge its amount label (see
            // ApplyCreatureLabelsPortrait), even if the container's child count happens to be unchanged.
            state.PowerChildrenDirty = true;
            if (state.PowerContainer is not { } container || !GodotObject.IsInstanceValid(container))
                return;
            if (CountVisiblePowers(container) == 0 || state.Hitbox is not { } hitbox ||
                !GodotObject.IsInstanceValid(hitbox))
                return;
            // One plan for the whole row: replanning only this creature from cached gaps made the add's first frame differ
            // from the batch that follows it (a neighbour's limit moved without the neighbour being laid out again), and
            // the icons already on screen jumped by that difference.
            ReapplyStatusRowNeighbours(liveCreature, state, refl);
        });
        if (powerContainer.HasSignal("child_entered_tree"))
            powerContainer.Connect("child_entered_tree", handler);
        _wiredPowerContainers[containerId] = (powerContainer, handler);
    }

    /// <summary>
    /// Re-plans and re-applies every status block standing on the same row as <paramref name="creature"/> (its own foot
    /// line), because one block's size changes the gap the neighbours may overhang into. Called synchronously from a
    /// power add so every block on the row is drawn at its final geometry on that same frame. The plan uses the same
    /// inputs as <see cref="ApplyCreatureLabelsPortraitBatch"/>: every creature's untouched base block, the gaps between
    /// those blocks, and the neighbours' hit-box bodies as hard edges.
    /// </summary>
    private void ReapplyStatusRowNeighbours(Control creature, CreatureDesktopState state, CombatReflection refl)
    {
        if (SafeAreaCanvas.IsPortrait is false)
            return;
        _statusBodyEpoch++;
        float rowY = MathF.Round(OwnFeetLine(creature, state) / 16f) * 16f;
        var row = new List<StatusRowEntry>();
        foreach ((ulong id, CreatureDesktopState other) in _creatures)
        {
            if (other.PowerContainer is not { } container || !GodotObject.IsInstanceValid(container))
                continue;
            if (GodotObject.InstanceFromId(id) is not Control node || !GodotObject.IsInstanceValid(node))
                continue;
            float otherRow = MathF.Round(OwnFeetLine(node, other) / 16f) * 16f;
            if (otherRow != rowY)
                continue;
            int count = CountVisiblePowers(container);
            if (count == 0 || other.Hitbox is not { } hitbox || !GodotObject.IsInstanceValid(hitbox))
                continue;
            row.Add(new StatusRowEntry(node, other, ComputeStatusLayout(other.Hitbox, count)));
        }
        ApplyStatusRow(row, refl);
    }

    private static int CountVisiblePowers(Control powerContainer)
    {
        int count = 0;
        int children = powerContainer.GetChildCount();
        for (int i = 0; i < children; i++)
        {
            if (powerContainer.GetChild(i) is Control { Visible: true } icon && !icon.IsQueuedForDeletion())
                count++;
        }
        return count;
    }

    /// <summary>The left edge of something <paramref name="width"/> wide centred on <paramref name="centerX"/>, kept on the canvas.</summary>
    private static float LeftEdgeOnCanvas(float centerX, float width)
    {
        float half = width * 0.5f;
        float min = StageSideMargin + half;
        float max = SafeAreaCanvas.PortraitWidth - StageSideMargin - half;
        return (max < min ? SafeAreaCanvas.PortraitWidth * 0.5f : Math.Clamp(centerX, min, max)) - half;
    }

    public void Capture(Control creature, Control? hitbox, CombatReflection refl) =>
        _creatures.Add(creature.GetInstanceId(), CreatureDesktopState.Capture(creature, hitbox, refl));

    public void Queue(Control creature, float maxBarWidth, bool isAlly) =>
        _labelRequests.Add(new LabelRequest(creature, _creatures[creature.GetInstanceId()], maxBarWidth, isAlly));

    private sealed class CreatureDesktopState
    {
        private const string BoundsStandInName = "PortraitStateBounds";

        private readonly Vector2 _displayScale, _displayPivot;
        private readonly Vector2 _intentScale, _intentPivot;
        private readonly Vector2 _powerScale, _powerPosition, _powerPivot, _powerSize;
        private readonly float _barHitboxLeft, _barHitboxRight;
        // The bar's own place inside the health display: the band rule shifts its global Y every frame.
        private readonly Vector2 _hpBarPosition;
        private Control? _boundsStandIn;
        /// <summary>The intent row's icon count and scale as last laid out (see LayoutCombatIntents).</summary>
        public int LastIntentCount = -1;
        public float LastIntentScale = -1f;
        public float LastIntentWidth = -1f;

        /// <summary>The power container's child count as last walked for amount-label enlarging, and whether its add signal
        /// fired since: the walk is a Godot array per creature per frame, and an icon can only appear with a count change
        /// or an add.</summary>
        public int LastPowerChildCount = -1;
        public bool PowerChildrenDirty;

        /// <summary>The status block last written to the power container; <see cref="StatusBlock.Applied"/> is false until
        /// the first write, and a block is only laid out again when the container no longer carries it.</summary>
        public StatusBlock AppliedStatus;

        /// <summary>
        /// The block-shield rule's cache: the HP label it rules, its native place, the shield and the shield's two nodes,
        /// the measured glyph ink of the digits and of the block number, and the last capped report. The nodes and the two
        /// measurements were looked up and shaped again on every frame of the fight; the bar, shield and label geometry is
        /// still read live, so a sliding shield is followed exactly.
        /// </summary>
        public Label? HpRuleLabel;
        public Control? HpBlockShield;
        public TextureRect? HpBlockIcon;
        public Label? HpBlockLabel;
        public float HpNativeX, HpNativeScale;
        public GlyphInk HpInk, HpBlockInk;
        public string? HpCapped;

        /// <summary>A label's glyph ink as last measured, keyed by the text, the label's rect and the theme font size (all
        /// exact): its advance at that size, its ascent, and the outline, placed by the label's alignment inside the rect.</summary>
        public struct GlyphInk
        {
            public string? Text;
            public Vector2 Box;
            public int FontSize;
            public Rect2 Ink;
            public bool Known;
        }

        /// <summary>What <c>NPowerContainer.SetCreatureBounds</c> and the icon scale were given, and what they left behind.</summary>
        public readonly record struct StatusBlock(float Left, float IconWidth, float ContainerScaleX, int Columns, int Count,
            bool Applied);

        /// <summary>Whether the container still carries exactly the block <paramref name="icons"/> describes.</summary>
        public bool StatusBlockIsApplied(Control powerContainer, StatusIconLayout icons, int powerCount)
        {
            StatusBlock applied = AppliedStatus;
            if (!applied.Applied || applied.Count != powerCount || applied.Columns != icons.Columns) return false;
            if (MathF.Abs(applied.Left - icons.Left) > 0.01f || MathF.Abs(applied.IconWidth - icons.IconWidth) > 0.01f)
                return false;
            if (!powerContainer.IsVisibleInTree()) return false;
            return MathF.Abs(powerContainer.Scale.X - applied.ContainerScaleX) <= 0.002f;
        }

        /// <summary>After the native layout call: what the container now carries.</summary>
        public void RememberStatusBlock(Control powerContainer, StatusIconLayout icons, int powerCount) =>
            AppliedStatus = new StatusBlock(icons.Left, icons.IconWidth, powerContainer.Scale.X, icons.Columns, powerCount,
                true);

        /// <summary>
        /// What the game adds to the hit box's width for this creature's health bar (<c>NHealthBar.UpdateLayoutForCreatureBounds</c>):
        /// nothing for a player, 24 for a monster less the monster's own reduction.
        /// </summary>
        public readonly float BarExtraWidth;

        /// <summary>The game's layout call failed for this creature: its health display stays as the game draws it.</summary>
        public bool StateDisplayLayoutFailed { get; private set; }

        /// <summary>The creature's parts drawn larger in portrait: its health display and what that holds, its hit box and its intents.</summary>
        public readonly Control? Display, Hitbox, Intents, Nameplate, HpBar, HpBarHitbox, PowerContainer;

        private CreatureDesktopState(Control creature, Control? hitbox, CombatReflection refl)
        {
            // In the creature's scene the health display (NCreatureStateDisplay) is the node named HealthBar; inside it the
            // bar itself (NHealthBar) has that name again.
            Display = creature.GetNodeOrNull<Control>("%HealthBar") ?? creature.GetNodeOrNull<Control>("HealthBar");
            Hitbox = hitbox;
            Intents = creature.GetNodeOrNull<Control>("%Intents") ?? creature.GetNodeOrNull<Control>("Intents");
            Control? healthBar = Display?.GetNodeOrNull<Control>("%HealthBar") ?? Display?.GetNodeOrNull<Control>("HealthBar");
            HpBar = healthBar?.GetNodeOrNull<Control>("%HpBarContainer") ?? healthBar?.GetNodeOrNull<Control>("HpBarContainer");
            HpBarHitbox = Display?.GetNodeOrNull<Control>("%HpBarHitbox") ?? healthBar?.GetNodeOrNull<Control>("HpBarHitbox");
            Nameplate = Display?.GetNodeOrNull<Control>("%NameplateContainer") ?? Display?.GetNodeOrNull<Control>("NameplateContainer");
            PowerContainer = Display?.GetNodeOrNull<Control>("%PowerContainer") ?? Display?.GetNodeOrNull<Control>("PowerContainer");

            _displayScale = Display?.Scale ?? Vector2.One;
            _displayPivot = Display?.PivotOffset ?? Vector2.Zero;
            _intentScale = Intents?.Scale ?? Vector2.One;
            _intentPivot = Intents?.PivotOffset ?? new Vector2(500f, 20f);
            _powerScale = PowerContainer?.Scale ?? Vector2.One;
            _powerPosition = PowerContainer?.Position ?? Vector2.Zero;
            _powerPivot = PowerContainer?.PivotOffset ?? Vector2.Zero;
            _powerSize = PowerContainer?.Size ?? Vector2.Zero;
            _barHitboxLeft = HpBarHitbox?.OffsetLeft ?? -138f;
            _barHitboxRight = HpBarHitbox?.OffsetRight ?? 120f;
            _hpBarPosition = HpBar?.Position ?? Vector2.Zero;
            BarExtraWidth = refl.GetHealthBarExtraWidth(creature);
        }

        public static CreatureDesktopState Capture(Control creature, Control? hitbox, CombatReflection refl) =>
            new(creature, hitbox, refl);

        /// <summary>
        /// The hidden control that stands in for the creature's hit box when the health display is laid out again: the
        /// game reads its left edge on the canvas and its width. A child of the creature, so it goes when the creature
        /// does; null while the creature cannot take a child yet.
        /// </summary>
        public Control? EnsureBoundsStandIn(Control creature)
        {
            if (_boundsStandIn is { } known && GodotObject.IsInstanceValid(known) && known.IsInsideTree())
                return known;
            if (!creature.IsInsideTree() || !creature.IsNodeReady())
                return null;

            var standIn = new Control
            {
                Name = BoundsStandInName,
                Visible = false,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                FocusMode = Control.FocusModeEnum.None,
            };
            creature.AddChild(standIn);
            if (!standIn.IsInsideTree())
            {
                standIn.Free();
                return null;
            }
            _boundsStandIn = standIn;
            return standIn;
        }

        public void GiveUpStateDisplay(Control creature, CombatReflection refl)
        {
            StateDisplayLayoutFailed = true;
            RestoreStateDisplay();
            try
            {
                refl.InvokeUpdateBounds(creature);
            }
            catch (Exception)
            {
                // The game's own layout fails for this creature too: nothing more to put back.
            }
        }

        private void RestoreStateDisplay()
        {
            if (Display != null && GodotObject.IsInstanceValid(Display))
            {
                Display.Scale = _displayScale;
                Display.PivotOffset = _displayPivot;
            }
            if (HpBar != null && GodotObject.IsInstanceValid(HpBar))
                HpBar.Position = _hpBarPosition;
            if (HpBarHitbox != null && GodotObject.IsInstanceValid(HpBarHitbox))
            {
                HpBarHitbox.OffsetLeft = _barHitboxLeft;
                HpBarHitbox.OffsetRight = _barHitboxRight;
            }
            if (PowerContainer != null && GodotObject.IsInstanceValid(PowerContainer))
            {
                PowerContainer.Scale = _powerScale;
                PowerContainer.PivotOffset = _powerPivot;
                PowerContainer.Size = _powerSize;
                PowerContainer.Position = _powerPosition;
            }
            if (_boundsStandIn != null && GodotObject.IsInstanceValid(_boundsStandIn))
                _boundsStandIn.QueueFree();
            _boundsStandIn = null;
        }

        public void Restore(Control creature, CombatReflection refl, PortraitCombatStage stage, Action<Control, CombatReflection> restoreOrbs)
        {
            // The larger health display and intents first: the game lays the display out again from the hit box below.
            RestoreStateDisplay();
            if (Intents != null && GodotObject.IsInstanceValid(Intents))
            {
                Intents.Scale = _intentScale;
                Intents.PivotOffset = _intentPivot;
            }

            stage.RestoreCreatureRoot(creature);
            restoreOrbs(creature, refl);
            // The block-shield rule may have moved and shrunk the HP number: the game's own bar gets its label back.
            if (HpBar is { } hpBar && GodotObject.IsInstanceValid(hpBar) &&
                hpBar.GetNodeOrNull<Label>("%HpLabel") is { } hpLabel)
            {
                if (hpLabel.HasMeta("portrait_hp_x"))
                {
                    hpLabel.Position = new Vector2(hpLabel.GetMeta("portrait_hp_x").AsSingle(), hpLabel.Position.Y);
                    hpLabel.RemoveMeta("portrait_hp_x");
                }
                if (hpLabel.HasMeta("portrait_hp_scale"))
                {
                    float hpScale = hpLabel.GetMeta("portrait_hp_scale").AsSingle();
                    hpLabel.Scale = new Vector2(hpScale, hpScale);
                    hpLabel.RemoveMeta("portrait_hp_scale");
                }
            }
            if (creature.HasMeta("portrait_hp_shift_capped"))
                creature.RemoveMeta("portrait_hp_shift_capped");
            stage.RestoreCreatureBounds(creature, refl);
        }
    }
}
