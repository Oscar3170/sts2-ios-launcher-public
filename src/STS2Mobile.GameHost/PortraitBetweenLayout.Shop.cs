using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using STS2Mobile.Platform;

namespace STS2Mobile.GameHost;

/// <summary>
/// The shop in portrait: the native rug turned upright as the shop's cloth, the Cards and Items pages with their two
/// settings tabs, the weird shop's relic grid, the Blitz shop's two columns over its remaining-picks line, and the shop
/// room's full-bleed backdrop with the merchant and the player seated as the room's own scene has them. Every control keeps
/// its game layout for landscape; the stock groups are never reparented, only placed and shown or hidden by page.
/// </summary>
internal sealed partial class PortraitBetweenLayout
{
    public const string ShopClothName = "PhoneShopCloth";
    public const string ShopCardsTabName = "PhoneShopCardsTab";
    public const string ShopItemsTabName = "PhoneShopItemsTab";
    public const string ShopBlitzUnderlayName = "PhoneShopBlitzUnderlay";
    public const string ShopBlitzFadeName = "PhoneShopBlitzFade";

    /// <summary>0 for Cards, 1 for Items, -1 when the open shop has no pages (or none is open).</summary>
    public static int ShopPage { get; private set; } = -1;

    private const string ShopTabScene = "res://scenes/screens/settings_tab.tscn";
    private const BindingFlags ShopInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    // The native open destination and the game's closed destination (the latter is replaced for the taller cloth).
    private const float ShopOpenY = 80f;
    private const float ShopNativeClosedY = -1000f;
    private const float ShopClosedSeconds = 0.5f;
    private const float ShopBackstopSeconds = 0.8f;
    // The merchant's speech bubble reaches 182 units left of its point and 98 right.
    private const float ShopDialogueLeftReach = 182f;
    private const float ShopDialogueRightReach = 98f;
    // A shop price's number on the screen, in canvas units: 32 is 11 points on a 375 point wide phone.
    private const float ShopPriceUnits = 32f;
    private const float ShopItemPadWidth = 127f;
    private const float ShopItemPadHeight = 127f;
    private const float ShopBackFallbackY = 355f;
    private const float ShopTabMargin = 16f;

    // The shop room's authored composition, in the room scene's own units (measured from the native art).
    private static readonly Vector2 ShopAuthoredSize = new(2560f, 1200f);
    private static readonly Vector2 ShopArtOrigin = new(-10f, 20f);
    private const float ShopArtScale = 1.01f;
    private const float ShopCoverScale = 1.035f;

    private readonly Dictionary<ulong, ShopState> _shopStates = new();
    private readonly Dictionary<ulong, ShopRoomState> _shopRoomStates = new();
    private readonly Dictionary<ulong, Texture2D> _rotatedCloths = new();
    private static Shader? _shopBlitzFadeShader;

    private sealed class ShopState
    {
        public ulong Id;
        public Control? Inventory;
        public TextureRect? Stock;
        public TextureRect? Cloth;
        public Control? Backstop;
        public Control? InputBlocker;
        public Node2D? Dialogue;
        public Control? BackButton;
        public Control? CardsTab;
        public Control? ItemsTab;
        public Texture2D? StockTexture;
        public bool StockClip;
        public TextureRect.ExpandModeEnum StockExpand;
        public TextureRect.StretchModeEnum StockStretch;
        public PropertyInfo? OpenProperty;
        public Func<bool>? IsOpen;
        public FieldInfo? TweenField;
        public FieldInfo? BackShowPosField;
        public FieldInfo? BackHidePosField;
        public FieldInfo? BackMoveTweenField;
        public Vector2 BackShowHome;
        public Vector2 BackHideHome;
        public Vector2 BackSizeHome;
        public bool BackCaptured;
        public FieldInfo? HoverTweenField;
        public Vector2 Canvas;
        public Vector2 DialogueHome;
        public bool DialogueCaptured;
        public bool Paged;
        public bool Weird;
        public bool Blitz;
        public int Page;
        public bool WasOpen;
        public bool LaidOut;
        public bool Dirty = true;
        public bool Failed;
        public float ClosedY;
        public float RemovalScale;
        public Tween? CloseTween;
        public bool TabPressed;
        public int TabPressPage = -1;
        public Vector2 TabPressPosition;
        public readonly List<Control> CardGroups = new();
        public readonly List<Control> ItemGroups = new();
        public readonly List<Control> Cards = new();
        public readonly List<Control> Colorless = new();
        public readonly List<Control> Relics = new();
        public readonly List<Control> Potions = new();
        public readonly List<Control> BlitzSlots = new();
        public Control? Removal;
        public Control? CharacterGroup;
        public Control? ColorlessGroup;
        public Control? RelicsGroup;
        public Control? PotionsGroup;
        public Control? BlitzGroup;
        public bool BlitzRoom;
        public Control? BlitzFooter;
        public bool? BlitzFooterFitContent;
        public int GroupSignature = -1;
    }

    private sealed class ShopRoomState
    {
        public ulong Id;
        public string TypeName = "";
        public Vector2 Canvas;
        public bool Blitz;
        public Control? Room;
        public Control? Scene;
        public Control? Bg;
        public Control? Party;
        public Control? Merchant;
        public Control? BlitzMerchant;
        public Vector2 MerchantHome;
        public readonly List<ShopRoomLayer> Layers = new();
        public TextureRect? Underlay;
        public TextureRect? Fade;
        public bool Applied;
        public bool Failed;
    }

    private sealed class ShopRoomLayer
    {
        public Node2D Node = null!;
        public Vector2 HomePosition;
        public Vector2 HomeScale;
    }

    // ---- The shop inventory --------------------------------------------------------------------------------------------

    /// <summary>
    /// One shop, laid out when its mode or the canvas changes, then kept: page changes only toggle the groups'
    /// visibility, so stock never reshuffles after a purchase and Courier restock keeps every slot where it was.
    /// </summary>
    private void UpdateShopPortrait(Control inventory, float capHeight, Vector2 canvas)
    {
        try
        {
            if (!inventory.IsInsideTree() || !inventory.IsNodeReady()) return;
            ulong id = inventory.GetInstanceId();
            if (!_shopStates.TryGetValue(id, out ShopState? state) || state.Stock is null ||
                !GodotObject.IsInstanceValid(state.Stock))
            {
                if (inventory.GetNodeOrNull<TextureRect>("%SlotsContainer") is not { } stock) return;
                state = CreateShopState(inventory, stock);
                _shopStates[id] = state;
            }
            // Without the game's open state the shop cannot be told open from closed; leave it as the game has it.
            if (state.OpenProperty == null || state.Failed) return;

            // The room fills the stock after the inventory enters the tree; find the slots again whenever that changes.
            RefreshShopGroups(state);
            bool open = state.IsOpen?.Invoke() ?? false;
            if (state.Canvas != canvas) state.Dirty = true;
            // The stock is complete by the time it can open: one fresh layout on the opening frame settles any late content.
            if (open && !state.WasOpen) state.Dirty = true;
            if (state.Dirty || !state.LaidOut)
            {
                ApplyShopLayout(inventory, state, capHeight, canvas, open);
                state.Canvas = canvas;
                state.Dirty = false;
                state.LaidOut = true;
            }

            PlaceShopBackButton(state, canvas);
            if (open)
            {
                if (!state.WasOpen) OnShopOpened(inventory, state);
                state.WasOpen = true;
                PlaceShopTabs(inventory, state, canvas, open: true);
            }
            else
            {
                if (state.WasOpen) OnShopClosed(inventory, state);
                state.WasOpen = false;
                KeepShopClosed(inventory, state, canvas);
                PlaceShopTabs(inventory, state, canvas, open: false);
            }

            KeepShopDialogueInside(state, canvas);
            KeepRemovalScaled(state);
        }
        catch (Exception error)
        {
            if (_shopStates.TryGetValue(inventory.GetInstanceId(), out ShopState? failed)) failed.Failed = true;
            GD.PushError("Portrait between layout: the shop could not be laid out: " + error.Message);
        }
    }

    private ShopState CreateShopState(Control inventory, TextureRect stock)
    {
        var state = new ShopState
        {
            Id = inventory.GetInstanceId(),
            Inventory = inventory,
            Stock = stock,
            Backstop = inventory.GetNodeOrNull<Control>("Backstop") ?? inventory.GetNodeOrNull<Control>("%Backstop"),
            InputBlocker = inventory.GetNodeOrNull<Control>("%InputBlocker"),
            Dialogue = inventory.GetNodeOrNull<Node2D>("%Dialogue"),
            BackButton = inventory.GetNodeOrNull<Control>("%BackButton"),
            OpenProperty = inventory.GetType().GetProperty("IsOpen", ShopInstance),
            TweenField = FindPrivateField(inventory.GetType(), "_inventoryTween"),
        };
        if (state.OpenProperty?.GetMethod is { } getter)
        {
            try { state.IsOpen = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), inventory, getter); }
            catch (ArgumentException) { state.IsOpen = null; }
        }
        if (state.BackButton is { } backButton)
        {
            state.BackShowPosField = FindPrivateField(backButton.GetType(), "_showPos");
            state.BackHidePosField = FindPrivateField(backButton.GetType(), "_hidePos");
            state.BackMoveTweenField = FindPrivateField(backButton.GetType(), "_moveTween");
            state.BackSizeHome = backButton.Size;
            if (state.BackShowPosField?.GetValue(backButton) is Vector2 showHome) state.BackShowHome = showHome;
            if (state.BackHidePosField?.GetValue(backButton) is Vector2 hideHome) state.BackHideHome = hideHome;
            state.BackCaptured = state.BackShowPosField != null;
            Capture(backButton, captureVisible: false);
        }
        state.Weird = inventory.GetType().FullName == FakeMerchantInventoryTypeName;

        // The stock groups are scene nodes; their slots are filled in after the inventory is readied, so only the groups are
        // kept here and the slots are found again whenever their number changes (see RefreshShopGroups).
        state.CharacterGroup = FindShopGroup(inventory, stock, "CharacterCards");
        state.ColorlessGroup = FindShopGroup(inventory, stock, "ColorlessCards");
        state.RelicsGroup = FindShopGroup(inventory, stock, "Relics");
        state.PotionsGroup = FindShopGroup(inventory, stock, "Potions");
        state.BlitzGroup = FindShopGroup(inventory, stock, "BlitzSlots");
        state.Removal = stock.GetNodeOrNull<Control>("MerchantCardRemoval") ??
                        inventory.GetNodeOrNull<Control>("%MerchantCardRemoval");
        // The standard inventory carries the Blitz container too, hidden and full: the room decides the mode, not the count.
        state.BlitzRoom = IsInBlitzRoom(inventory) || state.BlitzGroup?.Visible == true;
        state.BlitzFooter = stock.GetNodeOrNull<Control>("BlitzSelectionText") ??
                            inventory.GetNodeOrNull<Control>("%BlitzSelectionText");
        RefreshShopGroups(state);
        return state;
    }

    private static Control? FindShopGroup(Control inventory, TextureRect stock, string name) =>
        stock.GetNodeOrNull<Control>(name) ?? inventory.GetNodeOrNull<Control>("%" + name);

    private static bool IsInBlitzRoom(Node node)
    {
        for (Node? parent = node.GetParent(); parent != null; parent = parent.GetParent())
            if (parent.GetType().FullName == BlitzMerchantRoomTypeName) return true;
        return false;
    }

    private static int GroupChildren(Control? group) =>
        group != null && GodotObject.IsInstanceValid(group) ? group.GetChildCount() : 0;

    /// <summary>
    /// Finds the stocked slots again whenever the game has filled or rebuilt the groups. The room creates the slots after the
    /// inventory enters the tree (a first pass sees empty groups), so the mode and every list are re-read from the group child
    /// counts; a change asks for one new layout, never a reshuffle at any other time.
    /// </summary>
    private void RefreshShopGroups(ShopState state)
    {
        int signature = 17;
        signature = signature * 31 + GroupChildren(state.CharacterGroup);
        signature = signature * 31 + GroupChildren(state.ColorlessGroup);
        signature = signature * 31 + GroupChildren(state.RelicsGroup);
        signature = signature * 31 + GroupChildren(state.PotionsGroup);
        signature = signature * 31 + GroupChildren(state.BlitzGroup);
        signature = signature * 31 + (state.Removal != null && GodotObject.IsInstanceValid(state.Removal) ? 1 : 0);
        if (signature == state.GroupSignature) return;
        state.GroupSignature = signature;

        state.CardGroups.Clear();
        state.ItemGroups.Clear();
        state.Cards.Clear();
        state.Colorless.Clear();
        state.Relics.Clear();
        state.Potions.Clear();
        state.BlitzSlots.Clear();
        AddGroupSlots(state, state.CharacterGroup, state.CardGroups, state.Cards);
        AddGroupSlots(state, state.ColorlessGroup, state.CardGroups, state.Colorless);
        AddGroupSlots(state, state.RelicsGroup, state.ItemGroups, state.Relics);
        AddGroupSlots(state, state.PotionsGroup, state.ItemGroups, state.Potions);
        // Only a Blitz room's container is stock: in a standard room the same scene node holds six hidden slots.
        if (state.BlitzRoom) AddGroupSlots(state, state.BlitzGroup, state.ItemGroups, state.BlitzSlots);
        if (state.Removal != null && GodotObject.IsInstanceValid(state.Removal)) state.ItemGroups.Add(state.Removal);

        state.Blitz = state.BlitzSlots.Count > 0;
        state.Paged = !state.Weird && !state.Blitz && state.CardGroups.Count > 0 && state.ItemGroups.Count > 0;
        if (state.BlitzSlots.Count > 0 && state.HoverTweenField == null)
            state.HoverTweenField = FindPrivateField(state.BlitzSlots[0].GetType(), "_hoverTween");
        if (state.Removal != null && state.HoverTweenField == null)
            state.HoverTweenField = FindPrivateField(state.Removal.GetType(), "_hoverTween");

        // Everything this pass moves or hides is kept first, so leaving portrait puts the game's own shop back exactly.
        foreach (Control group in state.CardGroups) Capture(group, captureVisible: true);
        foreach (Control group in state.ItemGroups) Capture(group, captureVisible: true);
        foreach (Control slot in state.Cards) Capture(slot, captureVisible: false);
        foreach (Control slot in state.Colorless) Capture(slot, captureVisible: false);
        foreach (Control slot in state.Relics) Capture(slot, captureVisible: false);
        foreach (Control slot in state.Potions) Capture(slot, captureVisible: false);
        foreach (Control slot in state.BlitzSlots) Capture(slot, captureVisible: false);
        if (state.Removal != null) Capture(state.Removal, captureVisible: true);

        state.Dirty = true;
        GD.Print($"Portrait between layout: shop mode {(state.Paged ? "pages" : state.Weird ? "weird" : state.Blitz ? "blitz" : "stock")}: " +
                 $"cards={state.Cards.Count} colorless={state.Colorless.Count} relics={state.Relics.Count} " +
                 $"potions={state.Potions.Count} blitz={state.BlitzSlots.Count} removal={state.Removal != null} paged={state.Paged}.");
    }

    private static void AddGroupSlots(ShopState state, Control? group, List<Control> groups, List<Control> slots)
    {
        if (group == null || !GodotObject.IsInstanceValid(group)) return;
        List<Control> children = group.GetChildren().OfType<Control>().ToList();
        if (children.Count == 0) return;
        groups.Add(group);
        slots.AddRange(children);
    }

    private static FieldInfo? FindPrivateField(Type type, string name)
    {
        for (Type? current = type; current != null; current = current.BaseType)
            if (current.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                                       BindingFlags.DeclaredOnly) is { } field)
                return field;
        return null;
    }

    private void ApplyShopLayout(Control inventory, ShopState state, float capHeight, Vector2 canvas, bool open)
    {
        TextureRect stock = state.Stock!;
        float top = capHeight + PortraitBands.RunContentTop + 8f;
        ShopLayoutRect cloth = PortraitShopMath.ClothRect(canvas.X, top, canvas.Y);
        // The tabs sit in the bottom band under the stock's content: every card, removal and price stays above them.
        float backHeight = state.BackCaptured ? state.BackSizeHome.Y : 110f;
        float tabTop = PortraitShopMath.TabRowTop(canvas.Y, backHeight);
        ShopLayoutRect rect = PortraitShopMath.StockRect(cloth, tabTop - ShopTabMargin);

        // The rug stands upright behind the stock; the game's own rug is taken off the stock so only the rotated one shows.
        if (state.StockTexture == null && stock.Texture != null)
        {
            state.StockTexture = stock.Texture;
            state.StockClip = stock.ClipContents;
            state.StockExpand = stock.ExpandMode;
            state.StockStretch = stock.StretchMode;
        }
        EnsureShopCloth(state);
        if (state.Cloth is { } clothNode)
            clothNode.Texture = RotatedShopCloth(state.StockTexture);

        Capture(inventory, captureVisible: false);
        // Position the inventory so the stock's open Y (80, which the game tweens to) leaves the cloth's top margin above it
        // and puts the cloth's top at the band's start.
        Vector2 parentOrigin = inventory.GlobalPosition - inventory.Position;
        float stockTop = top + PortraitShopMath.ClothInsetTop;
        inventory.Position = new Vector2(-parentOrigin.X, stockTop - ShopOpenY - parentOrigin.Y);
        SetShopBackdrop(state, canvas);

        if (state.Dialogue is { } dialogue)
        {
            if (!state.DialogueCaptured)
            {
                state.DialogueHome = dialogue.Position;
                state.DialogueCaptured = true;
            }
            dialogue.Position = new Vector2(dialogue.Position.X, ShopOpenY - 54f);
        }

        Capture(stock, captureVisible: false);
        stock.Texture = null;
        stock.ClipContents = false;
        stock.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
        stock.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        stock.PivotOffset = Vector2.Zero;
        stock.Scale = Vector2.One;
        state.ClosedY = PortraitShopMath.ClosedY(inventory.GlobalPosition.Y, cloth.Height);
        float y = open || NativeShopTweenRunning(state, inventory) ? stock.Position.Y : state.ClosedY;
        stock.OffsetLeft = rect.X;
        stock.OffsetRight = rect.X + rect.Width;
        stock.OffsetTop = y;
        stock.OffsetBottom = y + rect.Height;
        bool sliding = NativeShopTweenRunning(state, inventory);
        if (open) stock.Show();
        else if (!sliding) stock.Hide();

        if (state.Blitz)
            PlaceBlitzPage(state, rect);
        else if (state.Weird)
            PlaceWeirdPage(state, rect);
        else
            PlacePagedShop(state, rect);

        float inner = Math.Max(0f, rect.Width - 2f * PortraitShopMath.Pad);
        PadShopRow(state.Cards, inner / 3f);
        PadShopRow(state.Colorless, inner / PortraitShopMath.CardColorlessSpread);
        PadShopRow(state.Relics, inner / Math.Max(1, state.Relics.Count));
        PadShopRow(state.Potions, inner / Math.Max(1, state.Potions.Count));
        PadShopRow(state.BlitzSlots, inner / 2f);
        if (state.Removal is { } removal && removal.GetNodeOrNull<Control>("%Hitbox") is { } removalHitbox)
            EnsureHitPad(removalHitbox, ShopItemPadWidth, ShopItemPadHeight);
        foreach (Node cost in stock.FindChildren("Cost", "HBoxContainer", recursive: true, owned: false))
            EnlargeShopPrice((Control)cost);

        EnsureShopTabs(inventory, state);
        if (state.Paged)
        {
            SetShopPage(inventory, state, open ? state.Page : 0, announce: false);
            if (!open) ShopPage = -1;
        }
        else
        {
            ShopPage = -1;
            HideShopTabs(state);
        }
        PlaceShopTabs(inventory, state, canvas, open);
    }

    private void PlacePagedShop(ShopState state, ShopLayoutRect rect)
    {
        int power = PowerCardIndex(state.Cards);
        ShopCardPage cards = PortraitShopMath.CardPage(state.Cards.Count, power, state.Colorless.Count, rect.Width, rect.Height);
        PlaceGroup(ItemGroupFor(state, state.Cards), cards.Scale, state.Cards, cards.ClassCards);
        PlaceGroup(ItemGroupFor(state, state.Colorless), cards.Scale, state.Colorless, cards.Colorless);

        ShopItemsPage items = PortraitShopMath.ItemsPage(state.Relics.Count, state.Potions.Count, rect.Width, rect.Height);
        PlaceGroup(ItemGroupFor(state, state.Relics), PortraitShopMath.RelicScale, state.Relics, items.Relics);
        PlaceGroup(ItemGroupFor(state, state.Potions), PortraitShopMath.PotionScale, state.Potions, items.Potions);

        if (state.Removal is { } removal)
        {
            float previousBottom = (rect.Height - PortraitShopMath.ItemsBottomInset) * 0.42f +
                                    PortraitShopMath.PotionScale * 160f;
            for (int i = 0; i < state.Potions.Count && i < items.Potions.Count; i++)
            {
                float costBottom = state.Potions[i].GetNodeOrNull<Control>("Cost") is { } cost
                    ? cost.GetRect().End.Y
                    : 160f;
                previousBottom = Math.Max(previousBottom, items.Potions[i].Y + PortraitShopMath.PotionScale * costBottom);
            }
            float nativeTop = removal.GetNodeOrNull<Control>("%Hitbox")?.Position.Y ?? -180f;
            float nativeBottom = removal.GetNodeOrNull<Control>("Cost") is { } removalCost ? removalCost.GetRect().End.Y : 210f;
            ShopSlotPlacement fitted = PortraitShopMath.FitRemoval(
                previousBottom, rect.Height, nativeTop, nativeBottom, PortraitShopMath.RemovalScale);
            removal.Position = new Vector2(rect.Width / 2f, fitted.Y);
            state.RemovalScale = fitted.Scale;
            removal.Scale = new Vector2(fitted.Scale, fitted.Scale);
        }
    }

    private void PlaceWeirdPage(ShopState state, ShopLayoutRect rect)
    {
        IReadOnlyList<ShopSlotPlacement> relics =
            PortraitShopMath.WeirdRelics(state.Relics.Count, rect.Width, rect.Height);
        float scale = relics.Count > 0 ? relics[0].Scale : PortraitShopMath.WeirdMaxScale;
        PlaceGroup(ItemGroupFor(state, state.Relics), scale, state.Relics, relics);
    }

    private void PlaceBlitzPage(ShopState state, ShopLayoutRect rect)
    {
        var footprints = new ShopBlitzFootprint[state.BlitzSlots.Count];
        for (int i = 0; i < state.BlitzSlots.Count; i++)
        {
            Control slot = state.BlitzSlots[i];
            Rect2 bounds = slot.GetNodeOrNull<Control>("%Hitbox")?.GetRect() ?? new Rect2(0f, 0f, 200f, 200f);
            if (slot.GetNodeOrNull<Control>("CostContainer") is { Visible: true } cost)
                bounds = bounds.Merge(cost.GetRect());
            footprints[i] = new ShopBlitzFootprint(bounds.GetCenter().Y, bounds.Size.Y);
        }
        IReadOnlyList<ShopSlotPlacement> slots = PortraitShopMath.BlitzSlots(footprints, rect.Width, rect.Height);
        float scale = slots.Count > 0 ? slots[0].Scale : PortraitShopMath.BlitzMaxScale;
        PlaceGroup(ItemGroupFor(state, state.BlitzSlots), scale, state.BlitzSlots, slots);

        if (state.BlitzGroup is { } blitzGroup && GodotObject.IsInstanceValid(blitzGroup) && !blitzGroup.Visible)
            blitzGroup.Show();
        // Only the Blitz stock belongs to this page: the shared scene's card, relic and potion containers stay out of it.
        foreach (Control group in state.CardGroups)
            if (GodotObject.IsInstanceValid(group) && group.Visible) group.Visible = false;
        foreach (Control group in state.ItemGroups)
            if (GodotObject.IsInstanceValid(group) && !ReferenceEquals(group, state.BlitzGroup) &&
                !ReferenceEquals(group, state.Removal) && group.Visible)
                group.Visible = false;
        if (state.BlitzFooter is { } summary && GodotObject.IsInstanceValid(summary))
        {
            Capture(summary, captureVisible: false);
            if (state.BlitzFooterFitContent == null && summary is RichTextLabel footerLabel)
                state.BlitzFooterFitContent = footerLabel.FitContent;
            summary.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
            // The label's own bounds kept it 1400 units wide (its scene size and fit-to-content): a page-wide footer is asked for.
            summary.CustomMinimumSize = Vector2.Zero;
            if (summary is RichTextLabel fitted) fitted.FitContent = false;
            summary.Size = new Vector2(rect.Width, PortraitShopMath.BlitzFooter);
            Vector2 footerAt = new(0f, rect.Height - PortraitShopMath.BlitzFooter);
            if (summary.GetParent() == state.Stock) summary.Position = footerAt;
            else if (state.Stock is { } stock) summary.GlobalPosition = stock.GlobalPosition + footerAt;
            summary.Set("MinFontSize", PortraitShopMath.BlitzFooterFontSize);
            summary.Set("MaxFontSize", PortraitShopMath.BlitzFooterFontSize);
            SetRichFontSize(summary, PortraitShopMath.BlitzFooterFontSize);
        }
    }

    /// <summary>
    /// One group of slots: the group carries the page's magnification, so the game's own hover scale on a slot keeps
    /// working and still returns to the size the game expects. Slot positions are given in canvas units and divided back
    /// into the group's own units.
    /// </summary>
    private static void PlaceGroup(Control? group, float scale, List<Control> slots,
        IReadOnlyList<ShopSlotPlacement> placements)
    {
        if (group == null || slots.Count == 0) return;
        float native = NativeSlotScale(slots[0]);
        if (native < 0.01f) native = 0.65f;
        float groupScale = Math.Max(0.01f, scale / native);
        group.PivotOffset = Vector2.Zero;
        group.Position = Vector2.Zero;
        group.Scale = new Vector2(groupScale, groupScale);
        for (int i = 0; i < slots.Count && i < placements.Count; i++)
            slots[i].Position = new Vector2(placements[i].X / groupScale, placements[i].Y / groupScale);
    }

    private static Control? ItemGroupFor(ShopState state, List<Control> slots)
    {
        if (slots.Count == 0) return null;
        return slots[0].GetParent() as Control;
    }

    private static float NativeSlotScale(Control slot)
    {
        try
        {
            for (Type? type = slot.GetType(); type != null; type = type.BaseType)
            {
                PropertyInfo? scale = type.GetProperty("SmallScale", BindingFlags.Instance | BindingFlags.Public |
                    BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (scale?.GetValue(slot) is Vector2 value && value.X > 0.01f) return value.X;
            }
        }
        catch (Exception)
        {
            // The game's slot no longer answers: fall back to the scale every shop slot starts at.
        }
        return 0.65f;
    }

    private static int PowerCardIndex(List<Control> cards)
    {
        for (int i = 0; i < cards.Count; i++)
        {
            try
            {
                object? entry = cards[i].GetType().GetProperty("Entry", ShopInstance)?.GetValue(cards[i]);
                object? creation = entry?.GetType().GetProperty("CreationResult", ShopInstance)?.GetValue(entry);
                object? card = creation?.GetType().GetProperty("Card", ShopInstance)?.GetValue(creation);
                object? type = card?.GetType().GetProperty("Type", ShopInstance)?.GetValue(card);
                if (type?.ToString() == "Power") return i;
            }
            catch (Exception)
            {
                return -1;
            }
        }
        return -1;
    }

    private void PadShopRow(List<Control> slots, float pitch)
    {
        if (slots.Count == 0) return;
        float width = Math.Min(ShopItemPadWidth, Math.Max(0f, pitch - 6f));
        foreach (Control slot in slots)
            if (slot.GetNodeOrNull<Control>("%Hitbox") is { } hitbox)
                EnsureHitPad(hitbox, width, ShopItemPadHeight);
    }

    // ---- Cloth, tabs, pages and the open/close state -------------------------------------------------------------------

    private void EnsureShopCloth(ShopState state)
    {
        if (state.Cloth != null && GodotObject.IsInstanceValid(state.Cloth)) return;
        if (state.Stock is not { } stock || state.Inventory is not { } inventory) return;
        var cloth = new TextureRect
        {
            Name = ShopClothName,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            PivotOffset = Vector2.Zero,
        };
        inventory.AddChild(cloth);
        inventory.MoveChild(cloth, stock.GetIndex());
        // The game slides the stock's own position; the rect's signal fires with the new value in the same frame, so the
        // cloth moves with the stock even while a tween is in flight (a per-frame pass would lag one frame behind).
        stock.ItemRectChanged += () => SyncShopCloth(state);
        stock.VisibilityChanged += () => SyncShopCloth(state);
        state.Cloth = cloth;
        SyncShopCloth(state);
    }

    private static void SyncShopCloth(ShopState state)
    {
        if (state.Cloth is not { } cloth || !GodotObject.IsInstanceValid(cloth)) return;
        if (state.Stock is not { } stock || !GodotObject.IsInstanceValid(stock)) return;
        var inset = new Vector2(PortraitShopMath.ClothInsetSide, PortraitShopMath.ClothInsetTop);
        var size = new Vector2(
            stock.Size.X + 2f * PortraitShopMath.ClothInsetSide,
            stock.Size.Y + PortraitShopMath.ClothInsetTop + PortraitShopMath.ClothInsetBottom);
        Vector2 at = stock.Position - inset;
        if (!cloth.Position.IsEqualApprox(at)) cloth.Position = at;
        if (!cloth.Size.IsEqualApprox(size)) cloth.Size = size;
        if (cloth.Visible != stock.Visible) cloth.Visible = stock.Visible;
    }

    private Texture2D? RotatedShopCloth(Texture2D? texture)
    {
        if (texture == null) return null;
        ulong id = texture.GetInstanceId();
        if (_rotatedCloths.TryGetValue(id, out Texture2D? cached) && GodotObject.IsInstanceValid(cached)) return cached;
        try
        {
            if (texture.GetImage() is not { } image || image.IsEmpty()) return texture;
            if (image.IsCompressed()) image.Decompress();
            if (image.IsCompressed()) return texture;
            image.Rotate90(ClockDirection.Clockwise);
            Texture2D rotated = ImageTexture.CreateFromImage(image.GetRegion(image.GetUsedRect()));
            _rotatedCloths[id] = rotated;
            return rotated;
        }
        catch (Exception)
        {
            // Some texture types have no readable image; the game's own rug then stands in, unrotated.
            return texture;
        }
    }

    private void SetShopBackdrop(ShopState state, Vector2 canvas)
    {
        foreach (Control? control in new[] { state.Backstop, state.InputBlocker })
        {
            if (control == null || !GodotObject.IsInstanceValid(control)) continue;
            Capture(control, captureVisible: false);
            control.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
            control.GlobalPosition = Vector2.Zero;
            control.Size = canvas;
        }
    }

    private void EnsureShopTabs(Control inventory, ShopState state)
    {
        if (!state.Paged || !inventory.IsInsideTree() || !inventory.IsNodeReady()) return;
        if (state.CardsTab == null || !GodotObject.IsInstanceValid(state.CardsTab))
            state.CardsTab = CreateShopTab(inventory, state, ShopCardsTabName, "Cards", 0);
        if (state.ItemsTab == null || !GodotObject.IsInstanceValid(state.ItemsTab))
            state.ItemsTab = CreateShopTab(inventory, state, ShopItemsTabName, "Items", 1);
    }

    private Control? CreateShopTab(Control inventory, ShopState state, string name, string label, int page)
    {
        try
        {
            if (!ResourceLoader.Exists(ShopTabScene)) return null;
            using var scene = GD.Load<PackedScene>(ShopTabScene);
            if (scene?.Instantiate() is not Control tab) return null;
            tab.Name = name;
            inventory.AddChild(tab);
            tab.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
            tab.FocusMode = Control.FocusModeEnum.All;
            tab.MouseFilter = Control.MouseFilterEnum.Stop;
            tab.Call("SetLabel", label);
            if (tab.GetNodeOrNull<Control>("Label") is { } text)
            {
                text.Set("MinFontSize", PortraitShopMath.TabFontSize);
                text.Set("MaxFontSize", PortraitShopMath.TabFontSize);
                text.AddThemeFontSizeOverride("font_size", PortraitShopMath.TabFontSize);
            }
            tab.Connect("MousePressed", Callable.From((InputEvent e) => OnShopTabPressed(state, page, e)));
            tab.Connect("MouseReleased", Callable.From((InputEvent e) => OnShopTabReleased(inventory, state, page, e)));
            return tab;
        }
        catch (Exception error)
        {
            GD.PushError("Portrait between layout: a shop tab could not be made: " + error.Message);
            return null;
        }
    }

    private void OnShopTabPressed(ShopState state, int page, InputEvent e)
    {
        if (e is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } press) return;
        state.TabPressed = true;
        state.TabPressPage = page;
        state.TabPressPosition = press.GlobalPosition;
    }

    private void OnShopTabReleased(Control inventory, ShopState state, int page, InputEvent e)
    {
        if (e is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } release) return;
        bool tapped = state.TabPressed && state.TabPressPage == page &&
                      state.TabPressPosition.DistanceTo(release.GlobalPosition) <= SelectConfirmMath.ConfirmSlop;
        state.TabPressed = false;
        state.TabPressPage = -1;
        if (!tapped || !state.Paged || state.IsOpen?.Invoke() is not true) return;
        if (state.InputBlocker is { } blocker && blocker.MouseFilter == Control.MouseFilterEnum.Stop) return;
        if (state.Page == page) return;
        // A page change lets the selected item go before its stock is hidden under the other page.
        SetShopPage(inventory, state, page, announce: true);
        if (inventory.GetType().GetProperty("MerchantHand", ShopInstance)?.GetValue(inventory) is GodotObject hand)
            hand.Call("StopPointing", 0f);
    }

    private void SetShopPage(Control inventory, ShopState state, int page, bool announce)
    {
        state.Page = page;
        foreach (Control group in state.CardGroups)
            if (GodotObject.IsInstanceValid(group)) group.Visible = page == 0;
        foreach (Control group in state.ItemGroups)
            if (GodotObject.IsInstanceValid(group)) group.Visible = page == 1;
        if (state.CardsTab is { } cards && GodotObject.IsInstanceValid(cards))
        {
            if (page == 0) cards.Call("Select");
            else cards.Call("Deselect");
        }
        if (state.ItemsTab is { } items && GodotObject.IsInstanceValid(items))
        {
            if (page == 1) items.Call("Select");
            else items.Call("Deselect");
        }
        ShopPage = state.Paged ? page : -1;
        if (announce) PhoneSelectConfirm.Clear("shop page");
    }

    /// <summary>
    /// The shop's own Back button as a phone target: at least 44 points tall and fully on the canvas, beside the tab row.
    /// The game animates the button to its <c>_showPos</c>/<c>_hidePos</c>, so those are moved instead of the position; the
    /// running slide is never fought.
    /// </summary>
    private static void PlaceShopBackButton(ShopState state, Vector2 canvas)
    {
        if (!state.BackCaptured || state.BackButton is not { } back || !GodotObject.IsInstanceValid(back)) return;
        var size = new Vector2(
            Math.Max(state.BackSizeHome.X, PortraitBands.MinTarget),
            Math.Max(state.BackSizeHome.Y, PortraitBands.MinTarget));
        if (!back.Size.IsEqualApprox(size)) back.Size = size;
        // The row lives in the bottom band, on the same line as the tabs; only its x needed moving off the canvas edge.
        float centreY = PortraitShopMath.TabRowCentre(canvas.Y, state.BackSizeHome.Y);
        var show = new Vector2(20f, centreY - size.Y / 2f);
        Vector2 hide = new(
            PortraitShopMath.HideX(show.X, state.BackHideHome.X - state.BackShowHome.X, size.X),
            show.Y + (state.BackHideHome.Y - state.BackShowHome.Y));
        if (state.BackShowPosField?.GetValue(back) is Vector2 currentShow && !currentShow.IsEqualApprox(show))
            state.BackShowPosField.SetValue(back, show);
        if (state.BackHidePosField?.GetValue(back) is Vector2 currentHide && !currentHide.IsEqualApprox(hide))
            state.BackHidePosField.SetValue(back, hide);
        if (state.BackMoveTweenField?.GetValue(back) is Tween move && GodotObject.IsInstanceValid(move) && move.IsRunning())
            return;
        Vector2 want = state.IsOpen?.Invoke() == true ? show : hide;
        if (!back.GlobalPosition.IsEqualApprox(want)) back.GlobalPosition = want;
    }

    private void PlaceShopTabs(Control inventory, ShopState state, Vector2 canvas, bool open)
    {
        if (!state.Paged) return;
        EnsureShopTabs(inventory, state);
        bool closing = state.CloseTween is { } tween && GodotObject.IsInstanceValid(tween) && tween.IsRunning();
        bool shown = open || closing;
        if (state.CardsTab is { } cardsTab && GodotObject.IsInstanceValid(cardsTab) && cardsTab.Visible != shown)
            cardsTab.Visible = shown;
        if (state.ItemsTab is { } itemsTab && GodotObject.IsInstanceValid(itemsTab) && itemsTab.Visible != shown)
            itemsTab.Visible = shown;
        if (!shown) return;
        Rect2 back = ShopBackRect(state, canvas);
        var (cards, items) = PortraitShopMath.TabRow(
            new ShopLayoutRect(back.Position.X, back.Position.Y, back.Size.X, back.Size.Y), canvas.X);
        Vector2 origin = inventory.GlobalPosition;
        PlaceTab(state.CardsTab, cards, origin);
        PlaceTab(state.ItemsTab, items, origin);
    }

    private static void PlaceTab(Control? tab, ShopLayoutRect rect, Vector2 origin)
    {
        if (tab == null || !GodotObject.IsInstanceValid(tab)) return;
        Vector2 size = new(rect.Width, rect.Height);
        tab.PivotOffset = size / 2f;
        if (!tab.Size.IsEqualApprox(size)) tab.Size = size;
        Vector2 at = new Vector2(rect.X, rect.Y) - origin;
        if (!tab.Position.IsEqualApprox(at)) tab.Position = at;
    }

    private static Rect2 ShopBackRect(ShopState state, Vector2 canvas)
    {
        if (state.BackButton is { } back && GodotObject.IsInstanceValid(back))
        {
            Rect2 rect = back.GetGlobalRect();
            if (rect.Size.X > 1f && rect.Size.Y > 1f && rect.Position.Y < canvas.Y - 20f && rect.End.Y > 0f)
                return rect;
            if (state.BackShowPosField?.GetValue(back) is Vector2 show && back.Size.X > 1f && back.Size.Y > 1f)
                return new Rect2(show, back.Size * back.Scale.Abs());
        }
        return new Rect2(20f, canvas.Y - ShopBackFallbackY, 198f, 109f);
    }

    private static void HideShopTabs(ShopState state)
    {
        if (state.CardsTab is { } cardsTab && GodotObject.IsInstanceValid(cardsTab)) cardsTab.Visible = false;
        if (state.ItemsTab is { } itemsTab && GodotObject.IsInstanceValid(itemsTab)) itemsTab.Visible = false;
    }

    private void OnShopOpened(Control inventory, ShopState state)
    {
        state.CloseTween?.Kill();
        state.CloseTween = null;
        state.Stock?.Show();
        if (state.Paged) SetShopPage(inventory, state, 0, announce: false);
        else ShopPage = -1;
    }

    private void OnShopClosed(Control inventory, ShopState state)
    {
        PhoneSelectConfirm.Clear("shop close");
        BeginOwnedShopClose(inventory, state);
    }

    /// <summary>
    /// The game's closed position only clears a desktop-height rug, so from the first close on this shop owns the slide:
    /// the native tween is killed and the stock is carried to <see cref="ShopState.ClosedY"/> over the same time, with the
    /// backstop fading as before. The cloth moves with the stock's rect, so it leaves the screen with it.
    /// </summary>
    private void BeginOwnedShopClose(Control inventory, ShopState state)
    {
        if (state.CloseTween is { } running && GodotObject.IsInstanceValid(running) && running.IsRunning()) return;
        if (state.Stock is not { } stock || !GodotObject.IsInstanceValid(stock)) return;
        if (state.TweenField?.GetValue(inventory) is Tween native && GodotObject.IsInstanceValid(native) && native.IsRunning())
            native.Kill();
        state.ClosedY = PortraitShopMath.ClosedY(inventory.GlobalPosition.Y, state.Cloth?.Size.Y ?? 0f);
        Tween tween = inventory.CreateTween().SetParallel();
        tween.TweenProperty(stock, "position:y", state.ClosedY, ShopClosedSeconds)
            .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic).FromCurrent();
        if (state.Backstop is { } backstop && GodotObject.IsInstanceValid(backstop))
            tween.TweenProperty(backstop, "modulate:a", 0f, ShopBackstopSeconds)
                .SetEase(Tween.EaseType.InOut).SetTrans(Tween.TransitionType.Sine).FromCurrent();
        tween.Finished += () =>
        {
            if (GodotObject.IsInstanceValid(stock))
            {
                stock.Position = new Vector2(stock.Position.X, state.ClosedY);
                // Nothing of the shop is drawn after the slide.
                stock.Hide();
            }
            HideShopTabs(state);
            // The next open starts on the Cards page, chosen while nothing of the shop is on screen.
            if (GodotObject.IsInstanceValid(inventory)) SetShopPage(inventory, state, 0, announce: false);
            ShopPage = -1;
        };
        state.CloseTween = tween;
    }

    /// <summary>While closed, the native tween (if the game made a new one) is replaced, and the stock waits out of sight.</summary>
    private void KeepShopClosed(Control inventory, ShopState state, Vector2 canvas)
    {
        if (state.Stock is not { } stock || !GodotObject.IsInstanceValid(stock)) return;
        if (state.CloseTween is { } own && GodotObject.IsInstanceValid(own) && own.IsRunning()) return;
        if (state.TweenField?.GetValue(inventory) is Tween native && GodotObject.IsInstanceValid(native) && native.IsRunning())
        {
            BeginOwnedShopClose(inventory, state);
            return;
        }
        if (!stock.Position.Y.Equals(state.ClosedY))
            stock.Position = new Vector2(stock.Position.X, state.ClosedY);
        ShopPage = -1;
    }

    private bool NativeShopTweenRunning(ShopState state, Control inventory) =>
        state.TweenField?.GetValue(inventory) is Tween tween && GodotObject.IsInstanceValid(tween) && tween.IsRunning();

    private static void KeepShopDialogueInside(ShopState state, Vector2 canvas)
    {
        if (state.Dialogue is not { } dialogue || !GodotObject.IsInstanceValid(dialogue)) return;
        float x = Math.Clamp(dialogue.Position.X, ShopDialogueLeftReach + PortraitShopMath.ClothSideMargin,
            canvas.X - ShopDialogueRightReach - PortraitShopMath.ClothSideMargin);
        if (Math.Abs(x - dialogue.Position.X) > 0.5f)
            dialogue.Position = new Vector2(x, dialogue.Position.Y);
    }

    /// <summary>The card removal keeps the page's size even after the game's own hover tween tried another.</summary>
    private void KeepRemovalScaled(ShopState state)
    {
        if (state.Removal is not { } removal || !GodotObject.IsInstanceValid(removal) || state.RemovalScale <= 0.01f) return;
        if (state.HoverTweenField?.GetValue(removal) is Tween hover && GodotObject.IsInstanceValid(hover) && hover.IsRunning())
            hover.Kill();
        float scale = state.RemovalScale;
        if (!Mathf.IsEqualApprox(removal.Scale.X, scale) || !Mathf.IsEqualApprox(removal.Scale.Y, scale))
            removal.Scale = new Vector2(scale, scale);
    }

    // ---- Prices --------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A shop price is a gold icon and a 39 unit number in a slot: in portrait the number is set to about 11 points on the
    /// screen whatever the scales, and the icon grows with it.
    /// </summary>
    private void EnlargeShopPrice(Control cost)
    {
        if (cost.GetNodeOrNull<Label>("CostLabel") is not { } label || !label.IsInsideTree()) return;
        float scale = label.GetGlobalTransformWithCanvas().Scale.Y;
        if (scale < 0.01f) return;
        int gameFont = GameLabelFontSize(label);
        int font = (int)MathF.Ceiling(ShopPriceUnits / scale);
        if (font <= gameFont) return;
        SetLabelFontSize(label, font);
        foreach (string limit in new[] { "MinFontSize", "MaxFontSize" })
        {
            Dictionary<ulong, int> kept = limit == "MinFontSize" ? _labelMinFontSizes : _labelMaxFontSizes;
            ulong id = label.GetInstanceId();
            if (label.Get(limit).VariantType != Variant.Type.Int) continue;
            if (!kept.ContainsKey(id)) kept[id] = label.Get(limit).AsInt32();
            label.Set(limit, font);
        }
        foreach (TextureRect icon in cost.GetChildren().OfType<TextureRect>())
        {
            PortraitControlSnapshot snap = Capture(icon, captureVisible: false);
            icon.CustomMinimumSize = snap.InitialCustomMinimumSize * ((float)font / gameFont);
        }
    }

    // ---- Leaving portrait -----------------------------------------------------------------------------------------------

    private void RestoreShopLandscape(Control inventory)
    {
        ulong id = inventory.GetInstanceId();
        if (_shopStates.Remove(id, out ShopState? state) && state.LaidOut)
        {
            if (state.CloseTween is { } tween && GodotObject.IsInstanceValid(tween)) tween.Kill();
            if (state.Stock is { } stock && GodotObject.IsInstanceValid(stock))
            {
                if (state.StockTexture != null)
                {
                    stock.Texture = state.StockTexture;
                    stock.ClipContents = state.StockClip;
                    stock.ExpandMode = state.StockExpand;
                    stock.StretchMode = state.StockStretch;
                }
                if (state.Cloth is { } cloth && GodotObject.IsInstanceValid(cloth))
                {
                    if (cloth.GetParent() is { } parent) parent.RemoveChild(cloth);
                    cloth.QueueFree();
                }
                bool open = state.IsOpen?.Invoke() ?? false;
                if (!NativeShopTweenRunning(state, inventory))
                    stock.Position = new Vector2(stock.Position.X, open ? ShopOpenY : ShopNativeClosedY);
                // The game's own close tween may have been replaced by this pass: put the backdrop where the game expects it.
                if (state.Backstop is { } backstop && GodotObject.IsInstanceValid(backstop))
                {
                    Color colour = backstop.Modulate;
                    backstop.Modulate = new Color(colour.R, colour.G, colour.B, open ? 0.8f : 0f);
                }
            }
            FreeShopTab(state.CardsTab);
            FreeShopTab(state.ItemsTab);
            if (state.Dialogue is { } dialogue && GodotObject.IsInstanceValid(dialogue) && state.DialogueCaptured)
                dialogue.Position = state.DialogueHome;
            if (state.BlitzFooter is RichTextLabel footer && GodotObject.IsInstanceValid(footer) &&
                state.BlitzFooterFitContent is { } fit)
                footer.FitContent = fit;
            if (state.BackCaptured && state.BackButton is { } back && GodotObject.IsInstanceValid(back))
            {
                if (state.BackMoveTweenField?.GetValue(back) is Tween move && GodotObject.IsInstanceValid(move)) move.Kill();
                state.BackShowPosField?.SetValue(back, state.BackShowHome);
                state.BackHidePosField?.SetValue(back, state.BackHideHome);
                bool open = state.IsOpen?.Invoke() ?? false;
                back.GlobalPosition = open ? state.BackShowHome : state.BackHideHome;
            }
        }
        ShopPage = -1;
    }

    private static void FreeShopTab(Control? tab)
    {
        if (tab == null || !GodotObject.IsInstanceValid(tab)) return;
        if (tab.GetParent() is { } parent) parent.RemoveChild(tab);
        tab.QueueFree();
    }

    private void PruneShopInstances()
    {
        foreach (ulong id in _shopStates.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList())
            _shopStates.Remove(id);
        foreach (ulong id in _shopRoomStates.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList())
            _shopRoomStates.Remove(id);
        foreach (ulong id in _rotatedCloths.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList())
            _rotatedCloths.Remove(id);
    }

    // ---- The room behind the shop --------------------------------------------------------------------------------------

    /// <summary>
    /// The merchant room's backdrop, player and merchant composed from the room's authored art: the art scaled to
    /// cover the whole canvas, the player standing beside the rug and the merchant on his authored seat. Applied from
    /// stored baselines, so re-applying after a rotation or an orientation switch lands in the same place.
    /// </summary>
    private void ApplyShopRoom(Control room, string typeName, float canvasWidth, float canvasHeight)
    {
        try
        {
            ulong id = room.GetInstanceId();
            if (!_shopRoomStates.TryGetValue(id, out ShopRoomState? state) || state.Scene == null ||
                !GodotObject.IsInstanceValid(state.Scene))
            {
                state = CreateShopRoomState(room, typeName);
                if (state == null) return;
                _shopRoomStates[id] = state;
            }
            if (state.Failed) return;
            var canvas = new Vector2(canvasWidth, canvasHeight);
            if (state.Applied && state.Canvas == canvas && (!state.Blitz || state.BlitzMerchant != null)) return;
            state.Canvas = canvas;
            if (state.Blitz) ApplyBlitzShopRoom(state, canvas);
            else ApplyStandardShopRoom(state, canvas);
            state.Applied = true;
        }
        catch (Exception error)
        {
            if (_shopRoomStates.TryGetValue(room.GetInstanceId(), out ShopRoomState? failed)) failed.Failed = true;
            GD.PushError("Portrait between layout: the shop room could not be framed: " + error.Message);
        }
    }

    private ShopRoomState? CreateShopRoomState(Control room, string typeName)
    {
        if (room.GetNodeOrNull<Control>("SceneContainer") is not { } scene) return null;
        var state = new ShopRoomState
        {
            Id = room.GetInstanceId(),
            TypeName = typeName,
            Room = room,
            Scene = scene,
            Blitz = typeName == BlitzMerchantRoomTypeName,
        };
        if (typeName == BlitzMerchantRoomTypeName)
        {
            if (scene.GetNodeOrNull<Control>("BgContainer") is not { } bg) return null;
            state.Bg = bg;
            state.BlitzMerchant = room.GetNodeOrNull<Control>("%MerchantButton") ??
                                  room.GetNodeOrNull<Control>("MerchantButton") ??
                                  scene.GetNodeOrNull<Control>("%MerchantButton");
            if (state.BlitzMerchant != null) Capture(state.BlitzMerchant, captureVisible: false);
            Capture(scene, captureVisible: false);
            Capture(bg, captureVisible: false);
            return state;
        }
        if (scene.GetNodeOrNull<Control>("BgContainer") is not { } art ||
            scene.GetNodeOrNull<Control>("CharacterContainer") is not { } party ||
            scene.GetNodeOrNull<Control>("MerchantButton") is not { } merchant)
            return null;
        state.Bg = art;
        state.Party = party;
        state.Merchant = merchant;
        state.MerchantHome = merchant.GlobalPosition;
        foreach (Node child in scene.GetChildren())
        {
            if (child is not Node2D layer) continue;
            string name = layer.Name.ToString();
            if (name is not ("SpineSprite2" or "stars" or "light_small5" or "light_small6")) continue;
            state.Layers.Add(new ShopRoomLayer { Node = layer, HomePosition = layer.Position, HomeScale = layer.Scale });
        }
        Capture(art, captureVisible: false);
        Capture(party, captureVisible: false);
        Capture(merchant, captureVisible: false);
        return state;
    }

    private static void ApplyStandardShopRoom(ShopRoomState state, Vector2 canvas)
    {
        if (state.Bg is not { } bg || state.Party is not { } party || state.Merchant is not { } merchant) return;
        bool weird = state.TypeName == FakeMerchantRoomTypeName;
        Rect2 art = weird ? new Rect2(-575.64f, -179.23f, 2433f, 1440f) : new Rect2(-320f, -60f, 2560f, 1200f);
        float cover = MathF.Max(canvas.X / art.Size.X, canvas.Y / art.Size.Y) * ShopCoverScale;
        Vector2 merchantContact = weird ? new Vector2(147f, 444f) : new Vector2(130f, 306f);
        Vector2 nativeButton = weird ? new Vector2(174f, -195f) : new Vector2(246f, -72f);
        Vector2 merchantFloor = (new Vector2(960f, 540f) + nativeButton + merchantContact - ShopArtOrigin) / ShopArtScale;
        Vector2 playerFloor = weird ? new Vector2(1041f, 870f) : new Vector2(1155f, 888f);

        bg.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        bg.PivotOffset = Vector2.Zero;
        bg.Size = ShopAuthoredSize;
        bg.Scale = Vector2.One * cover;
        float centerX = Math.Clamp(merchantFloor.X - canvas.X * 0.20f / cover,
            art.Position.X + canvas.X * 0.5f / cover, art.End.X - canvas.X * 0.5f / cover);
        bg.GlobalPosition = canvas * 0.5f - new Vector2(centerX, art.GetCenter().Y) * cover;
        Transform2D artToWorld = bg.GetGlobalTransform();

        foreach (ShopRoomLayer layer in state.Layers)
        {
            if (!GodotObject.IsInstanceValid(layer.Node)) continue;
            layer.Node.GlobalPosition = artToWorld * ((layer.HomePosition - ShopArtOrigin) / ShopArtScale);
            layer.Node.Scale = layer.HomeScale * bg.Scale / ShopArtScale;
        }

        party.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        party.PivotOffset = Vector2.Zero;
        party.Scale = Vector2.One * cover * (weird ? 1.06f : 0.80f);
        party.GlobalPosition = artToWorld * playerFloor;

        merchant.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        merchant.GlobalPosition = state.MerchantHome;
        merchant.Scale = Vector2.One * cover / ShopArtScale * (weird ? 0.62f : 0.71f);
        merchant.GlobalPosition += artToWorld * merchantFloor - merchant.GetGlobalTransform() * merchantContact;
    }

    /// <summary>
    /// The Blitz room: the whole authored scene carried as one coordinate system, with the full-bleed background art under
    /// it and a fade so its crop does not cut hard against the underlay.
    /// </summary>
    private static void ApplyBlitzShopRoom(ShopRoomState state, Vector2 canvas)
    {
        if (state.Scene is not { } scene || state.Bg is not { } bg) return;
        if (state.BlitzMerchant == null && state.Room is { } room)
            state.BlitzMerchant = room.GetNodeOrNull<Control>("%MerchantButton") ??
                                  room.GetNodeOrNull<Control>("MerchantButton") ??
                                  scene.GetNodeOrNull<Control>("%MerchantButton");
        scene.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        scene.Size = new Vector2(1920f, 1080f);
        scene.PivotOffset = Vector2.Zero;
        float scale = (canvas.X - 57.86f) / 732.86f;
        float floor = canvas.Y * 0.60f - 99.64f;
        scene.Scale = Vector2.One * scale;
        scene.GlobalPosition = new Vector2(canvas.X / 2f, floor) - new Vector2(1000f, 766f) * scale;

        if (state.Underlay == null || !GodotObject.IsInstanceValid(state.Underlay))
        {
            Texture2D? texture = null;
            if (bg.FindChild("Layer_00", recursive: true, owned: false) is { } layer)
            {
                var rect = layer as TextureRect ?? layer.FindChildren("*", "TextureRect", true, false).OfType<TextureRect>()
                    .FirstOrDefault(candidate => candidate.Texture is not null && candidate.Size.X > 1000f);
                texture = rect?.Texture;
            }
            if (texture == null) return;
            var underlay = new TextureRect
            {
                Name = ShopBlitzUnderlayName,
                Texture = texture,
                ZIndex = -1,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                Position = Vector2.Zero,
                Size = canvas,
            };
            scene.GetParent().AddChild(underlay);
            scene.GetParent().MoveChild(underlay, 0);
            state.Underlay = underlay;
        }
        else
        {
            state.Underlay.Size = canvas;
        }
        // The authored scene is wider than the canvas; slide it left when its merchant button (built after a room is entered)
        // runs off the right edge, so the shop's own target is reachable.
        if (state.BlitzMerchant is { } merchant && GodotObject.IsInstanceValid(merchant))
        {
            Rect2 box = merchant.GetGlobalRect();
            const float margin = 24f;
            if (box.End.X > canvas.X - margin)
                scene.GlobalPosition -= new Vector2(box.End.X - (canvas.X - margin), 0f);
            else if (box.Position.X < margin)
                scene.GlobalPosition += new Vector2(margin - box.Position.X, 0f);
        }
        EnsureShopBlitzFade(state, canvas, floor);
    }

    private static void EnsureShopBlitzFade(ShopRoomState state, Vector2 canvas, float floor)
    {
        if (state.Underlay == null || state.Scene?.GetParent() is not { } room) return;
        if (state.Fade == null || !GodotObject.IsInstanceValid(state.Fade))
        {
            _shopBlitzFadeShader ??= new Shader
            {
                Code = """
                    shader_type canvas_item;
                    uniform float lower_start;
                    uniform float lower_end;
                    uniform bool blend_top;
                    uniform float upper_start;
                    uniform float upper_end;
                    varying float local_y;
                    void vertex() { local_y = VERTEX.y; }
                    void fragment() {
                        float lower = smoothstep(lower_start, lower_end, local_y);
                        float upper = blend_top ? 1.0 - smoothstep(upper_start, upper_end, local_y) : 0.0;
                        COLOR.a *= max(lower, upper);
                    }
                    """,
            };
            var fade = new TextureRect
            {
                Name = ShopBlitzFadeName,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                Material = new ShaderMaterial { Shader = _shopBlitzFadeShader },
            };
            room.AddChild(fade);
            room.MoveChild(fade, state.Underlay.GetIndex() + 1);
            state.Fade = fade;
        }
        TextureRect underlay = state.Underlay;
        state.Fade.Texture = underlay.Texture;
        state.Fade.StretchMode = underlay.StretchMode;
        state.Fade.Modulate = underlay.Modulate;
        state.Fade.Size = canvas;
        state.Fade.GlobalPosition = Vector2.Zero;
        var material = (ShaderMaterial)state.Fade.Material;
        float lowerStart = floor + 392.14f;
        float lowerEnd = floor + 617.14f;
        float upperStart = floor - 848.57f;
        float upperEnd = floor - 591.43f;
        material.SetShaderParameter("lower_start", lowerStart);
        material.SetShaderParameter("lower_end", lowerEnd);
        material.SetShaderParameter("blend_top", true);
        material.SetShaderParameter("upper_start", upperStart);
        material.SetShaderParameter("upper_end", upperEnd);
    }

    private void RestoreShopRoomsLandscape()
    {
        foreach (ShopRoomState state in _shopRoomStates.Values)
        {
            foreach (ShopRoomLayer layer in state.Layers)
                if (GodotObject.IsInstanceValid(layer.Node))
                {
                    layer.Node.Position = layer.HomePosition;
                    layer.Node.Scale = layer.HomeScale;
                }
            foreach (TextureRect? made in new[] { state.Underlay, state.Fade })
            {
                if (made == null || !GodotObject.IsInstanceValid(made)) continue;
                made.GetParent()?.RemoveChild(made);
                made.QueueFree();
            }
        }
        _shopRoomStates.Clear();
    }
}
