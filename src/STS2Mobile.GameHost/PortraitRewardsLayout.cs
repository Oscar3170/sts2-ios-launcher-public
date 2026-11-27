using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using STS2Mobile.Platform;

namespace STS2Mobile.GameHost;

/// <summary>
/// The spoils screen and the card reward in portrait: the loot window scaled so its banner fits the phone, cut to its rows
/// and centred so the reward buttons sit around the middle of the screen, the loot Skip hidden while a card reward is open
/// over it, and the reward cards in a two-column grid under the shared banner with the alternatives in one row above the
/// footer. The game's own values are captured before the first move and put back when Portrait Mode is turned off. The
/// action lane and the shared banner helpers stay with the between-screen coordinator.
/// </summary>
internal sealed class PortraitRewardsLayout
{
    public const string RewardsScreenTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.NRewardsScreen";
    public const string CardRewardTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NCardRewardSelectionScreen";

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
    private const float NativeLootWidth = 526f;
    private const float NativeLootHeight = 640f;

    /// <summary>One loot screen's window, the list's mask and the list, with the lane its action hangs from.</summary>
    private sealed class LootScreenState(Control screen, Control window, Control mask, Control list, Control? action, bool blitz)
    {
        public readonly Control Screen = screen;
        public readonly Control Window = window;
        public readonly Control Mask = mask;
        public readonly Control List = list;
        public readonly Control? Action = action;
        public readonly bool Blitz = blitz;
        public Control? Banner;
        public int RowCount = -1;
        public float ContentHeight;
        public bool Placed;
        public Tween? Fade;
    }

    private readonly Dictionary<ulong, PortraitControlSnapshot> _snapshots = new();
    private readonly HashSet<ulong> _cardRewards = new();
    private readonly Dictionary<ulong, List<ulong>> _laidOutRewardHolders = new();
    private readonly Dictionary<ulong, LootScreenState> _lootScreens = new();
    private readonly HashSet<ulong> _lootRows = new();
    private readonly HashSet<ulong> _lootSkipsHidden = new();
    private readonly HashSet<ulong> _lootBannersHidden = new();
    private readonly Dictionary<ulong, (bool Had, bool Value)> _rewardAutoSizes = new();
    private readonly Dictionary<ulong, BoxContainer.AlignmentMode> _alternativeAlignments = new();
    private readonly HashSet<ulong> _liftedBanners = new();
    private readonly Dictionary<ulong, (bool Had, int Size)[]> _richFontSizes = new();

    private static FieldInfo? _rewardsFadeTween;

    // ---- entry points from PortraitBetweenLayout ---------------------------------------------------------------

    /// <summary>Lays one loot screen's window and rows out for portrait; cheap to call again when the tree adds it.</summary>
    public void Apply(Control screen, float capHeight, Vector2 canvas) => LayoutRewardsPortrait(screen, capHeight, canvas);

    public void ApplyCardReward(Control screen) => _cardRewards.Add(screen.GetInstanceId());

    /// <summary>The loot screen's own Skip stays hidden while a card reward is open over it.</summary>
    public void UpdateSkipVisibility() => KeepLootSkipsClearOfCardReward(CardRewardShown());

    public void Update(Vector2 canvas, float capHeight)
    {
        bool cardRewardOpen = CardRewardShown();
        foreach (LootScreenState state in _lootScreens.Values)
            if (GodotObject.IsInstanceValid(state.Screen) && state.Screen.IsVisibleInTree())
                UpdateLootPortrait(state, canvas, capHeight, cardRewardOpen);
        foreach (ulong id in _cardRewards)
            if (GodotObject.InstanceFromId(id) is Control screen && GodotObject.IsInstanceValid(screen) && screen.IsInsideTree())
                UpdateCardRewardPortrait(screen, canvas, capHeight);
    }

    // ---- restore --------------------------------------------------------------------------------------------------------

    /// <summary>The reward cards go back to the game's single row, and the next opening gets fresh slot order.</summary>
    public void RestoreCardRewardsLandscape()
    {
        foreach (ulong id in _cardRewards)
            if (GodotObject.InstanceFromId(id) is Control screen && GodotObject.IsInstanceValid(screen))
                RestoreCardRewardLandscape(screen);
        _laidOutRewardHolders.Clear();
    }

    /// <summary>The loot window's entrance is ours while portrait; kill it before the snapshots put the game's layout back.</summary>
    public void RestoreLootLandscape()
    {
        foreach (LootScreenState loot in _lootScreens.Values)
            if (loot.Fade is { } fade && GodotObject.IsInstanceValid(fade)) fade.Kill();
        _lootScreens.Clear();
        _lootRows.Clear();
    }

    public void RestoreHiddenSkips()
    {
        foreach (ulong id in _lootSkipsHidden)
            if (GodotObject.InstanceFromId(id) is Control hidden && GodotObject.IsInstanceValid(hidden) && !hidden.Visible)
                hidden.Show();
        _lootSkipsHidden.Clear();
    }

    public void RestoreControls()
    {
        foreach ((ulong id, PortraitControlSnapshot snapshot) in _snapshots)
            if (GodotObject.InstanceFromId(id) is Control control && GodotObject.IsInstanceValid(control))
                snapshot.Restore(control);
        _snapshots.Clear();
        _lootBannersHidden.Clear();
    }

    public void RestoreAlignments()
    {
        foreach ((ulong id, BoxContainer.AlignmentMode alignment) in _alternativeAlignments)
            if (GodotObject.InstanceFromId(id) is BoxContainer box && GodotObject.IsInstanceValid(box))
                box.Alignment = alignment;
        _alternativeAlignments.Clear();
    }

    public void RestoreBanners() => PortraitBetweenLayout.RestoreBanners(_liftedBanners);

    public void RestoreFonts()
    {
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
    }

    public void PruneDead()
    {
        foreach (ulong id in _snapshots.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _snapshots.Remove(id);
        _cardRewards.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        foreach (ulong id in _laidOutRewardHolders.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList())
            _laidOutRewardHolders.Remove(id);
        _liftedBanners.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        _lootRows.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        _lootSkipsHidden.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        _lootBannersHidden.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        foreach (ulong id in _lootScreens.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _lootScreens.Remove(id);
        foreach (ulong id in _rewardAutoSizes.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _rewardAutoSizes.Remove(id);
        foreach (ulong id in _alternativeAlignments.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _alternativeAlignments.Remove(id);
        foreach (ulong id in _richFontSizes.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _richFontSizes.Remove(id);
    }

    // ---- spoils screen --------------------------------------------------------------------------------------------------

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
                screen, window, mask, list, screen.GetNodeOrNull<Control>("ProceedButton"), PortraitBetweenLayout.IsBlitzRun());
        Capture(window, captureVisible: false);
        Capture(mask, captureVisible: false);
        Capture(list, captureVisible: false);
        if (state.Banner == null && window.GetNodeOrNull<Control>("Background/Banner") is { } banner)
        {
            state.Banner = banner;
            Capture(banner, captureVisible: true);
        }
        // Rows are two units taller and 14 apart (was 86 and 10), so a loot row is at least 44 points deep.
        if (!list.HasThemeConstantOverride("separation") ||
            list.GetThemeConstant("separation") != (int)PortraitBetweenMath.LootRowSeparation)
            list.AddThemeConstantOverride("separation", (int)PortraitBetweenMath.LootRowSeparation);
        UpdateLootPortrait(state, canvas, capHeight, CardRewardShown());
    }

    private void UpdateLootPortrait(LootScreenState state, Vector2 canvas, float capHeight, bool cardRewardOpen)
    {
        // The rows are laid out first: the window's height and place come from how tall their block is (the list keeps one
        // row size; its labels read at 15 points; it is walked only when its rows change).
        int rowCount = state.List.GetChildCount();
        if (rowCount != state.RowCount) ApplyLootRows(state, rowCount);

        // A card reward over the loot window has its own banner just under the stone header; the loot window's banner now
        // stands where that one is drawn, so it steps aside while the reward is open and comes back with the loot rows.
        if (state.Banner is { } lootBanner)
        {
            ulong bannerId = lootBanner.GetInstanceId();
            if (cardRewardOpen && lootBanner.Visible)
            {
                lootBanner.Hide();
                _lootBannersHidden.Add(bannerId);
            }
            else if (!cardRewardOpen && _lootBannersHidden.Remove(bannerId) && !lootBanner.Visible)
            {
                lootBanner.Show();
            }
        }

        float laneTop = PortraitBetweenMath.ActionLaneTop(state.Blitz, canvas.Y, PortraitBands.MinTarget);
        LootWindowFit fit = PortraitBetweenMath.LootWindow(
            canvas.X, canvas.Y, PortraitBands.HeaderBottom(capHeight), laneTop, NativeLootWidth, NativeLootHeight,
            state.ContentHeight);
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
            Math.Abs(mask.OffsetLeft - PortraitBetweenMath.LootMaskLeft) > 0.01f ||
            Math.Abs(mask.OffsetRight + PortraitBetweenMath.LootMaskRight) > 0.01f ||
            Math.Abs(mask.OffsetTop - PortraitBetweenMath.LootMaskTop) > 0.01f ||
            Math.Abs(mask.OffsetBottom + PortraitBetweenMath.LootMaskBottom) > 0.01f)
        {
            mask.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            mask.OffsetLeft = PortraitBetweenMath.LootMaskLeft;
            mask.OffsetRight = -PortraitBetweenMath.LootMaskRight;
            mask.OffsetTop = PortraitBetweenMath.LootMaskTop;
            mask.OffsetBottom = -PortraitBetweenMath.LootMaskBottom;
        }

        var target = new Vector2(fit.X, fit.Y);
        FieldInfo? fadeField = RewardsFadeTween(state.Screen.GetType());
        Tween? fadeTween = fadeField?.GetValue(state.Screen) as Tween;
        // Only the entrance is taken over. A complete set (the final boss offers none; a terminal set whose last reward was
        // taken) fades its empty window out instead, and turning that fade-out into this fade-in showed an empty loot window.
        bool complete = state.Screen.Get("IsComplete").AsBool();
        if (!complete && fadeTween != null && fadeTween.IsValid() && fadeTween.IsRunning() && state.Fade != fadeTween)
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
            ulong actionId = action.GetInstanceId();
            if (cardRewardOpen && state.Screen.IsVisibleInTree() && PortraitBetweenLayout.ProceedIsSkip(action) && action.Visible)
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

    // ---- card reward: 2 x 2 grid ---------------------------------------------------------------------------------------

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
        float lift = PortraitBetweenLayout.BannerLift(canvas, capHeight);
        PortraitBetweenLayout.LiftBanner(screen.GetNodeOrNull<Control>("UI/Banner"), canvas, lift, _liftedBanners);
        float bannerBottom = centreY - PortraitBetweenLayout.BannerBottomOffset - lift;
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

    private static FieldInfo? RewardsFadeTween(Type screenType)
    {
        _rewardsFadeTween ??= screenType.GetField("_fadeTween", BindingFlags.Instance | BindingFlags.NonPublic);
        return _rewardsFadeTween;
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
}
