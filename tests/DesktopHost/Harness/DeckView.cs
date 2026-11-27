using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Runs;

namespace STS2Mobile.DesktopHost.Harness;

internal sealed partial class TestDriver
{
    /// <summary>
    /// The grid's scroll content. The deck view instances its grid with editable children, so the native unique name does
    /// not resolve from the grid there; the launcher's own pass looks it up both ways for the same reason.
    /// </summary>
    private static Control GridScrollContent(NCardGrid grid) =>
        grid.GetNodeOrNull<Control>("%ScrollContainer") ?? grid.GetNodeOrNull<Control>("ScrollContainer")
        ?? throw new InvalidOperationException($"{grid.Name} has no ScrollContainer");

    /// <summary>
    /// The card a grid holder draws, in global (canvas) units. A grid holder is zero-sized and centres its <c>NCard</c> on
    /// its origin, and Godot's <c>Control.GetGlobalRect()</c> reports an unscaled local size, so the holder or hitbox rect is
    /// the card's small native rect rather than the drawn one. The card's own default size through its global scale is the
    /// drawn rect, centred on the card's global position — the same measurement the selection-screen preview checks use.
    /// </summary>
    private static Rect2 DrawnCardRect(NGridCardHolder holder)
    {
        Vector2 size = NCard.defaultSize * holder.GetGlobalTransform().Scale;
        Vector2 centre = holder.GetGlobalRect().GetCenter();
        return new Rect2(centre - size * 0.5f, size);
    }

    private static Rect2 LastDrawnCardRect(NCardGrid grid) =>
        grid.GetCardHolder(grid.SortedCards[^1]) is { } holder ? DrawnCardRect(holder) : default;

    /// <summary>
    /// The grid's own bottom scroll limit. It is not simply the viewport-to-content difference: the game sizes the scroll
    /// content with its bottom padding and clamps to <c>ScrollLimitBottom</c>. Read the grid's live value so the host
    /// reports the limit the game actually applies.
    /// </summary>
    private static float GridScrollLimitBottom(NCardGrid grid)
    {
        if (AccessTools.Property(grid.GetType(), "ScrollLimitBottom")?.GetValue(grid) is float native) return native;
        Control content = GridScrollContent(grid);
        float scale = content.GetGlobalTransform().Scale.Y;
        float viewport = scale > 0.0001f ? grid.GetGlobalRect().Size.Y / scale : grid.GetGlobalRect().Size.Y;
        return Mathf.Min(0f, viewport - content.Size.Y);
    }

    private async Task CheckDeckSelectionScrollAsync(string name, NCardGrid grid, Control prompt, params Control[] previews)
    {
        if (SelectionScreenOf(grid) is { } selectionScreen) CheckScreenBacks(name, selectionScreen);
        var content = GridScrollContent(grid);
        Rect2 view = grid.GetGlobalRect();
        bool FullyInView(int index) => grid.GetCardHolder(grid.SortedCards[index]) is { } holder
            && holder.IsVisibleInTree() && view.Grow(2f).Encloses(DrawnCardRect(holder));
        bool NoPreview() => previews.All(p => !p.Visible) && NGame.Instance?.InspectCardScreen?.IsVisibleInTree() != true;
        float limit = GridScrollLimitBottom(grid);
        CheckGridStartsBelowHeader(name, view, Mathf.Abs(content.Position.Y) < 1f
            && Enumerable.Range(0, 3).All(FullyInView), content.Position.Y);
        _report.Check($"{name}_grid_ends_above_prompt", grid.ClipContents && view.End.Y < prompt.GetGlobalRect().Position.Y,
            $"grid={view} prompt={prompt.GetGlobalRect()}");
        // The prompt's dark box is the game's ColorRect behind its label, with 6 units of margin above and below: it must
        // hug the text, not stand twice as tall as it.
        if (prompt.FindChild("BottomLabel", true, false) is RichTextLabel promptLabel && promptLabel.IsVisibleInTree())
        {
            float text = promptLabel.GetContentHeight();
            _report.Check($"{name}_prompt_box_fits_text", prompt.Size.Y <= text + 12f + 8f,
                $"box={prompt.Size.Y:F1} text={text:F1} label={promptLabel.Size} min={promptLabel.GetCombinedMinimumSize()} " +
                $"font={promptLabel.GetThemeFontSize("normal_font_size")}");
        }
        // The row cut by the grid's clipped foot is the one the native recycler rebinds: watch every holder, not one picture.
        await CheckGridHoldersStableAsync($"{name}_top", grid);

        Vector2 top = view.Position + view.Size * new Vector2(0.48f, 0.18f);
        Vector2 bottom = view.Position + view.Size * new Vector2(0.48f, 0.80f);
        // Start on a card to verify a drag also cancels a pending selection.
        var first = grid.GetCardHolder(grid.SortedCards[1]);
        if (first is not null && FullyInView(1))
        {
            Vector2 start = CenterOf(first.Hitbox);
            await _pointer.DragAsync(start, new Vector2(start.X, view.Position.Y + 12f), steps: 12);
            await WaitSecondsAsync(0.45);
        }
        bool passed = content.Position.Y < -50f && limit < -400f && NoPreview();
        float reachedBottom = content.Position.Y;
        bool lastVisible = false;
        Rect2 lastRect = default;
        for (int pass = 0; pass < 2 && passed; pass++)
        {
            for (int i = 0; i < 14 && content.Position.Y - GridScrollLimitBottom(grid) > 2f && NoPreview(); i++)
            {
                await _pointer.DragAsync(bottom, top, steps: 14);
                await WaitSecondsAsync(0.4);
            }
            reachedBottom = content.Position.Y;
            // The game's bottom limit follows the grid's and content's live sizes, which settle after the first rows load.
            limit = GridScrollLimitBottom(grid);
            lastVisible = FullyInView(grid.SortedCards.Count - 1);
            lastRect = LastDrawnCardRect(grid);
            passed &= Mathf.Abs(reachedBottom - limit) < 2f && lastVisible && NoPreview();
            if (pass == 0) await CaptureAsync($"{name}_grid_bottom");
            if (pass == 0) await CheckGridHoldersStableAsync($"{name}_bottom", grid);
            for (int i = 0; i < 14 && Mathf.Abs(content.Position.Y) > 1f && NoPreview(); i++)
            {
                await _pointer.DragAsync(top, bottom, steps: 14);
                await WaitSecondsAsync(0.4);
            }
            passed &= Mathf.Abs(content.Position.Y) < 1f && FullyInView(0) && NoPreview();
        }
        _report.Check($"{name}_swipe_to_both_ends_without_selecting", passed,
            $"offset={content.Position.Y} reached={reachedBottom} limit={limit:F1} last={lastRect} lastVisible={lastVisible} " +
            $"firstVisible={FullyInView(0)} preview={!NoPreview()}");
    }

    /// <summary>
    /// A slow finger scroll, sampled after every frame is drawn while the finger is down: every card whose place in the
    /// scrolled content falls inside the grid's view must have been drawn there, by a visible holder, with that card's own
    /// face. The rows at the grid's clipped foot are rebound by the game's recycler while the content moves; a row left on
    /// the wrong cards for the frame that is drawn reads as flicker. Sampling at the scene tree's process_frame cannot see
    /// it: that signal fires before any node's _Process, so a rebind the grid makes in its own _Process and a repair made at
    /// process_frame of the next frame both fall between two samples. A holder rebound after the frame's last deferred-call
    /// flush (from RenderingServer.FramePreDraw, say) moves at once but keeps drawing the previous card's face until its
    /// deferred redraw runs in the next frame: the device showed whole rows of the wrong cards. The face is read from each
    /// card's title label as it last drew itself (its draw signal), against the title it holds now.
    /// </summary>
    private async Task CheckGridDrawnWhileTouchScrolledAsync(string name, NCardGrid grid)
    {
        var content = GridScrollContent(grid);
        Rect2 view = grid.GetGlobalRect();
        var cards = grid.SortedCards;
        if (cards.Count < 13 || grid.GetCardHolder(cards[0]) is not { } first || grid.GetCardHolder(cards[3]) is not { } second)
        {
            _report.Fail($"{name}_touch_scroll_rows_drawn", $"needs 13+ cards with the first two rows bound: cards={cards.Count}");
            return;
        }
        float contentTop = content.GetGlobalRect().Position.Y;
        float rowTop = DrawnCardRect(first).Position.Y - contentTop;
        float pitch = DrawnCardRect(second).Position.Y - contentTop - rowTop;
        float height = DrawnCardRect(first).Size.Y;
        int missingFrames = 0, missing = 0, staleFaces = 0, windowMoves = 0, draggingFrames = 0;
        string firstMiss = "";
        var missedCards = new SortedDictionary<int, (int Frames, float WorstDy, bool Visible)>();
        var windowField = AccessTools.Field(grid.GetType(), "_slidingWindowCardIndex");
        var draggingField = AccessTools.Field(grid.GetType(), "_isDragging");
        int window = windowField?.GetValue(grid) is int w0 ? w0 : -1;
        // Each title label's text as it last drew: its draw signal runs inside its redraw, with the text it draws.
        var drawnTitles = new Dictionary<ulong, string>();
        var watchedTitles = new List<(Label Label, Callable Handler)>();
        string DrawnTitleMismatch(NCardHolder holder)
        {
            if (holder.CardNode?.GetNodeOrNull<Label>("%TitleLabel") is not { } title) return "";
            ulong id = title.GetInstanceId();
            if (!drawnTitles.ContainsKey(id))
            {
                drawnTitles[id] = title.Text;
                var handler = Callable.From(() => drawnTitles[id] = title.Text);
                title.Connect(CanvasItem.SignalName.Draw, handler);
                watchedTitles.Add((title, handler));
                return "";
            }
            return drawnTitles[id] == title.Text ? "" : $"drew '{drawnTitles[id]}' for '{title.Text}'";
        }
        foreach (NCardHolder holder in grid.CurrentlyDisplayedCardHolders) DrawnTitleMismatch(holder);
        void Sample(int frame)
        {
            float top = content.GetGlobalRect().Position.Y + rowTop;
            int frameMissing = 0;
            for (int k = 0; k < cards.Count; k++)
            {
                float y = top + (k / 3) * pitch;
                if (y + height < view.Position.Y + 2f || y > view.End.Y - 2f) continue;
                var holder = grid.GetCardHolder(cards[k]);
                bool placed = holder is not null && holder.IsVisibleInTree() && Mathf.Abs(DrawnCardRect(holder).Position.Y - y) < 2f;
                string stale = placed ? DrawnTitleMismatch(holder!) : "";
                if (placed && stale.Length == 0) continue;
                frameMissing++;
                if (stale.Length > 0) staleFaces++;
                float dy = holder is null ? float.NaN : DrawnCardRect(holder).Position.Y - y;
                missedCards.TryGetValue(k, out var seen);
                missedCards[k] = (seen.Frames + 1, float.IsNaN(dy) || Mathf.Abs(dy) > Mathf.Abs(seen.WorstDy) ? dy : seen.WorstDy,
                    holder?.IsVisibleInTree() ?? false);
                if (firstMiss.Length == 0)
                    firstMiss = $"frame={frame} card={k} expectedY={y:F0} " +
                        $"holder={(holder is null ? "none" : $"{DrawnCardRect(holder)} visible={holder.IsVisibleInTree()} {stale}")}";
            }
            missing += frameMissing;
            if (frameMissing > 0) missingFrames++;
            int now = windowField?.GetValue(grid) is int w ? w : -1;
            if (now != window) windowMoves++;
            window = now;
            if (draggingField?.GetValue(grid) is true) draggingFrames++;
        }
        // Start two rows down: the game's recycler can only move its window up once the window has left the first row.
        grid.SetScrollPosition(-2f * pitch);
        await WaitSecondsAsync(0.5);
        Vector2 start = view.Position + view.Size * new Vector2(0.5f, 0.8f);
        Vector2 end = start - new Vector2(0f, view.Size.Y * 0.6f);
        float before = content.Position.Y;
        await _pointer.PressAsync(start);
        int frames = 0;
        Action sampleDrawn = () => Sample(frames++);
        RenderingServer.FramePostDraw += sampleDrawn;
        try
        {
            for (int step = 1; step <= 60; step++)
                await _pointer.MoveAsync(start.Lerp(end, step / 60f), pressed: true);
            await WaitFramesAsync(20);
        }
        finally
        {
            RenderingServer.FramePostDraw -= sampleDrawn;
            foreach ((Label title, Callable handler) in watchedTitles)
                if (GodotObject.IsInstanceValid(title) && title.IsConnected(CanvasItem.SignalName.Draw, handler))
                    title.Disconnect(CanvasItem.SignalName.Draw, handler);
        }
        await _pointer.ReleaseAsync(end);
        float moved = before - content.Position.Y;
        // The window only needs to move once per row scrolled; moving back and forth every frame rebinds the rows at the
        // grid's foot under the finger (the device's flicker on the fifth row), even when a sample misses the frame it shows.
        int windowBound = Mathf.CeilToInt(moved / pitch) + 1;
        _report.Check($"{name}_touch_scroll_rows_drawn", missingFrames == 0 && moved > 100f && windowMoves <= windowBound,
            $"missingFrames={missingFrames}/{frames} missing={missing} staleFaces={staleFaces} from={before:F0} moved={moved:F0} pitch={pitch:F0} " +
            $"windowMoves={windowMoves}/{windowBound} window={window} dragging={draggingFrames} {firstMiss} " +
            $"missed=[{string.Join(" ", missedCards.Select(m => $"{m.Key}:{m.Value.Frames}f/{m.Value.WorstDy:F1}{(m.Value.Visible ? "" : "/hidden")}"))}]");
        await WaitSecondsAsync(0.4);
        grid.SetScrollPosition(0f);
        await WaitSecondsAsync(0.4);
    }

    /// <summary>
    /// A swipe that starts on one card and lifts, then a swipe that starts on another: the first card must keep its drawn
    /// size and colour on every drawn frame of the second swipe. The finger's emulated mouse leaves the first card only when
    /// the second press lands, which is when the game's unhover runs on it (the device showed it flicker as if unhovered).
    /// </summary>
    private async Task CheckGridCardStillOnNextSwipeAsync(string name, NCardGrid grid)
    {
        // Landscape keeps the game's own hover and unhover animation.
        if (!IsPortraitHost()) return;
        Rect2 view = grid.GetGlobalRect();
        grid.SetScrollPosition(0f);
        await WaitSecondsAsync(0.5);
        // The first card has keyword tips (a Defend's Block): on the device its tip flashed as the next swipe touched down
        // on another card. The second is any card a row away from it.
        List<NGridCardHolder> shown = grid.CurrentlyDisplayedCardHolders.OfType<NGridCardHolder>()
            .Where(holder => holder.IsVisibleInTree() && view.Grow(-40f).HasPoint(DrawnCardRect(holder).GetCenter()))
            .ToList();
        NGridCardHolder? first = shown.FirstOrDefault(holder => holder.CardModel is { } model && model.HoverTips.Any());
        NGridCardHolder? second = first == null ? null : shown.LastOrDefault(holder =>
            Mathf.Abs(DrawnCardRect(holder).GetCenter().Y - DrawnCardRect(first).GetCenter().Y) > 10f);
        if (first == null || second == null)
        {
            _report.Fail($"{name}_card_still_on_next_swipe", $"needs a card with keyword tips and another row in view: " +
                $"shown={shown.Count} first={first?.Name} second={second?.Name}");
            return;
        }
        Vector2 firstStart = DrawnCardRect(first).GetCenter();
        // A finger rests a few frames before it moves; any keyword tip drawn in a swipe is a flash (the device's recording
        // showed one for about 100 ms as the second swipe touched down).
        bool AnyTip() => FindAll<NHoverTipSet>(_tree.Root).Any(tip => GodotObject.IsInstanceValid(tip) && tip.IsVisibleInTree());
        int tipFrames = 0;
        Action sampleTips = () => { if (AnyTip()) tipFrames++; };
        // The second touch lands as on a phone: the emulated mouse is still where the first one lifted.
        async Task<int> SwipeAsync(Vector2 from, bool asPhone)
        {
            tipFrames = 0;
            RenderingServer.FramePostDraw += sampleTips;
            try
            {
                if (asPhone) await _pointer.PressWhereLastLiftedAsync(from);
                else await _pointer.PressAsync(from);
                await WaitFramesAsync(4);
                for (int step = 1; step <= 12; step++)
                    await _pointer.MoveAsync(from - new Vector2(0f, view.Size.Y * 0.1f * step / 12f), pressed: true);
                await _pointer.ReleaseAsync(from - new Vector2(0f, view.Size.Y * 0.1f));
                await WaitSecondsAsync(0.7);
            }
            finally
            {
                RenderingServer.FramePostDraw -= sampleTips;
            }
            return tipFrames;
        }
        int firstTips = await SwipeAsync(firstStart, asPhone: false);
        await WaitSecondsAsync(0.3);
        if (!GodotObject.IsInstanceValid(first) || !GodotObject.IsInstanceValid(second) ||
            !view.Grow(-30f).HasPoint(DrawnCardRect(second).GetCenter()))
        {
            _report.Fail($"{name}_card_still_on_next_swipe", $"the second card left the view: {second.Name}");
            return;
        }
        Vector2 secondStart = DrawnCardRect(second).GetCenter();
        Vector2 size = DrawnCardRect(first).Size;
        Color colour = first.CardNode?.Modulate ?? Colors.White;
        int frames = 0, changed = 0;
        float worstScale = 0f, worstColour = 0f;
        string firstChange = "";
        Action sample = () =>
        {
            frames++;
            if (!GodotObject.IsInstanceValid(first)) return;
            float scaleDelta = Mathf.Abs(DrawnCardRect(first).Size.X / size.X - 1f);
            Color now = first.CardNode?.Modulate ?? Colors.White;
            float colourDelta = Mathf.Max(Mathf.Max(Mathf.Abs(now.R - colour.R), Mathf.Abs(now.G - colour.G)),
                Mathf.Max(Mathf.Abs(now.B - colour.B), Mathf.Abs(now.A - colour.A)));
            worstScale = Mathf.Max(worstScale, scaleDelta);
            worstColour = Mathf.Max(worstColour, colourDelta);
            if (scaleDelta <= 0.002f && colourDelta <= 0.01f) return;
            changed++;
            if (firstChange.Length == 0)
                firstChange = $"frame={frames} size={DrawnCardRect(first).Size} scale={first.Scale.X:F3} modulate={now}";
        };
        RenderingServer.FramePostDraw += sample;
        int secondTips;
        try
        {
            secondTips = await SwipeAsync(secondStart, asPhone: true);
        }
        finally
        {
            RenderingServer.FramePostDraw -= sample;
        }
        _report.Check($"{name}_card_still_on_next_swipe", changed == 0 && frames > 10 && firstTips == 0 && secondTips == 0,
            $"first={first.Name} second={second.Name} tipFrames={firstTips}/{secondTips} changedFrames={changed}/{frames} " +
            $"worstScale={worstScale:F3} worstColour={worstColour:F3} size={size} {firstChange}");
        grid.SetScrollPosition(0f);
        await WaitSecondsAsync(0.4);
    }

    /// <summary>
    /// With nothing scrolling, every shown holder of a grid keeps its card, its visibility and its place for 30 frames. The
    /// game's row recycler keys on screen coordinates; under a portrait header band both of its branches can fire at once and
    /// rebind or hide the row at the clipped foot every frame, which reads as flicker and is invisible to a single capture.
    /// </summary>
    private async Task CheckGridHoldersStableAsync(string name, NCardGrid grid)
    {
        var content = GridScrollContent(grid);
        await WaitSecondsAsync(0.3);
        var holders = grid.CurrentlyDisplayedCardHolders.ToList();
        var cards = holders.Select(h => h.CardModel).ToArray();
        var shown = holders.Select(h => h.IsVisibleInTree()).ToArray();
        var places = holders.Select(h => h.GetGlobalRect().Position).ToArray();
        float offset = content.Position.Y;
        int toggles = 0, rebinds = 0, moves = 0, scrolledFrames = 0;
        for (int frame = 0; frame < 30; frame++)
        {
            await WaitFramesAsync(1);
            bool scrolled = Mathf.Abs(content.Position.Y - offset) >= 0.5f;
            if (scrolled) scrolledFrames++;
            offset = content.Position.Y;
            for (int i = 0; i < holders.Count; i++)
            {
                if (!GodotObject.IsInstanceValid(holders[i])) { rebinds++; continue; }
                bool visible = holders[i].IsVisibleInTree();
                Vector2 place = holders[i].GetGlobalRect().Position;
                if (visible != shown[i]) toggles++;
                if (!ReferenceEquals(holders[i].CardModel, cards[i])) rebinds++;
                else if (!scrolled && place.DistanceTo(places[i]) > 0.5f) moves++;
                shown[i] = visible;
                cards[i] = holders[i].CardModel;
                places[i] = place;
            }
        }
        _report.Check($"{name}_grid_holders_stable", toggles == 0 && rebinds == 0 && moves == 0 && scrolledFrames == 0,
            $"holders={holders.Count} toggles={toggles} rebinds={rebinds} moves={moves} scrolledFrames={scrolledFrames} offset={offset:F1}");
    }

    // ---- the native Back button's frame -----------------------------------------------------------------------------------

    /// <summary>A Back the game disabled is slid out of the screen: its whole drawn art must be off the canvas.</summary>
    private partial void CheckBackHidden(string name, Control back);

    /// <summary>A Back the game shows must be inside the safe rect and must not be drawn under a visible tip or legend.</summary>
    private partial void CheckBackShown(string name, Control back);

    /// <summary>
    /// The rect a shown Back draws, in canvas units: its own box through the live canvas transform (the portrait scale and
    /// the parent's included). That is the whole button — the launcher scales the control, so the box is the art plane, the tap
    /// target and what the game's own <c>GetGlobalRect</c> reports. A Back's children are decoration (its outline is
    /// transparent unless focused) and the native art plane reaches past the box with transparent margins — measured on the
    /// captures, the visible ribbon is ~214 x 106 inside the 230 x 126 box — so unioning children measured pixels nobody sees.
    /// </summary>
    private static Rect2 DrawnBackRect(Control back) => CanvasRectOf(back);

    /// <summary>
    /// Everything a <em>parked</em> Back could put on screen, in canvas units: its box plus every visible child, whatever its
    /// modulate (the outline is opaque while focused, and the native art plane is larger than the button). The hidden check uses
    /// this conservatively — a parked Back must leave nothing at all on the canvas, not merely its box.
    /// </summary>
    private static Rect2 DrawnBackUnion(Control back)
    {
        Rect2 union = CanvasRectOf(back);
        var pending = new List<Node>(back.GetChildren());
        while (pending.Count > 0)
        {
            Node node = pending[^1];
            pending.RemoveAt(pending.Count - 1);
            if (node is Control child && child.IsVisibleInTree() && child.Size.X > 0.5f && child.Size.Y > 0.5f)
                union = union.Merge(CanvasRectOf(child));
            foreach (Node grandChild in node.GetChildren()) pending.Add(grandChild);
        }
        return union;
    }

    /// <summary>
    /// The rect a tip or legend plate covers: its own box plus its visible children, because a tip's plate is a child and its
    /// set's own box can be empty for a frame (the Back itself is measured by its box, see <see cref="DrawnBackRect"/>).
    /// </summary>
    private static Rect2 DrawnTipRect(Control tip)
    {
        Rect2 union = CanvasRectOf(tip);
        var pending = new List<Node>(tip.GetChildren());
        while (pending.Count > 0)
        {
            Node node = pending[^1];
            pending.RemoveAt(pending.Count - 1);
            if (node is Control child && child.IsVisibleInTree() && child.Modulate.A * child.SelfModulate.A > 0.01f
                && child.Size.X > 0.5f && child.Size.Y > 0.5f)
                union = union.Merge(CanvasRectOf(child));
            foreach (Node grandChild in node.GetChildren()) pending.Add(grandChild);
        }
        return union;
    }

    private static Rect2 CanvasRectOf(Control control)
    {
        Transform2D transform = control.GetGlobalTransformWithCanvas();
        Vector2 corner = transform * Vector2.Zero;
        return new Rect2(corner, (transform * control.Size - corner).Abs());
    }

    /// <summary>
    /// The visible tips a shown Back must not sit under: the game's hover tip sets and the enchant legend, the bottom-left
    /// plate the selection screens pack above the footer.
    /// </summary>
    private static IEnumerable<Rect2> VisibleFooterTips(Control back)
    {
        if (NGame.Instance?.HoverTipsContainer is { } container)
            foreach (NHoverTipSet tip in FindAll<NHoverTipSet>(container))
                if (tip.IsVisibleInTree()) yield return DrawnTipRect(tip);
        for (Node? node = back; node is not null && node is not Window; node = node.GetParent())
        {
            if (node is not Control screen) continue;
            if ((screen.GetNodeOrNull<Control>("%EnchantmentDescriptionContainer")
                    ?? screen.FindChild("EnchantmentDescriptionContainer", true, false) as Control) is not { } legend
                || !legend.IsVisibleInTree()) continue;
            yield return DrawnTipRect(legend);
            break;
        }
    }

    /// <summary>
    /// Checks every native Back the screen currently shows: an enabled one must be wholly inside the safe rect and clear of
    /// the footer tips, a disabled one wholly outside the canvas. Portrait only: landscape is the game's own layout, and the
    /// host partials own the safe rect.
    /// </summary>
    private void CheckScreenBacks(string name, Control screen)
    {
        if (!IsPortraitHost()) return;
        foreach (NBackButton back in FindAll<NBackButton>(screen))
        {
            if (!back.IsVisibleInTree()) continue;
            if (back.IsEnabled) CheckBackShown(name, back);
            else CheckBackHidden(name, back);
        }
    }

    /// <summary>The selection screen a grid belongs to, for the Back check.</summary>
    private static NCardGridSelectionScreen? SelectionScreenOf(Node node)
    {
        for (Node? parent = node; parent is not null; parent = parent.GetParent())
            if (parent is NCardGridSelectionScreen screen) return screen;
        return null;
    }

    private async Task RunDeckViewAsync(RunState state)
    {
        var player = state.Players[0];
        var original = player.Deck.Cards.ToArray();
        for (int i = original.Length; i < 28; i++)
            player.Deck.AddInternal(state.CloneCard(original[i % original.Length]), silent: true);
        var screen = NDeckViewScreen.ShowScreen(player)!;
        if (IsPortraitHost() && FindAll<NBackButton>(screen).FirstOrDefault() is { } deckBack)
            await CheckSlidesAlongItsLineAsync(deckBack, "deck_back_enters_on_its_row", 45);
        var grid = screen.GetNode<NCardGrid>("CardGrid");
        var content = GridScrollContent(grid);
        await WaitUntilAsync(() => grid.CurrentlyDisplayedCardHolders.Any(), "native deck displayed", MediumWait);
        await WaitSecondsAsync(0.7);
        if (!IsPortraitHost())
        {
            _report.Check("deck_landscape_sorter_native", content.HasNode("SortingOptions"), "header stays in native scroll container");
            return;
        }
        CheckScreenBacks("deck", screen);
        // Do not call GetInspectCardScreen just to query visibility: it can
        // instantiate a visible inspector and start a native close animation.
        bool IsInspecting() => NGame.Instance?.InspectCardScreen?.IsVisibleInTree() == true;
        Rect2 view = grid.GetGlobalRect();
        var header = DeckSortRow(screen, grid);
        var sorters = header.GetNode<Control>("HBoxContainer").GetChildren().OfType<NCardViewSortButton>().ToArray();
        bool FullyInView(MegaCrit.Sts2.Core.Models.CardModel card) => grid.GetCardHolder(card) is { } holder
            && view.Grow(2f).Encloses(DrawnCardRect(holder));
        CheckDeckSortersPosition(header);
        Rect2 firstCard = grid.GetCardHolder(grid.SortedCards[0]) is { } firstHolder ? DrawnCardRect(firstHolder) : default;
        _report.Check("deck_start_top_and_sorters_fit", Mathf.Abs(content.Position.Y) < 1f && FullyInView(grid.SortedCards[0])
            && DeckGridStartFits(header, view, firstCard, sorters),
            $"header={header.GetGlobalRect()} view={view} content={content.GetGlobalRect()}");
        CheckDeckGridColumns(grid);
        await CaptureAsync("deck_view_top");
        await CheckGridHoldersStableAsync("deck_top", grid);
        await CheckGridDrawnWhileTouchScrolledAsync("deck", grid);
        await CheckGridCardStillOnNextSwipeAsync("deck", grid);

        Vector2 top = view.Position + view.Size * new Vector2(0.48f, 0.13f);
        Vector2 bottom = view.Position + view.Size * new Vector2(0.48f, 0.87f);
        // A shorter grid shows fewer rows per swipe. Keep real touch input,
        // but stop on the last reachable card rather than after four gestures.
        for (int i = 0; i < 12 && !FullyInView(grid.SortedCards[^1]); i++)
        {
            float before = content.Position.Y;
            await _pointer.DragAsync(bottom, top, steps: 18);
            await WaitSecondsAsync(0.45);
            if (Mathf.Abs(content.Position.Y - before) < 1f) break;
        }
        _report.Check("deck_bottom_stays_and_no_inspect", content.Position.Y < -400f
            && FullyInView(grid.SortedCards[^1]) && !IsInspecting(),
            $"offset={content.Position.Y} last={LastDrawnCardRect(grid)} inspecting={IsInspecting()}");
        await CaptureAsync("deck_view_bottom");
        await CheckGridHoldersStableAsync("deck_bottom", grid);

        // Small real drags near the footer keep the last row in the viewport.
        // A single screenshot can miss the recycler alternating it out/in.
        var last = grid.SortedCards[^1];
        var lastHolder = grid.GetCardHolder(last);
        int unstableFrames = 0;
        float bottomOffset = content.Position.Y;
        Vector2 nearBottom = bottom - new Vector2(0f, 160f);
        Vector2 nudge = nearBottom + new Vector2(0f, 320f);
        await _pointer.PressAsync(nearBottom);
        await _pointer.MoveAsync(nudge, pressed: true);
        for (int i = 0; i < 30; i++)
        {
            await WaitFramesAsync(1);
            if (lastHolder is null || grid.GetCardHolder(last) != lastHolder || !lastHolder.IsVisibleInTree()) unstableFrames++;
        }
        await _pointer.ReleaseAsync(nudge);
        _report.Check("deck_last_row_remains_allocated_while_visible", unstableFrames == 0 && content.Position.Y > bottomOffset + 260f,
            $"unstableFrames={unstableFrames}/30 offset={bottomOffset:F1}->{content.Position.Y:F1}");
        await CheckGridScrollOpensNoTipsAsync(grid, view);

        for (int i = 0; i < 12 && Mathf.Abs(content.Position.Y) >= 1f; i++)
        {
            float before = content.Position.Y;
            await _pointer.DragAsync(top, bottom, steps: 18);
            await WaitSecondsAsync(0.45);
            if (Mathf.Abs(content.Position.Y - before) < 1f) break;
        }
        _report.Check("deck_top_does_not_snap_back", Mathf.Abs(content.Position.Y) < 1f && FullyInView(grid.SortedCards[0]),
            $"settled offset={content.Position.Y}");
        foreach (var sorter in sorters)
        {
            bool before = sorter.IsDescending;
            await _pointer.TapAsync(CenterOf(sorter));
            await WaitSecondsAsync(0.45);
            _report.Check("deck_sort_" + sorter.Name, sorter.IsDescending != before
                && Mathf.Abs(content.Position.Y) < 1f && FullyInView(grid.SortedCards[0]), $"offset={content.Position.Y}");
        }
        await _pointer.TapAsync(CenterOf(grid.GetCardHolder(grid.SortedCards[0])!.Hitbox));
        await WaitUntilAsync(IsInspecting, "deck card inspection after scrolling", ShortWait);
        _report.Pass("deck_tap_still_inspects_card");
    }

    /// <summary>
    /// A scroll is not a hover: a swipe that starts and ends on a card with keyword tips (the content follows the finger, so
    /// the card stays under it) must not open the card's tips, while the finger is down or after it lifts.
    /// </summary>
    private async Task CheckGridScrollOpensNoTipsAsync(NCardGrid grid, Rect2 view)
    {
        if (!IsPortraitHost()) return;
        NCardHolder? card = grid.CurrentlyDisplayedCardHolders.FirstOrDefault(holder => GodotObject.IsInstanceValid(holder)
            && holder.IsVisibleInTree() && holder.CardModel is { } model && model.HoverTips.Any()
            && view.Grow(-60f).HasPoint(CenterOf(holder.Hitbox)));
        if (card is null)
        {
            _report.Fail("deck_scroll_opens_no_card_tips", "no card with keyword tips is fully in view");
            return;
        }
        bool AnyTip() => FindAll<NHoverTipSet>(_tree.Root).Any(tip => GodotObject.IsInstanceValid(tip) && tip.IsVisibleInTree());
        int tipFrames = 0;
        Vector2 start = CenterOf(card.Hitbox);
        // Toward the side with more room, past the scroll's start distance either way.
        float room = view.End.Y - start.Y, roomUp = start.Y - view.Position.Y;
        Vector2 away = start + new Vector2(0f, room >= roomUp ? Math.Min(180f, room - 10f) : -Math.Min(180f, roomUp - 10f));
        await _pointer.PressAsync(start);
        await _pointer.MoveAsync(away, pressed: true);
        for (int i = 0; i < 10; i++)
        {
            await WaitFramesAsync(1);
            if (AnyTip()) tipFrames++;
        }
        await _pointer.MoveAsync(start, pressed: true);
        for (int i = 0; i < 10; i++)
        {
            await WaitFramesAsync(1);
            if (AnyTip()) tipFrames++;
        }
        await _pointer.ReleaseAsync(start);
        for (int i = 0; i < 30; i++)
        {
            await WaitFramesAsync(1);
            if (AnyTip()) tipFrames++;
        }
        _report.Check("deck_scroll_opens_no_card_tips", tipFrames == 0,
            $"card={card.CardModel?.Id} framesWithTips={tipFrames}/50 under={_tree.Root.GuiGetHoveredControl()?.Name}");
        await WaitSecondsAsync(0.45);
    }
}
