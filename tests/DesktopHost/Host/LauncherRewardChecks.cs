using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Nodes.TreasureRooms;
using MegaCrit.Sts2.addons.mega_text;
using STS2Mobile.GameHost;
using STS2Mobile.Platform;

namespace STS2Mobile.DesktopHost.Harness;

/// <summary>
/// The launcher host's answers for the shared rewards, card-screen, treasure and rest scenarios (Batch E): the loot list's
/// rows and its lane, the card reward's and choose-a-card's card/Skip/Peek sizes, the treasure room's chest, banner, relic
/// holders and labels, and the rest site's option row and act cover.
/// <para>
/// Every measurement is in the launcher's canvas units (1080 across; 2.88 units are one phone point, so a touch target is
/// 127). Thresholds from the original 1680-wide reference canvas are converted by 1080/1680 here, and phone-derived ones are
/// stated directly. The layout itself is the launcher's: nodes are found by the game's own names, never by a private method
/// of the portrait pass.
/// </para>
/// </summary>
internal sealed partial class TestDriver
{
    // 1080 / 1680: the reference layout numbers are quoted on the wider canvas; this host's are on the phone's 1080.
    private const float ReferenceToCanvas = 1080f / 1680f;
    // A card reward or choose-a-card card is a 450 unit card in the original 1680-wide reference canvas: 289 units here.
    private const float LauncherCardWidth = 450f * ReferenceToCanvas;
    // The lane's right edge: the original canvas holds it 48 units off the safe edge, so about 31 here.
    private static float LauncherLaneRightEdge(float canvasWidth) => canvasWidth - 48f * ReferenceToCanvas;
    // Skip and Proceed share one vertical centre; the reference lane sits at 5/8 of the height.
    private const float LauncherLaneCentreFactor = 0.625f;

    private (ulong Id, Vector2 Position)[] _launcherRewardHandBefore = Array.Empty<(ulong, Vector2)>();

    private static bool LauncherOnCanvas(Rect2 rect, Vector2 canvas) => new Rect2(Vector2.Zero, canvas).Grow(1f).Encloses(rect);

    /// <summary>
    /// The rectangle the card draws. A grid holder's own control is not that rectangle: focus grows the card to its hover
    /// scale and that growth is part of what the player sees, so the card node's current global rect is the honest measure
    /// for "the controls are below the cards".
    /// </summary>
    private static Rect2 LauncherDrawnCard(NGridCardHolder holder) =>
        holder.CardNode is { } card && GodotObject.IsInstanceValid(card) ? card.GetGlobalRect() : holder.GetGlobalRect();

    /// <summary>The union of the drawn cards the player is being offered, ignoring holders that are gone or not drawn yet.</summary>
    private static Rect2 LauncherDrawnCardBlock(IEnumerable<NGridCardHolder> holders)
    {
        Rect2[] rects = holders.Where(holder => GodotObject.IsInstanceValid(holder) && holder.IsVisibleInTree())
            .Select(LauncherDrawnCard).Where(rect => rect.Size.X > 1f && rect.Size.Y > 1f).ToArray();
        return rects.Length == 0 ? default : LauncherUnion(rects);
    }

    private static Rect2 LauncherUnion(IEnumerable<Rect2> rects)
    {
        Rect2 union = default;
        bool first = true;
        foreach (Rect2 rect in rects)
        {
            union = first ? rect : union.Merge(rect);
            first = false;
        }
        return union;
    }

    /// <summary>
    /// A control's rectangle as drawn: its size under its whole transform, so a scaled holder or pack shows the box the
    /// finger sees rather than the scene's unscaled size (Godot's own global rect keeps the size unscaled).
    /// </summary>
    private static Rect2 LauncherDrawnRect(Control control)
    {
        Transform2D transform = control.GetGlobalTransformWithCanvas();
        Vector2 a = transform * Vector2.Zero;
        Vector2 b = transform * control.Size;
        return new Rect2(new Vector2(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y)), (b - a).Abs());
    }

    /// <summary>The drawn rectangle merged with the launcher's larger touch area when it has one.</summary>
    private static Rect2 LauncherTargetRect(Control control)
    {
        Rect2 drawn = LauncherDrawnRect(control);
        return PhoneHitArea.Of(control) is { } area && area.IsVisibleInTree()
            ? drawn.Merge(LauncherDrawnRect(area))
            : drawn;
    }

    /// <summary>
    /// A card node's drawn rectangle: a card's scene has no size of its own (NCard.defaultSize is 300 x 422) and its picture
    /// is centred on the node, so the size comes from the default and the whole transform, about the node's origin.
    /// </summary>
    private static Rect2 LauncherCardNodeRect(Control card)
    {
        Transform2D transform = card.GetGlobalTransformWithCanvas();
        Vector2 size = new Vector2(300f, 422f) * transform.Scale;
        return new Rect2(transform.Origin - size * 0.5f, size);
    }

    /// <summary>The union of held cards' drawn rectangles (their hitboxes under every scale).</summary>
    private static Rect2 LauncherHeldCardBlock(IEnumerable<NGridCardHolder> holders)
    {
        Rect2[] rects = holders.Where(holder => GodotObject.IsInstanceValid(holder) && holder.IsVisibleInTree())
            .Select(holder => LauncherDrawnRect(holder.Hitbox)).Where(rect => rect.Size.X > 1f).ToArray();
        return rects.Length == 0 ? default : LauncherUnion(rects);
    }

    private static bool LauncherOverlaps(IReadOnlyList<Rect2> rects)
    {
        for (int i = 0; i < rects.Count; i++)
            for (int j = i + 1; j < rects.Count; j++)
                if (rects[i].Grow(-0.5f).Intersects(rects[j].Grow(-0.5f))) return true;
        return false;
    }
    private TextureRect? LauncherCanvasCover(Node room, Rect2 canvas, float? top = null)
    {
        Rect2 wanted = top is { } y ? new Rect2(new Vector2(canvas.Position.X, y), new Vector2(canvas.Size.X, canvas.End.Y - y)) : canvas;
        foreach (TextureRect rect in FindAll<TextureRect>(room))
        {
            if (!rect.IsVisibleInTree() || rect.Texture is null) continue;
            if (rect.GetGlobalRect().Grow(1f).Encloses(wanted)) return rect;
        }
        return null;
    }

    private NHandCardHolder[] LauncherBackgroundHand() =>
        FindAll<NPlayerHand>(_tree.Root).FirstOrDefault()?.ActiveHolders.ToArray() ?? Array.Empty<NHandCardHolder>();

    /// <summary>Whether the offered cards form one row: the launcher's layout for one to three cards.</summary>
    private static bool LauncherCardsInOneRow(IReadOnlyList<Rect2> drawn)
    {
        if (drawn.Count <= 1) return true;
        float top = drawn[0].Position.Y;
        return drawn.All(rect => Math.Abs(rect.Position.Y - top) < 2f);
    }

    /// <summary>
    /// Whether the combat hand still shows below the card block. The launcher's one row under the banner is taller than the
    /// band above a five-card portrait hand at 9:16, so cards and the hand's upper edge overlap there; what must not happen
    /// (the report's bug) is a block that reaches down through the hand and hides it. The card reward over a loot list is
    /// checked against the loot panel instead, which is drawn in front of the hand.
    /// </summary>
    private bool LauncherHandShowsBelowCards(Rect2 cards, out string detail)
    {
        Rect2[] hand = LauncherBackgroundHand()
            .Where(holder => GodotObject.IsInstanceValid(holder) && holder.IsVisibleInTree())
            .Select(holder => holder.CardNode is { } node && GodotObject.IsInstanceValid(node)
                ? LauncherCardNodeRect(node) : holder.GetGlobalRect())
            .Where(rect => rect.Size.X > 1f && rect.Size.Y > 1f)
            .ToArray();
        if (hand.Length == 0)
        {
            detail = "no hand behind the overlay";
            return true;
        }
        Rect2 union = LauncherUnion(hand);
        detail = $"cards={cards} hand={union}";
        return union.End.Y > cards.End.Y + 8f;
    }

    /// <summary>
    /// A loot list behind a card reward is in front of the combat hand. The report's failure was a second card row drawing
    /// below that panel, onto the hand; the cards have to stay over the panel then.
    /// </summary>
    private bool LauncherCardsOverLootPanel(Rect2 cards, out string detail)
    {
        foreach (NRewardsScreen loot in FindAll<NRewardsScreen>(_tree.Root))
        {
            if (!loot.IsVisibleInTree() || loot.GetNodeOrNull<Control>("Rewards") is not { } window) continue;
            if (!window.IsVisibleInTree()) continue;
            Rect2 panel = LauncherDrawnRect(window);
            detail = $"cards={cards} lootPanel={panel}";
            return cards.End.Y <= panel.End.Y + 0.5f;
        }
        detail = "no loot panel behind the card reward";
        return true;
    }

    // ---- Loot list: rows, panel and lane (L12) ----------------------------------------------------------------------------

    partial void CheckRewardsRowsLayout(string name, NRewardsScreen screen, IReadOnlyList<NRewardButton> rows)
    {
        if (!SafeAreaCanvas.IsPortrait)
        {
            // Landscape keeps the game's own window: the panel's portrait pivot and scale must be gone.
            if (screen.GetNodeOrNull<Control>("Rewards") is { } window)
                _report.Check("rewards_landscape_panel_restored",
                    window.Scale.IsEqualApprox(Vector2.One) && window.PivotOffset.IsEqualApprox(Vector2.Zero),
                    $"scale={window.Scale} pivot={window.PivotOffset}");
            return;
        }
        Vector2 canvas = GetViewportSize();
        Rect2 canvasRect = new(Vector2.Zero, canvas);
        Rect2[] boxes = rows.Select(row => row.GetGlobalRect()).ToArray();

        List<MegaRichTextLabel> labels = new();
        foreach (NRewardButton row in rows) labels.AddRange(FindAll<MegaRichTextLabel>(row));
        _report.Check("reward_labels_readable",
            labels.Count > 0 && labels.All(label => label.GetThemeFontSize("normal_font_size")
                * label.GetGlobalTransformWithCanvas().Scale.Y >= 32f - 0.5f),
            $"fonts={string.Join(",", labels.Select(label => label.GetThemeFontSize("normal_font_size")
                * label.GetGlobalTransformWithCanvas().Scale.Y))} floor=32");
        _report.Check("reward_rows_resized", rows.Count > 0 && boxes.All(box => box.Size.Y >= PortraitBands.MinTarget - 0.5f),
            $"heights={string.Join(",", boxes.Select(box => box.Size.Y.ToString("F0")))} floor={PortraitBands.MinTarget}");
        // The loot window keeps the game's own constant size (its 526 x 640 panel at the portrait scale), whatever the rows,
        // and the panel is centred on the screen's middle: the rows start at its top as in the game.
        if (screen.GetNodeOrNull<Control>("Rewards") is { } lootWindow)
        {
            Rect2 windowRect = lootWindow.GetGlobalRect();
            float drawnScale = windowRect.Size.X / 526f;
            _report.Check("rewards_loot_panel_native_size", Math.Abs(windowRect.Size.Y - 640f * drawnScale) <= 1f,
                $"panel={windowRect} expectedHeight={640f * drawnScale:F1}");
            _report.Check("rewards_loot_panel_centred", Math.Abs(windowRect.GetCenter().Y - canvas.Y * 0.5f) <= 2f,
                $"panel={windowRect} centre={windowRect.GetCenter().Y:F1} middle={canvas.Y * 0.5f:F1}");
        }

        if (screen.GetNodeOrNull<Control>("Rewards") is not { } panel) return;
        Rect2 panelRect = panel.GetGlobalRect();
        Rect2 mask = panel.GetNodeOrNull<Control>("%RewardContainerMask")?.GetGlobalRect() ?? panelRect;
        _report.Check("reward_rows_phone_touch_size",
            rows.Count > 0 && boxes.All(box => LauncherOnCanvas(box, canvas) && mask.Intersects(box)),
            $"rows={string.Join("; ", boxes)} mask={mask}");
        _report.Check("rewards_loot_panel_phone_width", panelRect.Size.X >= canvas.X * 0.74f - 0.5f,
            $"panel={panelRect} width={panelRect.Size.X:F0} floor={canvas.X * 0.74f:F0} canvas={canvasRect}");
        if (screen.GetNodeOrNull<NProceedButton>("ProceedButton") is { } proceed)
        {
            float laneTop = proceed.GetGlobalRect().Position.Y;
            _report.Check("rewards_loot_panel_above_lane", panelRect.End.Y <= laneTop - 8f,
                $"panelEnd={panelRect.End.Y:F1} laneTop={laneTop:F1} action={proceed.GetGlobalRect()}");
            // The game's panel reaches down to its Skip/Proceed: the lane hangs 24 units under the panel's bottom.
            _report.Check("rewards_loot_panel_reaches_lane", Math.Abs(laneTop - panelRect.End.Y - 24f) <= 1f,
                $"panelEnd={panelRect.End.Y:F1} laneTop={laneTop:F1} gap={laneTop - panelRect.End.Y:F1} expected=24");
        }
    }

    // ---- Card reward: alternatives and Skip (L13) -------------------------------------------------------------------------

    partial void CheckRewardAlternativesLayout(string name, NCardRewardSelectionScreen screen,
        IReadOnlyList<NCardRewardAlternativeButton> alternatives)
    {
        if (!SafeAreaCanvas.IsPortrait || alternatives.Count == 0) return;
        Vector2 canvas = GetViewportSize();
        List<NGridCardHolder> cards = FindAll<NGridCardHolder>(screen);
        float cardBottom = LauncherDrawnCardBlock(cards).End.Y;
        Rect2[] boxes = alternatives.Select(alternative => alternative.GetGlobalRect()).ToArray();
        bool rightAligned = Mathf.Abs(boxes.Max(box => box.End.X) - LauncherLaneRightEdge(canvas.X)) < 25f;
        bool clear = !boxes.SelectMany((box, i) => boxes.Skip(i + 1).Select(other => box.Intersects(other))).Any(overlap => overlap);
        _report.Check("reward_two_alternatives_below_cards",
            boxes.All(box => LauncherOnCanvas(box, canvas) && box.Position.Y > cardBottom
                && box.Size.Y >= PortraitBands.MinTarget - 0.5f)
            && rightAligned && clear,
            $"cardsBottom={cardBottom:F0} right={boxes.Max(box => box.End.X):F0} expected={LauncherLaneRightEdge(canvas.X):F0} " +
            $"alternatives={string.Join("; ", boxes)}");
    }

    partial void CheckRewardSkipLayout(string name, NCardRewardSelectionScreen screen, NCardRewardAlternativeButton skip,
        Control lootProceed)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        Vector2 canvas = GetViewportSize();
        Rect2 slot = skip.GetGlobalRect();
        List<NGridCardHolder> cards = FindAll<NGridCardHolder>(screen);
        float cardBottom = LauncherDrawnCardBlock(cards).End.Y;
        // The loot list sits behind the card reward: its own action must be out of play (hidden, disabled, or slid off the
        // canvas), never left reachable under the Skip.
        bool lootOutOfPlay = !lootProceed.Visible
            || lootProceed is not NClickableControl action || !action.IsEnabled
            || !LauncherOnCanvas(lootProceed.GetGlobalRect(), canvas);
        _report.Check("reward_skip_below_cards",
            lootOutOfPlay && LauncherOnCanvas(slot, canvas) && slot.Position.Y > cardBottom
            && slot.Size.Y >= PortraitBands.MinTarget - 0.5f
            && Mathf.Abs(slot.End.X - LauncherLaneRightEdge(canvas.X)) < 25f,
            $"cardsBottom={cardBottom:F0} skip={slot} expectedRight={LauncherLaneRightEdge(canvas.X):F0} lootProceed={lootProceed.GetGlobalRect()}");
    }

    partial void BeforeRewardSkipTap() =>
        _launcherRewardHandBefore = LauncherBackgroundHand().Select(holder => (holder.GetInstanceId(), holder.Position)).ToArray();

    partial void CheckRewardSkipHandUntouched(string name)
    {
        (ulong Id, Vector2 Position)[] now = LauncherBackgroundHand()
            .Select(holder => (holder.GetInstanceId(), holder.Position)).ToArray();
        bool same = now.Length == _launcherRewardHandBefore.Length
            && now.Zip(_launcherRewardHandBefore).All(pair =>
                pair.First.Id == pair.Second.Id && pair.First.Position.IsEqualApprox(pair.Second.Position));
        _report.Check("reward_skip_does_not_expand_background_hand", same,
            $"before={string.Join(",", _launcherRewardHandBefore.Select(entry => entry.Position))} " +
            $"after={string.Join(",", now.Select(entry => entry.Position))}");
    }

    partial void CheckRewardCardsLayout(string name, NCardRewardSelectionScreen screen,
        IReadOnlyList<NGridCardHolder> holders)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        Vector2 canvas = GetViewportSize();
        Rect2[] drawn = holders.Select(LauncherDrawnCard).ToArray();
        _report.Check("reward_cards_phone_width", holders.Count > 0 && holders.All(holder =>
                holder.Hitbox.GetGlobalRect().Size.X >= LauncherCardWidth - 0.5f
                && LauncherOnCanvas(LauncherDrawnCard(holder), canvas)),
            $"widths={string.Join(",", holders.Select(holder => holder.Hitbox.GetGlobalRect().Size.X.ToString("F0")))} " +
            $"floor={LauncherCardWidth:F0}");
        if (holders.Count is > 0 and <= 3)
            _report.Check("reward_cards_one_row", LauncherCardsInOneRow(drawn),
                $"rects={string.Join("; ", drawn)}");
        // The panel bound uses the laid-out cards (no focus growth): the focused card may grow a few units past the panel's
        // bottom into the dimmed hand, but the persistent rows must not leave the panel (the reported second-row bug).
        Rect2 placed = LauncherUnion(holders.Select(holder => holder.Hitbox.GetGlobalRect()));
        _report.Check("reward_cards_clear_of_hand",
            LauncherCardsOverLootPanel(placed, out string panelDetail), panelDetail);
    }

    // ---- Choose-a-card and the card reward fixture (L13) -------------------------------------------------------------------

    partial void CheckCardScreensLayout(string name, NChooseACardSelectionScreen screen,
        IReadOnlyList<NGridCardHolder> holders, NChoiceSelectionSkipButton? skip)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        Vector2 canvas = GetViewportSize();
        // The launcher scales the grid's row, not each holder, so holder Scale/SmallScale says nothing here: the drawn card
        // (its hitbox) is what the finger gets, and that is the phone width being asserted.
        Rect2[] drawn = holders.Select(LauncherDrawnCard).ToArray();
        _report.Check("choose_a_card_cards_scaled", holders.Count > 0 && holders.All(holder =>
                holder.Hitbox.GetGlobalRect().Size.X >= LauncherCardWidth - 0.5f),
            $"widths={string.Join(",", holders.Select(holder => holder.Hitbox.GetGlobalRect().Size.X.ToString("F0")))} " +
            $"heights={string.Join(",", holders.Select(holder => holder.Hitbox.GetGlobalRect().Size.Y.ToString("F0")))} " +
            $"floor={LauncherCardWidth:F0} holderScale={string.Join(",", holders.Select(holder => holder.Scale.X.ToString("F2")))}");
        if (holders.Count is > 0 and <= 3)
            _report.Check("choose_a_card_cards_one_row", LauncherCardsInOneRow(drawn),
                $"rects={string.Join("; ", drawn)}");
        Rect2 chooseBlock = holders.Count > 0 ? LauncherUnion(drawn) : default;
        string chooseHandDetail = "no cards";
        _report.Check("choose_a_card_hand_below_cards",
            holders.Count == 0 || LauncherHandShowsBelowCards(chooseBlock, out chooseHandDetail),
            chooseHandDetail);
        if (skip is not null)
            _report.Check("choose_a_card_skip_scaled",
                LauncherOnCanvas(skip.GetGlobalRect(), canvas) && skip.GetGlobalRect().Size.Y >= PortraitBands.MinTarget - 0.5f,
                $"size={skip.GetGlobalRect().Size} rect={skip.GetGlobalRect()}");
    }

    partial void CheckCardRewardScreenLayout(string name, NCardRewardSelectionScreen screen,
        IReadOnlyList<NGridCardHolder> holders)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        Vector2 canvas = GetViewportSize();
        Rect2[] drawn = holders.Select(LauncherDrawnCard).ToArray();
        _report.Check("card_reward_cards_scaled", holders.Count > 0 && holders.All(holder =>
                holder.Hitbox.GetGlobalRect().Size.X >= LauncherCardWidth - 0.5f
                && LauncherOnCanvas(LauncherDrawnCard(holder), canvas)),
            $"widths={string.Join(",", holders.Select(holder => holder.Hitbox.GetGlobalRect().Size.X.ToString("F0")))} " +
            $"heights={string.Join(",", holders.Select(holder => holder.Hitbox.GetGlobalRect().Size.Y.ToString("F0")))} " +
            $"floor={LauncherCardWidth:F0} holderScale={string.Join(",", holders.Select(holder => holder.Scale.X.ToString("F2")))}");
        if (holders.Count is > 0 and <= 3)
            _report.Check("card_reward_cards_one_row", LauncherCardsInOneRow(drawn),
                $"rects={string.Join("; ", drawn)}");
        Rect2 placed = holders.Count > 0
            ? LauncherUnion(holders.Select(holder => holder.Hitbox.GetGlobalRect())) : default;
        string panelDetail = "no cards";
        _report.Check("card_reward_cards_clear_of_hand",
            holders.Count == 0 || LauncherCardsOverLootPanel(placed, out panelDetail), panelDetail);
    }

    /// <summary>
    /// The combat picker (Attack Potion and a rest site's choose-a-card): Skip under the cards, the banner above them, and
    /// Peek, when it is enabled, below the cards and clear of Skip. Cards are measured by the rectangle they draw, not by the
    /// holder's hitbox (which carries padding); the footer's own right alignment is checked by the choose-a-card check.
    /// </summary>
    partial void CheckCombatChoiceControls(string name, NChooseACardSelectionScreen screen, IReadOnlyList<NGridCardHolder> holders)
    {
        if (!SafeAreaCanvas.IsPortrait || holders.Count == 0) return;
        Vector2 canvas = GetViewportSize();
        NPeekButton peek = screen.GetNode<NPeekButton>("%PeekButton");
        NChoiceSelectionSkipButton skip = screen.GetNode<NChoiceSelectionSkipButton>("SkipButton");
        Rect2 row = LauncherDrawnCardBlock(holders);
        if (row.Size.X <= 1f) return;
        Rect2 skipRect = skip.GetGlobalRect();
        Rect2 peekRect = peek.GetGlobalRect();
        Rect2 banner = screen.GetNode<Control>("Banner").GetGlobalRect();
        bool peekClear = !peek.IsEnabled
            || (LauncherOnCanvas(peekRect, canvas) && peekRect.Position.Y > row.End.Y && !skipRect.Intersects(peekRect));
        _report.Check(name + "_controls_clear_of_cards",
            LauncherOnCanvas(skipRect, canvas) && skipRect.Size.Y >= PortraitBands.MinTarget - 0.5f
            && skipRect.Position.Y > row.End.Y && banner.End.Y < row.Position.Y && peekClear,
            $"cards={row} banner={banner} skip={skipRect} peek={peekRect} enabled={peek.IsEnabled}");
    }

    // ---- Treasure room (L11) ---------------------------------------------------------------------------------------------

    partial void CheckTreasureLayout(string name, string phase, NTreasureRoom room,
        NTreasureRoomRelicCollection collection)
    {
        NTreasureButton chest = room.GetNode<NTreasureButton>("%Chest");
        GpuParticles2D coins = room.GetNode<GpuParticles2D>("%GoldExplosion");
        Vector2 canvas = GetViewportSize();
        Rect2 canvasRect = new(Vector2.Zero, canvas);
        NProceedButton proceed = room.ProceedButton;
        switch (phase)
        {
            case "closed":
            case "closed_skip":
            {
                Rect2 button = proceed.GetGlobalRect();
                bool outOfSight = !proceed.IsVisibleInTree() || !canvasRect.Grow(1f).Encloses(button);
                _report.Check(name + "_proceed_hidden_when_closed", !proceed.IsEnabled && (!SafeAreaCanvas.IsPortrait || outOfSight),
                    $"enabled={proceed.IsEnabled} button={button} canvas={canvas}");
                if (phase == "closed_skip" || !SafeAreaCanvas.IsPortrait) return;
                Rect2 box = chest.GetGlobalRect();
                // v0.113 collides through an occluder node; later builds hold the same floor as the burst shader's
                // floor_rect (left, top, right, bottom) in the emitter's own coordinates. Either way the floor lands inside the chest.
                Vector2? floor = room.GetNodeOrNull<Node2D>("GoldExplosionCollider")?.GlobalPosition;
                if (floor == null && coins.ProcessMaterial is ShaderMaterial shader
                    && shader.GetShaderParameter("floor_rect").VariantType == Variant.Type.Vector4)
                {
                    Vector4 rect = shader.GetShaderParameter("floor_rect").AsVector4();
                    floor = coins.GetGlobalTransform() * new Vector2((rect.X + rect.Z) * 0.5f, rect.Y);
                }
                _report.Check("treasure_chest_and_gold_origin",
                    floor is { } landing && box.Size.X >= canvas.X * 0.70f && LauncherOnCanvas(box, canvas)
                    && box.HasPoint(coins.GlobalPosition) && box.HasPoint(landing),
                    $"chest={box} width={box.Size.X:F0} floor={canvas.X * 0.70f:F0} gold={coins.GlobalPosition} " +
                    $"landing={floor?.ToString() ?? "missing"}");
                _report.Check("treasure_art",
                    LauncherCanvasCover(room, canvasRect, PortraitLayout.ComputeCapHeight(_tree.Root) + PortraitBands.TopBarHeight) is { } cover,
                    $"cover={(LauncherCanvasCover(room, canvasRect, PortraitLayout.ComputeCapHeight(_tree.Root) + PortraitBands.TopBarHeight) is { } found ? found.GetPath() : "none")}");
                return;
            }
            case "landscape":
                _report.Check("treasure_landscape_unchanged",
                    chest.Scale.IsEqualApprox(Vector2.One) && coins.Position.IsEqualApprox(new Vector2(937f, 512f)),
                    $"chest={chest.Scale} gold={coins.Position}");
                return;
            case "skip":
                if (!SafeAreaCanvas.IsPortrait) return;
                _report.Check("treasure_skip_safe_touch_target",
                    proceed.GetGlobalRect().Size.Y >= PortraitBands.MinTarget - 0.5f
                    && proceed.GetGlobalRect().Size.X >= PortraitBands.MinTarget - 0.5f
                    && LauncherOnCanvas(proceed.GetGlobalRect(), canvas), $"button={proceed.GetGlobalRect()}");
                return;
            case "open":
            {
                if (!SafeAreaCanvas.IsPortrait) return;
                Control banner = room.GetNode<Control>("%Banner");
                NTreasureRoomRelicHolder[] holders = collection.HoldersInUse.ToArray();
                Rect2[] buttons = holders.Select(holder => holder.GetGlobalRect()).ToArray();
                Rect2 bannerRect = banner.GetGlobalRect();
                bool overlap = buttons.SelectMany((box, i) => buttons.Skip(i + 1).Select(other => box.Intersects(other))).Any(hit => hit);
                _report.Check(name + "_relics_and_banner_fit",
                    LauncherOnCanvas(bannerRect, canvas)
                    && buttons.All(box => LauncherOnCanvas(box, canvas)
                        && box.Size.X >= PortraitBands.MinTarget - 0.5f && box.Size.Y >= PortraitBands.MinTarget - 0.5f
                        && box.Position.Y >= bannerRect.End.Y)
                    && !overlap,
                    $"banner={bannerRect} holders={string.Join("; ", buttons)}");
                bool labelsOnCanvas = true;
                foreach (string labelName in new[] { "%EmptyLabel", "%FightLabel" })
                    if (room.GetNodeOrNull<Control>(labelName) is { } label && label.IsVisibleInTree())
                        labelsOnCanvas &= LauncherOnCanvas(label.GetGlobalRect(), canvas);
                _report.Check(name + "_labels_on_canvas", labelsOnCanvas,
                    $"empty={room.GetNodeOrNull<Control>("%EmptyLabel")?.GetGlobalRect()} " +
                    $"fight={room.GetNodeOrNull<Control>("%FightLabel")?.GetGlobalRect()}");
                return;
            }
            case "proceed":
                if (!SafeAreaCanvas.IsPortrait) return;
                _report.Check(name + "_proceed_safe_touch_target",
                    proceed.GetGlobalRect().Size.Y >= PortraitBands.MinTarget - 0.5f
                    && proceed.GetGlobalRect().Size.X >= PortraitBands.MinTarget - 0.5f
                    && LauncherOnCanvas(proceed.GetGlobalRect(), canvas), $"button={proceed.GetGlobalRect()}");
                // Within the thumb's reach: the same lane as the loot screen's Skip/Proceed, 24 units under the loot panel,
                // which is the game's 640-tall panel at the width's scale, centred on the canvas.
                float lootScale = (canvas.X - 72f) / 654f;
                float lootLaneTop = canvas.Y * 0.5f + 640f * lootScale * 0.5f + 24f;
                _report.Check(name + "_proceed_on_loot_lane", Math.Abs(proceed.GetGlobalRect().Position.Y - lootLaneTop) <= 1.5f,
                    $"button={proceed.GetGlobalRect()} lootLaneTop={lootLaneTop:F1}");
                return;
        }
    }

    // ---- Count variants (rewards-variants) -------------------------------------------------------------------------------

    partial void CheckLootLongListLayout(string name, NRewardsScreen screen, IReadOnlyList<NRewardButton> rows, bool scrolled)
    {
        if (!SafeAreaCanvas.IsPortrait || rows.Count == 0) return;
        Vector2 canvas = GetViewportSize();
        Rect2 safe = PortraitSafeRect();
        Rect2[] boxes = rows.Select(row => LauncherDrawnRect(row)).ToArray();
        List<MegaRichTextLabel> labels = new();
        foreach (NRewardButton row in rows) labels.AddRange(FindAll<MegaRichTextLabel>(row));
        _report.Check(name + "_row_labels_readable",
            labels.Count > 0 && labels.All(label => label.GetThemeFontSize("normal_font_size")
                * label.GetGlobalTransformWithCanvas().Scale.Y >= 32f - 0.5f),
            $"fonts={string.Join(",", labels.Select(label => label.GetThemeFontSize("normal_font_size")
                * label.GetGlobalTransformWithCanvas().Scale.Y))} floor=32");
        _report.Check(name + "_rows_phone_size",
            boxes.All(box => box.Size.Y >= PortraitBands.MinTarget - 0.5f && box.Size.X >= canvas.X * 0.30f),
            $"heights={string.Join(",", boxes.Select(box => box.Size.Y.ToString("F0")))} floor={PortraitBands.MinTarget}");
        if (screen.GetNodeOrNull<Control>("Rewards") is not { } panel) return;
        Rect2 panelRect = LauncherDrawnRect(panel);
        if (screen.GetNodeOrNull<Control>("%RewardContainerMask") is not { } mask) return;
        Rect2 maskRect = LauncherDrawnRect(mask);
        _report.Check(name + "_window_phone_width", panelRect.Size.X >= canvas.X * 0.74f - 0.5f,
            $"panel={panelRect} width={panelRect.Size.X:F0} floor={canvas.X * 0.74f:F0}");
        _report.Check(name + "_mask_clips_rows", mask.ClipContents && maskRect.Size.Y > 1f,
            $"mask={maskRect} clip={mask.ClipContents}");
        if (screen.GetNodeOrNull<Control>("ProceedButton") is { } action)
        {
            Rect2 actionRect = LauncherTargetRect(action);
            _report.Check(name + "_action_reachable",
                action.IsVisibleInTree() && actionRect.Size.Y >= PortraitBands.MinTarget - 0.5f
                && actionRect.Size.X >= PortraitBands.MinTarget - 0.5f && safe.Grow(1f).Encloses(actionRect),
                $"action={actionRect} safe={safe}");
            _report.Check(name + "_panel_above_action", panelRect.End.Y <= actionRect.Position.Y - 8f,
                $"panelEnd={panelRect.End.Y:F1} actionTop={actionRect.Position.Y:F1}");
        }
        if (scrolled)
        {
            _report.Check(name + "_last_row_in_mask",
                boxes[^1].Position.Y < maskRect.End.Y && maskRect.Grow(2f).Encloses(boxes[^1])
                && LauncherOnCanvas(boxes[^1], canvas),
                $"last={boxes[^1]} mask={maskRect}");
            if (screen.GetNodeOrNull<Control>("%RewardsContainer") is { } list)
                _report.Check(name + "_list_scrolled", list.Position.Y < -50f, $"listTop={list.Position.Y:F0}");
        }
    }

    partial void CheckRewardAlternativeRow(string name, NCardRewardSelectionScreen screen,
        IReadOnlyList<NCardRewardAlternativeButton> alternatives)
    {
        if (!SafeAreaCanvas.IsPortrait || alternatives.Count == 0) return;
        Vector2 canvas = GetViewportSize();
        List<NGridCardHolder> cards = FindAll<NGridCardHolder>(screen);
        // The card node's own rect is empty in this scene (NCard has no size); the holder's hitbox is the 300 x 422 card
        // under the holder's and the row's scales, so that is the drawn block.
        float cardBottom = LauncherHeldCardBlock(cards).End.Y;
        Rect2[] boxes = alternatives.Select(LauncherTargetRect).ToArray();
        bool clear = !LauncherOverlaps(boxes);
        bool below = boxes.All(box => box.Position.Y > cardBottom);
        bool rightAligned = Mathf.Abs(boxes.Max(box => box.End.X) - LauncherLaneRightEdge(canvas.X)) < 25f;
        _report.Check(name + "_alternatives_phone_row",
            boxes.All(box => LauncherOnCanvas(box, canvas) && box.Size.Y >= PortraitBands.MinTarget - 0.5f)
            && below && clear && rightAligned,
            $"cardsBottom={cardBottom:F0} right={boxes.Max(box => box.End.X):F0} " +
            $"expected={LauncherLaneRightEdge(canvas.X):F0} alternatives={string.Join("; ", boxes)}");
    }

    partial void CheckChoiceCountLayout(string name, Control screen, IReadOnlyList<NGridCardHolder> holders, Control? skip)
    {
        if (!SafeAreaCanvas.IsPortrait || holders.Count == 0) return;
        Vector2 canvas = GetViewportSize();
        Rect2 safe = PortraitSafeRect();
        Rect2[] drawn = holders.Select(holder => LauncherDrawnRect(holder.Hitbox)).ToArray();
        Rect2[] drawnCards = holders.Select(LauncherDrawnCard).ToArray();
        // One to three cards form a single row; four or more use the launcher's widening grid with the peek footer dropped to
        // its native 128 units when needed, so every count keeps the phone width floor.
        _report.Check(name + "_cards_phone_width",
            drawn.All(rect => LauncherOnCanvas(rect, canvas) && rect.Size.X >= LauncherCardWidth - 0.5f),
            $"widths={string.Join(",", drawn.Select(rect => rect.Size.X.ToString("F0")))} floor={LauncherCardWidth:F0} " +
            $"count={holders.Count}");
        if (holders.Count <= 3)
            _report.Check(name + "_cards_one_row", LauncherCardsInOneRow(drawn),
                $"rects={string.Join("; ", drawn)}");
        _report.Check(name + "_hand_below_cards",
            LauncherHandShowsBelowCards(LauncherUnion(drawn), out string handDetail), handDetail);
        _report.Check(name + "_cards_inside_safe_frame", drawn.All(rect => safe.Grow(1f).Encloses(rect)),
            $"safe={safe} cards={string.Join("; ", drawn)}");
        _report.Check(name + "_cards_do_not_overlap", !LauncherOverlaps(drawn),
            $"cards={string.Join("; ", drawn)}");
        if (screen.GetNodeOrNull<Control>("Banner") is { } banner && banner.IsVisibleInTree())
            _report.Check(name + "_banner_above_cards",
                LauncherDrawnRect(banner).End.Y <= drawn.Min(rect => rect.Position.Y) + 1f,
                $"banner={LauncherDrawnRect(banner)} cardsTop={drawn.Min(rect => rect.Position.Y):F0}");
        if (skip is null) return;
        Rect2 skipRect = LauncherTargetRect(skip);
        _report.Check(name + "_skip_phone_target",
            LauncherOnCanvas(skipRect, canvas) && skipRect.Size.Y >= PortraitBands.MinTarget - 0.5f
            && skipRect.Size.X >= PortraitBands.MinTarget - 0.5f
            && skipRect.Position.Y >= drawn.Max(rect => rect.End.Y) - 1f,
            $"skip={skipRect} cardsBottom={drawn.Max(rect => rect.End.Y):F0}");
    }

    partial void CheckRelicCountLayout(string name, Control screen, IReadOnlyList<Control> holders, Control? skip)
    {
        if (!SafeAreaCanvas.IsPortrait || holders.Count == 0) return;
        Vector2 canvas = GetViewportSize();
        Rect2 safe = PortraitSafeRect();
        Rect2[] rects = holders.Select(LauncherTargetRect).ToArray();
        _report.Check(name + "_relic_targets_phone_size",
            rects.All(rect => rect.Size.X >= PortraitBands.MinTarget - 0.5f
                && rect.Size.Y >= PortraitBands.MinTarget - 0.5f && safe.Grow(1f).Encloses(rect)),
            $"relics={string.Join("; ", rects)} floor={PortraitBands.MinTarget}");
        _report.Check(name + "_relics_do_not_overlap", !LauncherOverlaps(rects),
            $"relics={string.Join("; ", rects)}");
        if (screen.GetNodeOrNull<Control>("Banner") is { } banner && banner.IsVisibleInTree())
            _report.Check(name + "_banner_above_relics",
                LauncherDrawnRect(banner).End.Y <= rects.Min(rect => rect.Position.Y) + 1f,
                $"banner={LauncherDrawnRect(banner)} relicsTop={rects.Min(rect => rect.Position.Y):F0}");
        if (skip is null) return;
        Rect2 skipRect = LauncherTargetRect(skip);
        _report.Check(name + "_skip_phone_target",
            LauncherOnCanvas(skipRect, canvas) && skipRect.Size.Y >= PortraitBands.MinTarget - 0.5f
            && skipRect.Size.X >= PortraitBands.MinTarget - 0.5f
            && skipRect.Position.Y >= rects.Max(rect => rect.End.Y) - 1f,
            $"skip={skipRect} relicsBottom={rects.Max(rect => rect.End.Y):F0}");
    }

    partial void CheckBundleLayout(string name, Control screen, IReadOnlyList<NCardBundle> packs, bool preview)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        Vector2 canvas = GetViewportSize();
        Rect2 safe = PortraitSafeRect();
        bool IsDrawn(Control control) => GodotObject.IsInstanceValid(control) && control.IsVisibleInTree();
        List<Rect2> cards = new();
        if (!preview)
        {
            foreach (NCardBundle pack in packs)
                cards.AddRange(pack.CardNodes.Where(IsDrawn).Select(LauncherCardNodeRect));
            _report.Check(name + "_pack_cards_phone_size",
                cards.Count == packs.Count * 3 && cards.All(rect => rect.Size.X >= LauncherCardWidth - 0.5f),
                $"cards={cards.Count} widths={string.Join(",", cards.Select(rect => rect.Size.X.ToString("F0")))} floor={LauncherCardWidth:F0}");
            _report.Check(name + "_pack_cards_on_canvas", cards.All(rect => LauncherOnCanvas(rect, canvas)),
                $"cards={string.Join("; ", cards)} canvas={canvas}");
            Rect2[] targets = packs.Select(pack => LauncherTargetRect(pack.Hitbox)).ToArray();
            _report.Check(name + "_pack_targets_phone_size",
                targets.All(rect => rect.Size.X >= PortraitBands.MinTarget - 0.5f
                    && rect.Size.Y >= PortraitBands.MinTarget - 0.5f && safe.Grow(1f).Encloses(rect))
                && !LauncherOverlaps(targets),
                $"packs={string.Join("; ", targets)} floor={PortraitBands.MinTarget}");
            if (screen.GetNodeOrNull<Control>("Banner") is { } banner && banner.IsVisibleInTree())
                _report.Check(name + "_banner_above_packs",
                    LauncherDrawnRect(banner).End.Y <= targets.Min(rect => rect.Position.Y) + 1f,
                    $"banner={LauncherDrawnRect(banner)} packsTop={targets.Min(rect => rect.Position.Y):F0}");
            if (screen.GetNodeOrNull<Control>("%SkipButton") is { } skip)
            {
                Rect2 skipRect = LauncherTargetRect(skip);
                Rect2 packBlock = LauncherUnion(targets);
                _report.Check(name + "_skip_phone_target",
                    skipRect.Size.Y >= PortraitBands.MinTarget - 0.5f
                    && skipRect.Size.X >= PortraitBands.MinTarget - 0.5f && LauncherOnCanvas(skipRect, canvas)
                    && skipRect.Position.Y >= packBlock.End.Y - 1f && !skipRect.Intersects(packBlock),
                    $"skip={skipRect} packs={packBlock}");
            }
            if (screen.GetNodeOrNull<Control>("%PeekButton") is { } peek && peek.IsVisibleInTree())
                _report.Check(name + "_peek_phone_target",
                    LauncherTargetRect(peek).Size.Y >= PortraitBands.MinTarget - 0.5f
                    && LauncherOnCanvas(LauncherTargetRect(peek), canvas),
                    $"peek={LauncherTargetRect(peek)}");
            return;
        }

        Control? previewCards = screen.GetNodeOrNull<Control>("%Cards");
        List<Control> holders = previewCards?.GetChildren().OfType<Control>()
            .Where(holder => !holder.IsQueuedForDeletion() && holder.IsVisibleInTree()).ToList() ?? new List<Control>();
        Rect2[] drawn = holders.Select(holder =>
            holder.GetType().GetProperty("Hitbox")?.GetValue(holder) is Control hitbox ? LauncherDrawnRect(hitbox)
                : LauncherDrawnRect(holder)).ToArray();
        _report.Check(name + "_preview_cards_phone_size",
            drawn.Length >= 3 && drawn.All(rect => rect.Size.X >= LauncherCardWidth - 0.5f && LauncherOnCanvas(rect, canvas)),
            $"cards={drawn.Length} widths={string.Join(",", drawn.Select(rect => rect.Size.X.ToString("F0")))} floor={LauncherCardWidth:F0}");
        _report.Check(name + "_preview_cards_inside_safe_frame",
            drawn.All(rect => safe.Grow(1f).Encloses(rect)) && !LauncherOverlaps(drawn),
            $"safe={safe} cards={string.Join("; ", drawn)}");
        foreach (string buttonName in new[] { "%Confirm", "%Cancel" })
        {
            if (screen.GetNodeOrNull<Control>(buttonName) is not { } button) continue;
            Rect2 rect = LauncherTargetRect(button);
            _report.Check(name + "_" + buttonName.TrimStart('%').ToLowerInvariant() + "_phone_target",
                rect.Size.Y >= PortraitBands.MinTarget - 0.5f && rect.Size.X >= PortraitBands.MinTarget - 0.5f
                && LauncherOnCanvas(rect, canvas),
                $"button={rect}");
        }
        // The preview's Cancel is the game's Back button: it stands where every other portrait Back does, at the footer's
        // left, with Confirm at the right on the same line.
        if (screen.GetNodeOrNull<Control>("%Cancel") is { } back && screen.GetNodeOrNull<Control>("%Confirm") is { } accept)
        {
            Rect2 backRect = back.GetGlobalRect();
            Rect2 acceptRect = accept.GetGlobalRect();
            bool sameLine = backRect.Position.Y < acceptRect.End.Y && acceptRect.Position.Y < backRect.End.Y;
            _report.Check(name + "_cancel_on_back_side",
                backRect.GetCenter().X < canvas.X * 0.5f && acceptRect.GetCenter().X > canvas.X * 0.5f && sameLine,
                $"cancel={backRect} confirm={acceptRect} sameLine={sameLine}");
        }
        if (screen.GetNodeOrNull<Control>("%SkipButton") is { } hiddenSkip)
            _report.Check(name + "_skip_hidden_in_preview", !hiddenSkip.IsVisibleInTree(),
                $"skipVisible={hiddenSkip.IsVisibleInTree()}");
    }

    // ---- Rest site (L9) --------------------------------------------------------------------------------------------------

    private partial void CheckRestSiteButtons(IReadOnlyList<NRestSiteButton> buttons)
    {
        if (buttons.Count == 0) return;
        Vector2 canvas = GetViewportSize();
        Rect2[] rects = buttons.Select(button => button.GetGlobalRect()).ToArray();
        Rect2 row = LauncherUnion(rects);
        if (!SafeAreaCanvas.IsPortrait)
        {
            // Landscape puts the game's own row back; the options stay reachable.
            _report.Check("rest_site_buttons_scaled", LauncherOnCanvas(row, canvas), $"row={row} canvas={canvas}");
            return;
        }
        // The row's scale is the documented one: 44 pt buttons up to eight options; from nine the grid shrinks so the widest
        // row keeps its margins (decision 62), so the floor is what that layout draws, never more.
        float scale = buttons.Count <= PortraitRestMath.SingleRowMax
            ? PortraitRestMath.OptionsScale(PortraitRestMath.OptionsWidth(buttons.Count, 0f), canvas.X)
            : PortraitRestMath.OptionsGridScale(buttons.Count, canvas.X);
        float floor = Math.Min(PortraitBands.MinTarget, PortraitRestMath.OptionsHeight * scale);
        _report.Check("rest_site_buttons_scaled", rects.All(box =>
            {
                return box.Size.X >= floor - 0.5f && box.Size.Y >= floor - 0.5f;
            }),
            $"sizes={string.Join(",", rects.Select(box => box.Size.ToString()))} floor={floor:F1} (44 pt up to 8 options, the shrink from 9)");
        Rect2 safe = PortraitSafeRect();
        float header = PortraitLayout.ComputeCapHeight(_tree.Root) + PortraitBands.TopBarHeight;
        _report.Check("rest_site_options_in_safe_frame",
            rects.All(box => safe.Grow(1f).Encloses(box)) && row.Position.Y >= header - 1f,
            $"safe={safe} row={row} header={header:F0} rects={string.Join("; ", rects)}");
        Rect2[] shrunk = rects.Select(box => box.Grow(-0.5f)).ToArray();
        bool overlap = shrunk.SelectMany((box, i) => shrunk.Skip(i + 1).Select(other => box.Intersects(other))).Any(hit => hit);
        _report.Check("rest_site_options_do_not_overlap", !overlap, $"rects={string.Join("; ", rects)}");
        float[] labels = buttons.Select(button =>
            button.GetNodeOrNull<Control>("Label") is { } label
                ? label.GetThemeFontSize("font_size") * label.GetGlobalTransformWithCanvas().Scale.Y
                : 0f).ToArray();
        _report.Check("rest_site_option_labels_readable", labels.Length > 0 && labels.All(height => height >= 28f - 0.5f),
            $"labelHeights={string.Join(",", labels.Select(height => height.ToString("F1")))} floor=28 (the game's own 28-unit label)");
    }

    private partial void CheckRestRoomLayout(NRestSiteRoom room)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        Vector2 canvas = GetViewportSize();
        Rect2 canvasRect = new(Vector2.Zero, canvas);
        List<NRestSiteButton> buttons = FindAll<NRestSiteButton>(room);
        Rect2 row = LauncherUnion(buttons.Select(button => button.GetGlobalRect()));
        float header = PortraitLayout.ComputeCapHeight(_tree.Root) + PortraitBands.TopBarHeight;
        _report.Check("rest_options_row_inside_canvas",
            buttons.Count > 0 && LauncherOnCanvas(row, canvas) && row.Position.Y >= header - 1f,
            $"row={row} header={header:F0} canvas={canvas}");
        _report.Check("rest_art_covers_canvas", LauncherCanvasCover(room, canvasRect) is { } cover,
            $"cover={(LauncherCanvasCover(room, canvasRect) is { } found ? found.GetPath() : "none")}");
    }

    /// <summary>Portrait uses select-then-confirm; landscape keeps the game's native single tap.</summary>
    private partial bool RestOptionsTakeTwoTaps() => IsPortraitHost();

    /// <summary>A selected-but-untaken option is the policy's selected item and carries the game's reticle as its child.</summary>
    private partial bool RestOptionSelected(NRestSiteButton button)
    {
        if (!GodotObject.IsInstanceValid(button)) return false;
        bool selected = NRestSiteRoom.Instance is { } room && ReferenceEquals(PhoneSelectConfirm.SelectedItemOn(room), button);
        // The departing reticle can still be fading when a new one is added; Godot may rename the new node.
        bool reticle = button.GetChildren().OfType<NSelectionReticle>().Any(candidate => candidate.IsVisibleInTree());
        return selected && reticle;
    }

    /// <summary>
    /// The selected option wears the game's focus look (<c>NRestSiteButton.OnFocus</c>): visuals at 1.1, outline at 1, the
    /// icon brightened to 1.2, with the reticle around it; the unfocus tween would take them to 1, 0.9 and 1 over 1 s.
    /// </summary>
    private partial void CheckRestSelectedLooksHovered(NRestSiteButton button)
    {
        if (!GodotObject.IsInstanceValid(button)) return;
        float visuals = button.GetNode<Control>("%Visuals").Scale.X;
        float outline = button.GetNode<Control>("%Outline").Scale.X;
        float bright = button.GetNode<TextureRect>("%Icon").Material is ShaderMaterial hsv
            ? hsv.GetShaderParameter("v").AsSingle() : -1f;
        _report.Check("rest_selected_option_looks_hovered", RestOptionSelected(button)
            && Mathf.Abs(visuals - 1.1f) < 0.01f && Mathf.Abs(outline - 1f) < 0.01f && Mathf.Abs(bright - 1.2f) < 0.01f,
            $"selected={RestOptionSelected(button)} visuals={visuals:F3} outline={outline:F3} v={bright:F3}");
    }

    /// <summary>
    /// The option copy (the native description the room shows on hover/focus) and the question against the buttons' drawn
    /// art and labels: no drawn line may touch an option, the copy must stay in the safe frame and clear of the shared action
    /// lane, and its text must read at least 28 units tall.
    /// </summary>
    private partial void CheckRestDescriptionClear(NRestSiteRoom room, int count)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        Vector2 canvas = GetViewportSize();
        Rect2 safe = PortraitSafeRect();
        List<NRestSiteButton> buttons = FindAll<NRestSiteButton>(room)
            .Where(button => button.IsVisibleInTree() && !button.IsQueuedForDeletion()).ToList();
        Rect2[] arts = buttons.Select(button => button.GetGlobalRect()).ToArray();
        Rect2[] labels = buttons.Select(RestLabelInk).ToArray();
        Rect2 rows = LauncherUnion(arts.Concat(labels));
        RichTextLabel? copy = room.GetNodeOrNull<RichTextLabel>("%Description");
        Rect2 copyRect = RestCopyTextRect(copy);
        Rect2? question = room.GetNodeOrNull<Control>("%Header")?.GetGlobalRect();
        bool clearOfOptions = !arts.Concat(labels).Any(rect => rect.Intersects(copyRect));
        bool clearOfQuestion = question is not { } questionRect || !questionRect.Intersects(copyRect);
        Rect2 lane = RestActionLane(room, canvas);
        _report.Check($"rest_description_clear_of_options_{count}",
            copy != null && copyRect.Size.Y >= 28f && clearOfOptions && clearOfQuestion
            && safe.Grow(1f).Encloses(copyRect) && !lane.Intersects(copyRect),
            $"copy={copyRect} rows={rows} question={question} lane={lane} safe={safe} " +
            $"options={(clearOfOptions ? "clear" : "overlap")} questionClear={clearOfQuestion}");
        _report.Check($"rest_proceed_lane_clear_of_options_{count}",
            lane.Position.Y > rows.End.Y && !arts.Concat(labels).Any(rect => rect.Intersects(lane)),
            $"lane={lane} rows={rows}");
        _report.Check($"rest_question_clear_of_options_{count}",
            question is not { } header || (!header.Intersects(rows) && safe.Grow(1f).Encloses(header)),
            $"question={question} rows={rows}");
    }

    /// <summary>
    /// The room's Proceed after an option is spent stands with its top at 80 % of the canvas height, within the thumb's
    /// reach, and never touches an option or the copy.
    /// </summary>
    private partial void CheckRestProceedLane(string name, NRestSiteRoom room)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        Vector2 canvas = GetViewportSize();
        Rect2 proceed = room.ProceedButton.GetGlobalRect();
        float laneTop = canvas.Y * RestProceedTopFraction;
        bool onLane = Math.Abs(proceed.Position.Y - laneTop) <= 1.5f;
        List<Rect2> others = FindAll<NRestSiteButton>(room)
            .Where(button => button.IsVisibleInTree() && !button.IsQueuedForDeletion())
            .Select(button => button.GetGlobalRect())
            .ToList();
        RichTextLabel? copy = room.GetNodeOrNull<RichTextLabel>("%Description");
        Rect2 copyRect = RestCopyTextRect(copy);
        if (copyRect.Size.Y > 0f) others.Add(copyRect);
        bool clear = others.All(rect => !rect.Intersects(proceed.Grow(-1f)));
        _report.Check($"rest_options_{name}_proceed_at_80pct",
            onLane && proceed.Size.Y >= PortraitBands.MinTarget - 1f && clear,
            $"proceed={proceed} laneTop={laneTop:F1} onLane={onLane} clear={clear} " +
            $"others={string.Join("; ", others)}");
    }

    /// <summary>Where the user wants the rest site's Proceed: its top at 80 % of the canvas height.</summary>
    private const float RestProceedTopFraction = 0.8f;

    /// <summary>An option's label as it is drawn: the label's top line of text, not its full-rect box.</summary>
    private static Rect2 RestLabelInk(NRestSiteButton button)
    {
        if (button.GetNodeOrNull<Control>("Label") is not { } label) return button.GetGlobalRect();
        Rect2 rect = label.GetGlobalRect();
        float ink = label.GetThemeFontSize("font_size") * Math.Abs(label.GetGlobalTransformWithCanvas().Scale.Y) * 1.2f;
        return new Rect2(rect.Position, new Vector2(rect.Size.X, Math.Min(rect.Size.Y, ink)));
    }

    /// <summary>The copy's drawn lines: where the label's text starts and how tall its rendered content is.</summary>
    private static Rect2 RestCopyTextRect(RichTextLabel? copy)
    {
        if (copy == null || !copy.IsVisibleInTree()) return default;
        Rect2 rect = copy.GetGlobalRect();
        float height = Math.Min(rect.Size.Y, copy.GetContentHeight());
        return new Rect2(rect.Position, new Vector2(rect.Size.X, height));
    }

    /// <summary>Rest options keep clear of the lane the room's Proceed uses.</summary>
    private static Rect2 RestActionLane(NRestSiteRoom room, Vector2 canvas)
    {
        float width = room.GetNodeOrNull<Control>("ProceedButton")?.Size.X ?? 269f;
        float top = canvas.Y * RestProceedTopFraction;
        return new Rect2(canvas.X - width - PortraitBetweenMath.ActionRightMargin, top, width, PortraitBands.MinTarget);
    }

}
