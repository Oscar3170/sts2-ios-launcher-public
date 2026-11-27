using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.addons.mega_text;
using STS2Mobile.GameHost;
using STS2Mobile.Platform;
using STS2Mobile.DesktopHost;

namespace STS2Mobile.DesktopHost.Harness;

/// <summary>
/// The launcher's answers for the shared <c>event</c>, <c>event-popup</c> and <c>tour-*</c> scenarios (L7), measured against the
/// batch contract: the subject-aware art crop and its fade, the translucent text slot with the fit-before-scroll font, four
/// full-size pinned rows whose choice scroll never selects on a drag, the options in place on the first drawn frame, the live
/// native VFX with only the desktop veil hidden, the room cover below the header, and no composition for custom or combat
/// events. Node names only (PortraitEventArt/-ArtFade/-TextSlot/-Choices/-RoomCover); no D-only symbols.
/// </summary>
internal sealed partial class TestDriver
{
    private const float LauncherEventTallRow = 154f;
    private const float LauncherEventCompactRow = 133f;
    private const float LauncherEventTightRow = 127f;
    /// <summary>
    /// The smallest fitted copy: 44 reference units at 1680 = 28 launcher units = 9.7 points, the size the layout stops
    /// shrinking at before the slot scrolls.
    /// </summary>
    private const float LauncherEventMinTextFont = 28f;
    /// <summary>
    /// The title keeps the reference +20 u (1680) over the copy: 28 + 13 launcher units at the smallest fit.
    /// </summary>
    private const float LauncherEventTitleFont = LauncherEventMinTextFont + 13f;
    /// <summary>
    /// The copy slot must show a title and at least two body lines at <see cref="LauncherEventMinTextFont"/>, or the page is
    /// not readable without scrolling before the player has even seen the choices. Title 41 u with 1.2 leading (~49) + copy
    /// gap 8 + two 28 u lines with 1.2 leading (67) + ink gutters 4 + panel insets 26 = 154 u, rounded up to 160 u (55.6 pt).
    /// </summary>
    private const float LauncherEventCopySlotFloor = 160f;
    /// <summary>The absolute page budget, independent of the event layout math: bottom band, copy gap and header cap.</summary>
    private const float LauncherEventBottomInset = 150f;
    private const float LauncherEventTextGap = 35f;

    private readonly Dictionary<ulong, (float OffsetY, Vector2 Size)> _eventProbeRows = new();
    private Callable _eventProbeCallable;
    private bool _eventProbeConnected;
    private int _eventProbeJumps;
    private string _eventProbeWorst = "";
    private int _eventRevealFrames;
    private int _eventBarShownWhileRevealing;
    private int _eventRevealedFrames;
    private bool _eventCopyOverflows;
    private bool _eventBarShownAfterReveal;
    private string _eventRevealDetail = "";

    private partial string EventArtNodeName() => "PortraitEventArt";

    /// <summary>
    /// The launcher's gesture arbitration around a choice tap: the event scroll's drag flags (a stale one would clear the
    /// next press), the hit pad over the row, the other window-input handlers' armed/swallow flags, and the choices scroll.
    /// Read by reflection so the diagnostics need no debug API in the product code.
    /// </summary>
    private partial string EventTapHostDiagnostics(NEventOptionButton option)
    {
        var text = new System.Text.StringBuilder("pad=");
        Control? pad = PhoneHitArea.Of(option);
        text.Append(pad is null ? "none" : $"{pad.Name} rect={pad.GetGlobalRect()} filter={pad.MouseFilter}");
        try
        {
            object? wiring = LauncherHostWiring.Active;
            object? layout = FieldOf(wiring, "_portraitLayout");
            object? drag = FieldOf(FieldOf(FieldOf(layout, "_between"), "_events"), "_eventDrag");
            text.Append(" eventDrag=").Append(drag is null ? "none" : DescribeFields(drag,
                "_down", "_dragging", "_active", "_targets", "_layout", "_startY", "_initial"));
            text.Append(" swipeReleases=").Append(PhoneSwipeRelease.Released);
            object? touchScroll = FieldOf(wiring, "_touchScroll");
            text.Append(" touchScroll=").Append(touchScroll is null ? "none" : DescribeFields(touchScroll,
                "_down", "_scrolling", "_screenSwiped", "_view", "_gestureView", "_screen"));
            object? settings = FieldOf(layout, "_settings");
            text.Append(" settings=").Append(settings is null ? "none" : DescribeFields(settings,
                "_swallowing", "_swallowTouchPress", "_swallowTouchRelease", "_swallowMousePress", "_swallowMouseRelease",
                "_down", "_dragging", "_screens"));
            if (NEventRoom.Instance?.Layout?.GetNodeOrNull<ScrollContainer>(EventChoicesNodeName()) is { } choices)
                text.Append($" choices=[offset={choices.ScrollVertical} filter={choices.MouseFilter} ")
                    .Append($"bar={choices.GetVScrollBar().Visible} rect={choices.GetGlobalRect()}]");
        }
        catch (Exception error)
        {
            text.Append(" host-diagnostics-error=").Append(error.Message);
        }
        return text.ToString();
    }

    private static object? FieldOf(object? target, string name) => target?.GetType()
        .GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
                         System.Reflection.BindingFlags.NonPublic)?.GetValue(target);

    private static string DescribeFields(object target, params string[] names)
    {
        var text = new System.Text.StringBuilder("{");
        foreach (string name in names)
        {
            object? value = FieldOf(target, name);
            if (value is System.Collections.ICollection collection) value = $"count={collection.Count}";
            text.Append(name).Append('=').Append(value ?? "null").Append(' ');
        }
        return text.Append('}').ToString();
    }

    /// <summary>
    /// The row height a page must reach. Full-size rows (53.5/46 pt) are required for one to four choices. At 1080x2340
    /// (0.069 top inset, cap 162, header bottom 390) the 160 u copy floor and 35 u gap leave 1605 u above the 150 u bottom
    /// band: four 154 u rows plus three 15 u gaps need only 661 u. At 1080x1920 compact (no inset, cap 0, header bottom
    /// 228) the same budget is 1347 u and four 133 u rows plus three 12 u gaps need 568 u. The 44 pt (127 u) fallback is therefore allowed only for a four-option
    /// page when the budget cannot hold four full rows; one to three choices always keep the full row. A page with more
    /// than four rows scrolls and uses the tight row.
    /// </summary>
    private float LauncherEventRowFloor(int count, Vector2 canvas, bool compact)
    {
        float full = compact ? LauncherEventCompactRow : LauncherEventTallRow;
        if (count < 4) return full;
        if (count > 4) return LauncherEventTightRow;
        float rows = 4f * full + 3f * (compact ? 12f : 15f);
        float budget = canvas.Y - LauncherEventBottomInset - LauncherMapHeaderBottom(_tree.Root)
            - LauncherEventCopySlotFloor - LauncherEventTextGap;
        return rows <= budget + 0.5f ? full : LauncherEventTightRow;
    }

    private partial string EventArtFadeNodeName() => "PortraitEventArtFade";

    private partial string EventTextSlotNodeName() => "PortraitEventTextSlot";

    private partial string EventChoicesNodeName() => "PortraitEventChoices";

    private partial string EventRoomCoverNodeName() => "PortraitEventRoomCover";

    private partial float HoverTipMinScale() =>
        SafeAreaCanvas.IsPortrait ? PhoneHoverTips.PortraitTipScale - 0.05f : 0f;

    private partial void CheckEventOptionMinimums(IReadOnlyList<NEventOptionButton> options)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        Vector2 canvas = GetViewportSize();
        bool compact = canvas.Y / canvas.X <= 16f / 9f + 0.001f;
        float floor = LauncherEventRowFloor(options.Count, canvas, compact);
        Rect2 safe = PortraitSafeRect();
        _report.Check("event_options_resized",
            options.Count > 0 && options.All(option => option.GetGlobalRect().Size.Y >= floor - 2f)
            && options.All(option => safe.Grow(1f).Encloses(option.GetGlobalRect())),
            $"heights={string.Join(",", options.Select(option => option.GetGlobalRect().Size.Y.ToString("F0")))} floor={floor:F0} compact={compact}");
    }

    private partial void CheckEventLabelFonts(IReadOnlyList<MegaRichTextLabel> labels)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        _report.Check("event_labels_readable",
            labels.Count > 0 && labels.All(label => label.GetThemeFontSize("normal_font_size") >= LauncherEventMinTextFont),
            $"fonts={string.Join(",", labels.Select(label => label.GetThemeFontSize("normal_font_size")))} min={LauncherEventMinTextFont:F1}");
    }

    private partial void CheckEventPage(NEventLayout layout, IReadOnlyList<NEventOptionButton> options,
        IReadOnlyList<MegaRichTextLabel> labels)
    {
        _ = labels;
        if (!SafeAreaCanvas.IsPortrait) return;
        Vector2 canvas = GetViewportSize();
        Rect2 safe = PortraitSafeRect();
        bool compact = canvas.Y / canvas.X <= 16f / 9f + 0.001f;
        float headerBottom = LauncherMapHeaderBottom(_tree.Root);

        // ---- room cover: the act's rest-site art behind the whole event so no band is empty.
        var cover = NEventRoom.Instance?.GetNodeOrNull<TextureRect>(EventRoomCoverNodeName());
        _report.Check("event_room_cover", cover is not null && cover.Texture is not null && cover.IsVisibleInTree()
            && cover.GetGlobalRect().Grow(2f).Encloses(new Rect2(Vector2.Zero, canvas)),
            $"cover={cover?.GetGlobalRect()} texture={cover?.Texture?.ResourcePath ?? "null"} canvas={canvas}");

        // The native portrait covers the transparent lower edge of the stone header.
        var art = layout.GetNodeOrNull<Control>(EventArtNodeName());
        var portrait = layout.GetNodeOrNull<TextureRect>("%Portrait");
        var choices = layout.GetNodeOrNull<ScrollContainer>(EventChoicesNodeName());
        Rect2 artRect = art?.GetGlobalRect() ?? default;
        _report.Check("event_art_clipped", art is { ClipContents: true } && portrait is not null && IsUnder(portrait, art),
            $"art={(art is null ? "missing" : $"{artRect} clip={art.ClipContents}")}");
        _report.Check("event_art_bleeds_behind_header", art is not null
            && headerBottom - artRect.Position.Y is >= 8f and <= 32f
            && artRect.Position.X <= 1f && artRect.End.X >= canvas.X - 2f,
            $"art={artRect} headerBottom={headerBottom:F0}");
        _report.Check("event_art_covers_frame", art is not null && portrait is not null
            && portrait.GetGlobalRect().Grow(1f).Encloses(artRect),
            $"portrait={portrait?.GetGlobalRect()} stretch={portrait?.StretchMode} art={artRect}");
        _report.Check("event_art_ends_above_choices", choices is null || artRect.End.Y <= choices.GetGlobalRect().Position.Y + 1f,
            $"artBottom={artRect.End.Y:F0} choiceTop={choices?.GetGlobalRect().Position.Y}");

        // ---- fade: a gradient from the art's last 14.5 % into the canvas bottom, above the art, below the controls.
        var fade = layout.GetNodeOrNull<TextureRect>(EventArtFadeNodeName());
        Rect2 fadeRect = fade?.GetGlobalRect() ?? default;
        _report.Check("event_art_fades_into_choices", fade is not null && fade.Texture is GradientTexture2D
            && fade.IsVisibleInTree() && fadeRect.Position.Y <= artRect.End.Y + 1f && fadeRect.End.Y >= canvas.Y - 2f
            && Mathf.Abs(fadeRect.Size.X - canvas.X) <= 2f && fade.GetIndex() > (art?.GetIndex() ?? -1),
            $"fade={fadeRect} texture={fade?.Texture?.GetType().Name ?? "null"} art={artRect}");

        // ---- text slot: a readable reserved area above the choices, translucent with rounded corners. The floor is
        // absolute (title + two body lines), not the layout's own 18 % calculation.
        var slot = layout.GetNodeOrNull<Control>(EventTextSlotNodeName());
        var panel = slot?.GetNodeOrNull<Panel>("Panel");
        var style = panel?.GetThemeStylebox("panel") as StyleBoxFlat;
        Rect2 slotRect = slot?.GetGlobalRect() ?? default;
        float firstOptionTop = options.Count > 0 ? options[0].GetGlobalRect().Position.Y : slotRect.End.Y;
        _report.Check("event_text_slot_present", slot is not null && panel is not null
            && style is not null && Mathf.Abs(style.BgColor.A - 0.58f) < 0.02f && style.CornerRadiusTopLeft >= 14,
            $"slot={slotRect} alpha={style?.BgColor.A:F2} radius={style?.CornerRadiusTopLeft}");
        _report.Check("event_text_slot_readable_floor", slot is not null
            && slotRect.Size.Y >= LauncherEventCopySlotFloor
            && slotRect.Position.Y >= headerBottom - 1f
            && slotRect.End.Y <= firstOptionTop + 1f,
            $"slot={slotRect} floor={LauncherEventCopySlotFloor:F0} headerBottom={headerBottom:F0} firstOptionTop={firstOptionTop:F0}");
        _report.Check("event_text_slot_inside_safe", slot is not null && safe.Grow(1f).Encloses(slotRect)
            && (choices is null || slotRect.End.Y <= choices.GetGlobalRect().Position.Y + 1f),
            $"slot={slotRect} safe={safe} choiceTop={choices?.GetGlobalRect().Position.Y}");
        var title = layout.GetNodeOrNull<Label>("%Title");
        var description = layout.GetNodeOrNull<RichTextLabel>("%EventDescription");
        _report.Check("event_copy_placed_in_slot", slot is not null && title is not null && description is not null
            && IsUnder(title, slot) && IsUnder(description, slot) && slotRect.Grow(1f).Encloses(title.GetGlobalRect()),
            $"title={title?.GetGlobalRect()} description={description?.GetGlobalRect()} slot={slotRect}");
        var textScroll = panel?.GetNodeOrNull<ScrollContainer>("Text");
        bool scrolling = textScroll?.GetVScrollBar().IsVisibleInTree() == true;
        bool copyFits = textScroll is not null && description is not null
            && description.GetContentHeight() <= textScroll.Size.Y + 1f;
        float descriptionFont = description?.GetThemeFontSize("normal_font_size") ?? 0f;
        _report.Check("event_copy_fits_or_scrolls", description is not null
            && descriptionFont >= LauncherEventMinTextFont
            && (copyFits || scrolling)
            && (textScroll is null || description.Size.X <= textScroll.Size.X + 1f),
            $"font={descriptionFont:F1} ink={description?.GetContentHeight():F0} box={textScroll?.Size} fits={copyFits} scroll={scrolling}");
        float titleFont = title?.GetThemeFontSize("font_size") ?? 0f;
        _report.Check("event_title_readable", titleFont >= LauncherEventTitleFont - 1f, $"titleFont={titleFont:F1}");

        // ---- choices: four full-size rows reserved low; more than four scroll; the reserve never moves a page.
        if (choices is null || options.Count == 0) return;
        Rect2 choicesRect = choices.GetGlobalRect();
        Rect2 clip = choicesRect.Intersection(safe).Grow(2f);
        float rowFloor = LauncherEventRowFloor(options.Count, canvas, compact);
        NEventOptionButton first = options[0];
        _report.Check("event_four_choice_rows_pinned", choices.ScrollVertical == 0
            && choicesRect.End.Y <= safe.End.Y - 20f
            && Mathf.Abs(first.GetGlobalRect().Position.Y - choicesRect.Position.Y) <= 4f
            && options.All(option => clip.Encloses(option.GetGlobalRect())),
            $"choices={choicesRect} first={first.GetGlobalRect()} offset={choices.ScrollVertical} rows={options.Count}");
        _report.Check("event_choice_rows_full_size", options.All(option => option.GetGlobalRect().Size.Y >= rowFloor - 2f),
            $"heights={string.Join(",", options.Select(option => option.GetGlobalRect().Size.Y.ToString("F0")))} floor={rowFloor:F0}");
        if (options.Count > 4)
        {
            VScrollBar bar = choices.GetVScrollBar();
            _report.Check("event_choices_scroll_for_extra_rows", bar.IsVisibleInTree() && bar.MaxValue > bar.Page,
                $"bar={bar.GetGlobalRect()} range={bar.MaxValue - bar.Page:F0}");
        }

        // ---- live VFX: the native particles stay; only the desktop copy veil is hidden. Many events ship no particle
        // nodes at all, so that page can only report the absence rather than fail a check about retained effects.
        var particles = FindAll<GpuParticles2D>(layout).ToArray();
        if (particles.Length == 0)
        {
            _report.Skip("event_native_effects_retained_without_desktop_veil",
                "this event's native scene has no particle nodes");
        }
        else
        {
            var veils = particles.Where(p => p.Texture?.ResourcePath.GetFile() == "dark_overlay_events.png").ToArray();
            _report.Check("event_native_effects_retained_without_desktop_veil",
                veils.All(p => !p.IsVisibleInTree()) && particles.Except(veils).Any(p => p.IsVisibleInTree()),
                $"particles={particles.Length} veils={veils.Length} visible native particle nodes={particles.Count(p => p.IsVisibleInTree())}");
        }
    }

    private partial void BeginEventLayoutProbe()
    {
        if (!SafeAreaCanvas.IsPortrait || _eventProbeConnected) return;
        _eventProbeRows.Clear();
        _eventProbeJumps = 0;
        _eventProbeWorst = "";
        _eventRevealFrames = 0;
        _eventBarShownWhileRevealing = 0;
        _eventRevealedFrames = 0;
        _eventCopyOverflows = false;
        _eventBarShownAfterReveal = false;
        _eventRevealDetail = "";
        _eventProbeCallable = Callable.From(ProbeEventFrame);
        _tree.Connect(SceneTree.SignalName.ProcessFrame, _eventProbeCallable);
        _eventProbeConnected = true;
    }

    /// <summary>
    /// One drawn frame of the event page: each option row's vertical offset from its own options column and its laid-out
    /// size, plus the copy's scrollbar against the game's own reveal. A row may slide in horizontally (the native AnimateIn)
    /// and the whole column may move with the page's reveal, but a row that changes its vertical place or its height after
    /// it was first drawn was laid out too late.
    /// </summary>
    private void ProbeEventFrame()
    {
        if (NEventRoom.Instance?.Layout is not { } layout) return;
        SampleEventReveal(layout);
        Control? container = layout.GetNodeOrNull<Control>("%OptionsContainer");
        if (container is null) return;
        float containerY = container.GetGlobalRect().Position.Y;
        foreach (NEventOptionButton row in layout.OptionButtons)
        {
            if (!GodotObject.IsInstanceValid(row) || !row.IsVisibleInTree() || row.Size.Y < 1f) continue;
            ulong id = row.GetInstanceId();
            Rect2 rect = row.GetGlobalRect();
            float offsetY = rect.Position.Y - containerY;
            if (!_eventProbeRows.TryGetValue(id, out var first))
            {
                _eventProbeRows[id] = (offsetY, rect.Size);
                continue;
            }
            if (Mathf.Abs(offsetY - first.OffsetY) > 1.5f || Mathf.Abs(rect.Size.Y - first.Size.Y) > 1.5f)
            {
                _eventProbeJumps++;
                if (_eventProbeWorst.Length == 0)
                    _eventProbeWorst = $"row={row.Name} y {first.OffsetY:F0}->{offsetY:F0} height {first.Size.Y:F0}->{rect.Size.Y:F0}";
                _eventProbeRows[id] = (offsetY, rect.Size);
            }
        }
    }

    private async partial Task CheckEventLayoutProbeAsync(string phase)
    {
        if (!_eventProbeConnected) return;
        // An overflowing copy's bar only appears once the game has finished revealing the text: stay sampled until the
        // reveal ends (bounded) so the after state is observed too.
        if (_eventCopyOverflows && _eventRevealFrames > 0)
        {
            try
            {
                await WaitUntilAsync(() => EventCopyRevealed(), phase + " copy reveal", MediumWait);
            }
            catch (TimeoutException)
            {
                // Reported by the check below.
            }
            await WaitSecondsAsync(0.25);
        }
        else
        {
            await WaitSecondsAsync(0.35);
        }
        DisconnectEventProbe();
        _report.Check(phase + "_options_laid_out_before_first_drawn_frame",
            _eventProbeRows.Count >= 2 && _eventProbeJumps == 0,
            $"sampledRows={_eventProbeRows.Count} verticalOrHeightJumps={_eventProbeJumps} {_eventProbeWorst}");
        if (_eventCopyOverflows && _eventRevealFrames > 0)
        {
            _report.Check(phase + "_text_scrollbar_hidden_while_revealing", _eventBarShownWhileRevealing == 0,
                $"revealFrames={_eventRevealFrames} barShownFrames={_eventBarShownWhileRevealing} {_eventRevealDetail}");
            _report.Check(phase + "_text_scrollbar_appears_after_reveal",
                _eventRevealedFrames > 0 && _eventBarShownAfterReveal,
                $"revealedFrames={_eventRevealedFrames} barShownAfter={_eventBarShownAfterReveal} {_eventRevealDetail}");
        }
        else
        {
            _report.Skip(phase + "_text_scrollbar_reveal",
                $"the page's copy does not overflow (overflow={_eventCopyOverflows} revealFrames={_eventRevealFrames})");
        }
    }

    /// <summary>
    /// One frame of the copy's scrollbar against the game's reveal: a bar drawn while <c>visible_ratio</c> is still short of
    /// one is the defect, and a bar drawn once the reveal has finished proves it comes back for an overflowing copy.
    /// </summary>
    private void SampleEventReveal(NEventLayout layout)
    {
        ScrollContainer? scroll = layout.GetNodeOrNull<Control>(EventTextSlotNodeName())
            ?.GetNodeOrNull<ScrollContainer>("Panel/Text");
        RichTextLabel? description = layout.GetNodeOrNull<RichTextLabel>("%EventDescription");
        if (scroll == null || description == null) return;
        VScrollBar bar = scroll.GetVScrollBar();
        bool overflows = bar.MaxValue > bar.Page + 0.5f;
        if (overflows) _eventCopyOverflows = true;
        bool revealing = description.VisibleRatio < 0.999f;
        bool shown = bar.Visible && bar.IsVisibleInTree() && bar.Modulate.A > 0.01f;
        if (revealing)
        {
            _eventRevealFrames++;
            if (overflows && shown) _eventBarShownWhileRevealing++;
        }
        else if (overflows)
        {
            _eventRevealedFrames++;
            if (shown) _eventBarShownAfterReveal = true;
        }
        if (_eventRevealDetail.Length == 0 && overflows)
            _eventRevealDetail = $"overflow={overflows} barVisible={bar.Visible} alpha={bar.Modulate.A:F2} " +
                                 $"ratio={description.VisibleRatio:F2} range={bar.MaxValue - bar.Page:F0}";
    }

    private static bool EventCopyRevealed() =>
        NEventRoom.Instance?.Layout?.GetNodeOrNull<RichTextLabel>("%EventDescription") is not { } description
        || description.VisibleRatio >= 0.999f;

    private void DisconnectEventProbe()
    {
        if (!_eventProbeConnected) return;
        if (_tree.IsConnected(SceneTree.SignalName.ProcessFrame, _eventProbeCallable))
            _tree.Disconnect(SceneTree.SignalName.ProcessFrame, _eventProbeCallable);
        _eventProbeConnected = false;
    }

    private partial async Task CheckEventDragAsync(NEventRoom room, NEventLayout layout, IReadOnlyList<NEventOptionButton> options)
    {
        if (!SafeAreaCanvas.IsPortrait || options.Count == 0) return;
        string[] page = options.Select(option => option.Option.Title.GetRawText()).ToArray();
        bool Unchanged() => NEventRoom.Instance == room && ReferenceEquals(room.Layout, layout)
            && layout.OptionButtons.All(button => GodotObject.IsInstanceValid(button) && button.IsVisibleInTree())
            && layout.OptionButtons.Select(button => button.Option.Title.GetRawText()).SequenceEqual(page);

        var slot = layout.GetNodeOrNull<Control>(EventTextSlotNodeName());
        var textScroll = slot?.GetNodeOrNull<ScrollContainer>("Panel/Text");
        if (textScroll is not null)
        {
            int before = textScroll.ScrollVertical;
            Rect2 frame = textScroll.GetGlobalRect();
            await _pointer.DragAsync(new Vector2(frame.GetCenter().X, frame.End.Y - 20f),
                new Vector2(frame.GetCenter().X, frame.Position.Y + 20f), 20);
            await WaitFramesAsync(4);
            bool shortCopy = !textScroll.GetVScrollBar().IsVisibleInTree();
            _report.Check("event_text_drag_scrolls_not_selects",
                Unchanged() && (shortCopy || textScroll.ScrollVertical > before),
                $"offset {before}->{textScroll.ScrollVertical} shortCopy={shortCopy}");
            if (textScroll.ScrollVertical > 0)
            {
                Rect2 back = textScroll.GetGlobalRect();
                await _pointer.DragAsync(new Vector2(back.GetCenter().X, back.Position.Y + 20f),
                    new Vector2(back.GetCenter().X, back.End.Y - 20f), 20);
                await WaitFramesAsync(4);
            }
        }

        // A drag that starts on a choice row must scroll (or do nothing) and must never choose it.
        NEventOptionButton first = options[0];
        Rect2 row = first.GetGlobalRect();
        await _pointer.DragAsync(row.GetCenter(), row.GetCenter() + new Vector2(0f, 130f), 18);
        await WaitFramesAsync(4);
        _report.Check("event_choice_drag_never_selects", Unchanged(),
            $"room={NEventRoom.Instance == room} layout={ReferenceEquals(room.Layout, layout)} options={layout.OptionButtons.Count()}");
    }

    private partial void CheckEventLandscape(NEventLayout layout, ScrollContainer? choices, Control? textSlot)
    {
        _report.Check("event_landscape_keeps_native_composition", choices is null && textSlot is null,
            "portrait-only text and choice containers are absent");
        _report.Check("event_landscape_art_native",
            layout.GetNodeOrNull<Control>(EventArtNodeName()) is null
            && layout.GetNodeOrNull<Control>(EventArtFadeNodeName()) is null
            && layout.GetNodeOrNull<TextureRect>(EventRoomCoverNodeName()) is null,
            "no portrait art clip, fade or room cover");
    }

    private partial async Task CheckEventExclusionsAsync(RunState state)
    {
        _ = state;
        if (!SafeAreaCanvas.IsPortrait) return;

        // A custom-layout event owns its whole screen: no event composition may touch it.
        await RunManager.Instance.EnterRoomDebug(RoomType.Event, model: ModelDb.Event<FakeMerchant>(), showTransition: false);
        await WaitUntilAsync(() => NEventRoom.Instance is { } room
                && (room.Layout is null || room.CustomEventNode is not null), "custom event room", MediumWait);
        await WaitSecondsAsync(1.0);
        NEventRoom custom = NEventRoom.Instance!;
        _report.Check("event_custom_layout_untouched", custom.Layout is null
            && custom.GetNodeOrNull<Control>(EventTextSlotNodeName()) is null
            && custom.GetNodeOrNull<Control>(EventChoicesNodeName()) is null
            && custom.GetNodeOrNull<Control>(EventArtNodeName()) is null,
            $"layout={custom.Layout?.GetType().Name ?? "custom"} customNode={custom.CustomEventNode?.GetType().Name}");

        // The live tableau stays native, but its copy and choices use the portrait event sizes.
        await RunManager.Instance.EnterRoomDebug(RoomType.Event, model: ModelDb.Event<PunchOff>(), showTransition: false);
        await WaitUntilAsync(() => NEventRoom.Instance?.Layout is NCombatEventLayout, "combat event layout", MediumWait);
        await WaitSecondsAsync(1.5);
        NEventLayout combat = NEventRoom.Instance!.Layout!;
        var copy = combat.GetNodeOrNull<RichTextLabel>("%EventDescription");
        await WaitUntilAsync(() => copy is not null && copy.IsVisibleInTree()
            && copy.VisibleRatio >= 0.99f && copy.Modulate.A >= 0.99f, "Punch Off description revealed", MediumWait);
        Rect2 safe = PortraitSafeRect();
        Vector2 canvas = GetViewportSize();
        bool compact = canvas.Y / canvas.X <= 16f / 9f + 0.001f;
        _report.Check("event_combat_layout_keeps_tableau", combat is NCombatEventLayout
            && combat.GetNodeOrNull<Control>(EventTextSlotNodeName()) is null
            && combat.GetNodeOrNull<Control>(EventChoicesNodeName()) is null
            && combat.GetNodeOrNull<Control>(EventArtNodeName()) is null,
            $"layout={combat.GetType().Name}");
        Rect2 copyRect = copy!.GetGlobalRect();
        float optionTop = combat.OptionButtons.Min(button => button.GetGlobalRect().Position.Y);
        float gap = optionTop - copyRect.End.Y;
        _report.Check("event_punch_off_description_above_choices",
            safe.Grow(2f).Encloses(copyRect) && gap is >= 0f and <= 96f,
            $"copy={copyRect} firstChoiceTop={optionTop:F1} gap={gap:F1}");
        float minimumRow = compact ? LauncherEventCompactRow : LauncherEventTallRow;
        _report.Check("event_punch_off_choices_phone_sized_and_reachable",
            combat.OptionButtons.All(button => button.IsVisibleInTree()
                && safe.Grow(2f).Encloses(button.GetGlobalRect())
                && button.GetGlobalRect().Size.Y >= minimumRow - 2f
                && button.GetNode<RichTextLabel>("%Text").GetThemeFontSize("normal_font_size") >= 39)
            && optionTop < canvas.Y * 0.75f,
            $"options={string.Join(", ", combat.OptionButtons.Select(button => button.GetGlobalRect()))} minHeight={minimumRow} canvas={canvas}");
        Rect2 tableau = ((NCombatEventLayout)combat).EmbeddedCombatRoom!.CreatureNodes
            .Select(creature => creature.Hitbox.GetGlobalRect()).Aggregate((a, b) => a.Merge(b));
        _report.Check("event_punch_off_copy_readable_and_clear_of_combatants",
            copy.GetThemeFontSize("normal_font_size") >= LauncherEventMinTextFont
            && copy.GetContentHeight() <= copy.Size.Y + 1f && copyRect.Position.Y >= tableau.End.Y - 2f,
            $"copy={copyRect} font={copy.GetThemeFontSize("normal_font_size")} tableau={tableau}");
        var constructs = ((NCombatEventLayout)combat).EmbeddedCombatRoom!.CreatureNodes
            .Where(creature => creature.Entity.IsMonster).OrderBy(creature => creature.GlobalPosition.X).ToArray();
        _report.Check("event_punch_off_constructs_face_each_other",
            constructs.Length == 2 && constructs[0].Scale.X < 0f && constructs[1].Scale.X > 0f,
            $"left-to-right scaleX={string.Join(", ", constructs.Select(creature => creature.Scale.X))}");
        await CaptureAsync("event_03_punch_off");
        await CheckPunchOffCombatEntryAsync((NCombatEventLayout)combat, copy);
    }

    private async Task CheckPunchOffCombatEntryAsync(NCombatEventLayout layout, RichTextLabel copy)
    {
        NEventOptionButton takeThem = layout.OptionButtons.Single(button =>
            button.Option.Title.LocEntryKey == "PUNCH_OFF.pages.INITIAL.options.I_CAN_TAKE_THEM.title");
        await TapEventOptionWithDiagnosticsAsync("event_punch_off_take_them", takeThem);
        NEventOptionButton? fight = null;
        await WaitUntilAsync(() => (fight = layout.OptionButtons.FirstOrDefault(button =>
            button.Option.Title.LocEntryKey == "PUNCH_OFF.pages.I_CAN_TAKE_THEM.options.FIGHT.title")) is not null,
            "Punch Off reveals its Fight choice", MediumWait);
        await WaitForEventResultRevealAsync(layout, fight!);
        var options = layout.GetNode<Control>("%OptionsContainer");
        int frames = 0, eventFlashFrames = 0;
        bool combatDrawn = false;
        void Sample()
        {
            frames++;
            if (!GodotObject.IsInstanceValid(layout) || !layout.HasCombatStarted) return;
            combatDrawn = true;
            if ((GodotObject.IsInstanceValid(copy) && copy.IsVisibleInTree()) ||
                (GodotObject.IsInstanceValid(options) && options.IsVisibleInTree()))
                eventFlashFrames++;
        }

        RenderingServer.FramePostDraw += Sample;
        NCombatRoom room;
        try
        {
            await TapEventOptionWithDiagnosticsAsync("event_punch_off_fight", fight!);
            await WaitUntilAsync(() => layout.HasCombatStarted && CombatManager.Instance.IsInProgress
                && IsPlayerPlayPhase() && NCombatRoom.Instance?.Ui?.Hand?.ActiveHolders.Count > 0,
                "Punch Off opens embedded combat with a playable hand", MediumWait);
            room = NCombatRoom.Instance!;
            await WaitForCombatPilesEndTurnAsync(room.Ui.EndTurnButton);
            await WaitUntilAsync(() => StageEnemies(room).Count == 2 && StageEnemies(room).All(creature => creature.Scale.X > 0f),
                "Punch Off restores both combatants' native combat facing", ShortWait);
            await WaitFramesAsync(4);
        }
        finally
        {
            RenderingServer.FramePostDraw -= Sample;
        }

        _report.Check("event_punch_off_transition_clears_event_ui", combatDrawn && eventFlashFrames == 0,
            $"drawnFrames={frames} eventVisibleAfterCombatStart={eventFlashFrames}");
        await CaptureAsync("event_04_punch_off_combat");

        var hand = room.Ui.Hand;
        var attack = hand.ActiveHolders.FirstOrDefault(IsTargetedPlayableCard);
        if (attack is not null)
        {
            int before = hand.ActiveHolders.Count;
            await _pointer.DragAsync(CenterOf(attack.Hitbox), CenterOf(StageEnemies(room)[0].Hitbox), 16);
            await WaitUntilAsync(() => hand.ActiveHolders.Count < before, "Punch Off accepts a card drag", ShortWait);
            _report.Pass("event_punch_off_card_played", $"hand={before}->{hand.ActiveHolders.Count}");
        }
        else
        {
            _report.Fail("event_punch_off_card_played", "opening hand has no playable targeted card");
        }
        await _pointer.TapAsync(CenterOf(room.Ui.EndTurnButton));
        await WaitUntilAsync(() => CombatManager.Instance.PlayerActionsDisabled, "Punch Off End Turn advances combat", ShortWait);
        await WaitThroughEnemyTurnAsync("Punch Off");
        _report.Pass("event_punch_off_end_turn", "enemy turn completed and player actions resumed");
    }
}
