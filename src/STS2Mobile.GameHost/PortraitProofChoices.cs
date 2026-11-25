using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Nodes;

namespace STS2Mobile.GameHost;

/// <summary>
/// <c>touch-choices</c> (no single tap takes a card or relic, the shop needs a second tap), with real touch events: in a fight,
/// choose a card (where the peek button sits against the first card; card 1 tapped, card 3 tapped, card 3 tapped again to take
/// it), a combat pile grid that takes one card (a tap, a tap on another, the game's confirm), and a relic choice; then the shop
/// (a tap selects, a tap elsewhere lets go, a hold inspects without buying, a swipe buys nothing, a second tap buys), the
/// treasure chest's relic (a tap, then a second tap), and the Spiraling Whirlpool event's last page (where text was cut off).
/// Each step logs whether anything was taken. Portrait only: landscape is the game's own (<c>stock-landscape</c>). It starts a
/// run, so it is for the simulator only.
/// </summary>
internal static partial class PortraitProof
{
    private static async Task RunTouchChoicesAsync(SceneTree tree, NGame game)
    {
        ClearNextStepFile();
        await EnsurePortraitAsync(tree, game);
        object player = await StartProofRunAsync(tree, game, characterIndex: 0, fivePotions: false, thirtyRelics: false, addThreePotions: false, closeRelicHover: true);
        await WaitFramesAsync(tree, 90);
        Call(StaticProp(GameType(ScreensNs + "Map.NMapScreen"), "Instance")!, "Close", false);
        DismissFtues(tree, Sts2);
        await WaitFramesAsync(tree, 20);
        await EnterEncounterAsync(Sts2, tree, player, "NIBBITS_NORMAL", 42UL);
        await WaitFramesAsync(tree, 60);
        var results = new List<string>();
        string cs = ScreensNs + "CardSelection.";
        object overlays = StaticProp(GameType(ScreensNs + "Overlays.NOverlayStack"), "Instance")!;
        IList deck = DeckCards(player);

        // Choose a card, in a fight (the peek button shows): a tap selects, a tap on another moves it, a second tap takes it.
        object chooseScreen = CallStatic(GameType(cs + "NChooseACardSelectionScreen"), "ShowScreen",
            TypedList("MegaCrit.Sts2.Core.Models.CardModel", deck.Cast<object>().Skip(4).Take(3)), true)!;
        Task chooseTask = (Task)Call(chooseScreen, "CardsSelected")!;
        await WaitFramesAsync(tree, 70);
        var screen = (Control)chooseScreen;
        List<Control> cards = RowItems(screen, "CardRow");
        await TouchStepAsync(tree, "choice-card-open", $"{DescribeChoice(screen, cards, chooseTask)} {DescribePeek(screen, cards)}", results);
        if (cards.Count > 2)
        {
            await TapAsync(tree, CentreOf(cards[0]));
            await WaitFramesAsync(tree, 30);
            await TouchStepAsync(tree, "choice-card-first", DescribeChoice(screen, cards, chooseTask), results);
            await TapAsync(tree, CentreOf(cards[2]));
            await WaitFramesAsync(tree, 30);
            await TouchStepAsync(tree, "choice-card-third", DescribeChoice(screen, cards, chooseTask), results);
            string expectedCard = CardName(cards[2]);
            await TapAgainAsync(tree, CentreOf(cards[2]));
            await WaitFramesAsync(tree, 60);
            await TouchStepAsync(tree, "choice-card-chosen", $"expected={expectedCard} {DescribeTask(chooseTask)} open={GodotObject.IsInstanceValid(screen) && screen.IsInsideTree()}", results);
        }
        Call(overlays, "Clear");
        await WaitFramesAsync(tree, 20);

        // A combat pile grid that takes one card: the game would close on the first tap; now it waits for its confirm button.
        Type prefsType = GameType("MegaCrit.Sts2.Core.CardSelection.CardSelectorPrefs", "CardSelectorPrefs");
        object prefs = Activator.CreateInstance(prefsType, StaticProp(prefsType, "DiscardSelectionPrompt"), 1)!;
        Type pileTypeEnum = GameType("MegaCrit.Sts2.Core.Entities.Cards.PileType");
        Type pileExt = GameType("MegaCrit.Sts2.Core.Entities.Cards.PileTypeExtensions", "PileTypeExtensions");
        object pile = CallWith(null, pileExt, "GetPile", [Enum.Parse(pileTypeEnum, "Draw"), player])!;
        var grid = (Control)CallWith(null, GameType(cs + "NCombatPileCardSelectScreen"), "Create", [pile, prefs, null])!;
        Call(overlays, "Push", grid);
        Task gridTask = (Task)Call(grid, "CardsSelected")!;
        await WaitFramesAsync(tree, 60);
        List<Control> gridCards = GridHolders(grid);
        await TouchStepAsync(tree, "choice-grid-open", $"{DescribeGrid(grid, gridTask)} {DescribePeek(grid, gridCards)}", results);
        // The second tap goes to a card of another kind, so the log shows the selection move.
        Control? otherCard = gridCards.Skip(1).FirstOrDefault(c => gridCards.Count > 0 && CardName(c) != CardName(gridCards[0]));
        if (gridCards.Count > 1 && otherCard != null)
        {
            string expected = CardName(otherCard);
            await TapAsync(tree, CentreOf(gridCards[0]));
            await WaitFramesAsync(tree, 30);
            await TouchStepAsync(tree, "choice-grid-first", $"tapped={CardName(gridCards[0])} {DescribeGrid(grid, gridTask)}", results);
            await TapAsync(tree, CentreOf(otherCard));
            await WaitFramesAsync(tree, 30);
            await TouchStepAsync(tree, "choice-grid-second", $"tapped={expected} {DescribeGrid(grid, gridTask)}", results);
            if (ConfirmOf(grid) is { } confirm) await TapAsync(tree, CentreOf(confirm));
            await WaitFramesAsync(tree, 60);
            await TouchStepAsync(tree, "choice-grid-confirmed", $"expected={expected} {DescribeTask(gridTask)}", results);
        }
        Call(overlays, "Clear");
        await WaitFramesAsync(tree, 20);

        // A relic choice: a tap selects, a tap on another moves it, a second tap takes it.
        var relicScreen = (Control)CallStatic(GameType(ScreensNs + "NChooseARelicSelection"), "ShowScreen",
            TypedList("MegaCrit.Sts2.Core.Models.RelicModel", ModelDbList("AllRelics").Skip(10).Take(3).Select(r => Call(r, "ToMutable")!)))!;
        Task relicTask = (Task)Call(relicScreen, "RelicsSelected")!;
        await WaitFramesAsync(tree, 70);
        List<Control> relics = RowItems(relicScreen, "RelicRow");
        await TouchStepAsync(tree, "choice-relic-open", DescribeChoice(relicScreen, relics, relicTask), results);
        if (relics.Count > 2)
        {
            await TapAsync(tree, CentreOf(relics[0]));
            await WaitFramesAsync(tree, 30);
            await TouchStepAsync(tree, "choice-relic-first", DescribeChoice(relicScreen, relics, relicTask), results);
            await TapAsync(tree, CentreOf(relics[2]));
            await WaitFramesAsync(tree, 30);
            await TouchStepAsync(tree, "choice-relic-third", DescribeChoice(relicScreen, relics, relicTask), results);
            string expected = RelicName(relics[2]);
            await TapAgainAsync(tree, CentreOf(relics[2]));
            await WaitFramesAsync(tree, 60);
            await TouchStepAsync(tree, "choice-relic-chosen", $"expected={expected} {DescribeTask(relicTask)}", results);
        }
        Call(overlays, "Clear");
        await WaitFramesAsync(tree, 20);
        await RunConsoleCmdAsync(Sts2, "WinConsoleCmd", player, []);
        await WaitFramesAsync(tree, 120);
        Call(overlays, "Clear");
        await WaitFramesAsync(tree, 20);

        // The shop: a tap selects, a tap elsewhere lets go, a second tap on the same item buys it. A hold opens the item's
        // inspection without buying, and a swipe buys nothing.
        player.GetType().GetProperty("Gold")!.SetValue(player, 999);
        await RunConsoleCmdAsync(Sts2, "RoomConsoleCmd", player, ["Shop"]);
        await WaitFramesAsync(tree, 120);
        DismissFtues(tree, Sts2);
        object? merchant = StaticProp(GameType("MegaCrit.Sts2.Core.Nodes.Rooms.NMerchantRoom"), "Instance");
        if (merchant != null) Call(merchant, "OpenInventory");
        await WaitFramesAsync(tree, 70);
        DismissFtues(tree, Sts2);
        await WaitFramesAsync(tree, 20);
        // The shop's stock has two pages (cards on one, relics and potions on the other). Switch them with real taps on the
        // native tabs, and report the page each step sees.
        List<Control> slots = ShopSlots(tree);
        await TouchStepAsync(tree, "choice-shop-open", DescribeShop(player, slots), results);
        await TapShopPageAsync(tree, items: true);
        slots = ShopSlots(tree);
        await TouchStepAsync(tree, "choice-shop-items-page", DescribeShop(player, slots), results);
        await TapShopPageAsync(tree, items: false);
        slots = ShopSlots(tree);
        await TouchStepAsync(tree, "choice-shop-cards-page", DescribeShop(player, slots), results);

        // A card: a tap selects it, on the page that shows cards.
        Control? cardSlot = slots.FirstOrDefault(s => s.GetType().Name == "NMerchantCard");
        if (cardSlot != null)
        {
            await TapAsync(tree, CentreOf(cardSlot));
            await WaitFramesAsync(tree, 30);
            await TouchStepAsync(tree, "choice-shop-card-tap", DescribeShop(player, slots), results);
        }

        // Relics and potions: the other page, where they are shown.
        await TapShopPageAsync(tree, items: true);
        slots = ShopSlots(tree);
        await TouchStepAsync(tree, "choice-shop-items-page-for-relics", DescribeShop(player, slots), results);
        Control? relicSlot = slots.FirstOrDefault(s => s.GetType().Name == "NMerchantRelic");
        Control? potionSlot = slots.FirstOrDefault(s => s.GetType().Name == "NMerchantPotion");
        if (relicSlot != null)
        {
            await TapAsync(tree, CentreOf(relicSlot));
            await WaitFramesAsync(tree, 30);
            await TouchStepAsync(tree, "choice-shop-relic-tap", DescribeShop(player, slots), results);
            // Held still: the relic's own inspection opens, and nothing is bought. The inspect screen is closed as a player does.
            object gold = Prop(player, "Gold")!;
            await HoldAsync(tree, CentreOf(relicSlot));
            await WaitFramesAsync(tree, 30);
            await TouchStepAsync(tree, "choice-shop-relic-hold",
                $"gold={gold}->{Prop(player, "Gold")} inspect={(InspectRelicOpen(game) ? "open" : "closed")} {DescribeShop(player, slots)}", results);
            if (InspectRelicOpen(game)) Call(Call(game, "GetInspectRelicScreen")!, "Close");
            await WaitFramesAsync(tree, 30);
            // Dragged well past the slop: a swipe buys nothing.
            Vector2 from = CentreOf(relicSlot);
            await SwipeAsync(tree, from, from + new Vector2(0f, -220f), 5);
            await WaitFramesAsync(tree, 30);
            await TouchStepAsync(tree, "choice-shop-relic-swipe", $"gold={gold}->{Prop(player, "Gold")} {DescribeShop(player, slots)}", results);
            // A tap elsewhere lets the selection go; the next tap selects again and only the one after buys.
            await TapAsync(tree, EmptySpot(tree));
            await WaitFramesAsync(tree, 30);
            await TouchStepAsync(tree, "choice-shop-relic-let-go", $"gold={gold}->{Prop(player, "Gold")} {DescribeShop(player, slots)}", results);
            await TapAsync(tree, CentreOf(relicSlot));
            await WaitFramesAsync(tree, 30);
            await TouchStepAsync(tree, "choice-shop-relic-tapped-again", DescribeShop(player, slots), results);
            await TapAgainAsync(tree, CentreOf(relicSlot));
            await WaitFramesAsync(tree, 60);
            await TouchStepAsync(tree, "choice-shop-relic-bought", $"gold={gold}->{Prop(player, "Gold")} {DescribeShop(player, slots)}", results);
        }
        if (potionSlot != null)
        {
            await TapAsync(tree, CentreOf(potionSlot));
            await WaitFramesAsync(tree, 30);
            await TapAsync(tree, EmptySpot(tree));
            await WaitFramesAsync(tree, 30);
            await TouchStepAsync(tree, "choice-shop-potion-let-go", DescribeShop(player, slots), results);
            await TapAsync(tree, CentreOf(potionSlot));
            await WaitFramesAsync(tree, 30);
            await TouchStepAsync(tree, "choice-shop-potion-tap", DescribeShop(player, slots), results);
        }
        Call(overlays, "Clear");
        await WaitFramesAsync(tree, 20);

        // The treasure chest: a tap on the relic selects it, a second tap takes it.
        await RunConsoleCmdAsync(Sts2, "RoomConsoleCmd", player, ["Treasure"]);
        await WaitFramesAsync(tree, 120);
        Node? room = Descendants(tree.Root).FirstOrDefault(n => n.GetType().Name == "NTreasureRoom");
        if (room != null) CallWith(room, null, "OnChestButtonReleased", [null]);
        await WaitFramesAsync(tree, 120);
        DismissFtues(tree, Sts2);
        await WaitFramesAsync(tree, 40);
        Control? collection = Descendants(tree.Root).OfType<Control>().FirstOrDefault(c => c.GetType().Name == "NTreasureRoomRelicCollection");
        List<Control> chestRelics = collection == null ? [] : ((IEnumerable)Prop(collection, "HoldersInUse")!).OfType<Control>().ToList();
        int relicsBefore = RelicCount(player);
        await TouchStepAsync(tree, "choice-treasure-open", DescribeTreasure(collection, chestRelics, player, relicsBefore), results);
        if (chestRelics.Count > 0 && collection != null)
        {
            await TapAsync(tree, CentreOf(chestRelics[0]));
            await WaitFramesAsync(tree, 30);
            await TouchStepAsync(tree, "choice-treasure-tap", DescribeTreasure(collection, chestRelics, player, relicsBefore), results);
            await TapAgainAsync(tree, CentreOf(chestRelics[0]));
            await WaitFramesAsync(tree, 90);
            await TouchStepAsync(tree, "choice-treasure-chosen", DescribeTreasure(collection, chestRelics, player, relicsBefore), results);
        }

        // Spiraling Whirlpool: Observe, enchant a Strike, and the page whose text was cut off.
        await RunConsoleCmdAsync(Sts2, "EventConsoleCmd", player, ["SPIRALING_WHIRLPOOL"]);
        await WaitFramesAsync(tree, 150);
        await TouchStepAsync(tree, "event-whirlpool-initial", DescribeEventText(tree), results);
        if (EventOption(tree, "Observe") is { } observe)
        {
            await TapAsync(tree, CentreOf(observe));
            await WaitFramesAsync(tree, 90);
            Control? enchant = Descendants(tree.Root).OfType<Control>().FirstOrDefault(c => c.GetType().Name == "NDeckEnchantSelectScreen" && c.IsVisibleInTree());
            if (enchant != null && GridHolders(enchant).FirstOrDefault() is { } first)
            {
                CallWith(enchant, null, "OnCardClicked", [Prop(first, "CardModel")]);
                await WaitFramesAsync(tree, 40);
                Control? confirm = Descendants(enchant).OfType<Control>().FirstOrDefault(c => c.GetType().Name == "NConfirmButton" && c.IsVisibleInTree() &&
                    c.GetType().GetProperty("IsEnabled")?.GetValue(c) is true);
                confirm?.Call("ForceClick");
                await WaitFramesAsync(tree, 150);
            }
            await TouchStepAsync(tree, "event-whirlpool-observed", $"enchant-screen={(enchant != null)} {DescribeEventText(tree)}", results);
        }

        GD.Print("Touch choices results: " + string.Join("; ", results));
    }

    private static List<Control> RowItems(Control screen, string row) =>
        screen.GetNodeOrNull<Control>(row)?.GetChildren().OfType<Control>().Where(c => !c.IsQueuedForDeletion()).OrderBy(c => c.Position.X).ToList() ?? [];

    private static List<Control> GridHolders(Control screen) =>
        Descendants(screen).OfType<Control>().Where(c => c.GetType().Name == "NGridCardHolder" && c.IsVisibleInTree())
            .OrderBy(c => c.GetGlobalRect().Position.Y).ThenBy(c => c.GetGlobalRect().Position.X).ToList();

    /// <summary>A second tap on the selected item: press, two frames, release, so it stays well under the hold time.</summary>
    private static async Task TapAgainAsync(SceneTree tree, Vector2 canvasPos)
    {
        SendTouch(tree, canvasPos, pressed: true);
        await WaitFramesAsync(tree, 2);
        SendTouch(tree, canvasPos, pressed: false);
    }

    /// <summary>A press held still past the hold time, then released: it must inspect the item and never confirm it.</summary>
    private static async Task HoldAsync(SceneTree tree, Vector2 canvasPos)
    {
        SendTouch(tree, canvasPos, pressed: true);
        await WaitMsecAsync(tree, (ulong)(STS2Mobile.Platform.SelectConfirmMath.HoldSeconds * 1000) + 150);
        SendTouch(tree, canvasPos, pressed: false);
    }

    /// <summary>A swipe that starts on an item and moves well past the confirm slop: it must buy or take nothing.</summary>
    private static async Task SwipeAsync(SceneTree tree, Vector2 from, Vector2 to, int steps)
    {
        SendTouch(tree, from, pressed: true);
        Vector2 previous = from;
        for (int step = 1; step <= steps; step++)
        {
            Vector2 point = from.Lerp(to, (float)step / steps);
            SendTouchDrag(tree, point, previous);
            previous = point;
            await WaitFramesAsync(tree, 2);
        }
        SendTouch(tree, to, pressed: false);
    }

    private static bool InspectRelicOpen(NGame game) => Call(game, "GetInspectRelicScreen") is CanvasItem screen && screen.Visible;

    /// <summary>The left edge of the screen, clear of the shop's cloth, slots and tabs: a tap there lets a selection go.</summary>
    private static Vector2 EmptySpot(SceneTree tree) => new(12f, tree.Root.GetVisibleRect().Size.Y / 2f);

    /// <summary>
    /// Switches the portrait shop's Cards/Items page with a real tap on its native tab, waiting for
    /// <see cref="PortraitShopLayout.ShopPage" /> to follow; true when the page asked for is shown (false when the shop has
    /// no pages, as the weird and Blitz shops do not).
    /// </summary>
    private static async Task<bool> TapShopPageAsync(SceneTree tree, bool items)
    {
        string name = items ? PortraitShopLayout.ShopItemsTabName : PortraitShopLayout.ShopCardsTabName;
        Control? tab = Descendants(tree.Root).OfType<Control>().FirstOrDefault(c => c.Name == name && c.IsVisibleInTree());
        if (tab == null) return false;
        int page = items ? 1 : 0;
        await TapAsync(tree, CentreOf(tab));
        for (int frame = 0; frame < 90 && PortraitShopLayout.ShopPage != page; frame++) await WaitFramesAsync(tree, 1);
        await WaitFramesAsync(tree, 20);
        return PortraitShopLayout.ShopPage == page;
    }

    private static Control? ConfirmOf(Control screen) =>
        Descendants(screen).OfType<Control>().FirstOrDefault(c => c.GetType().Name == "NConfirmButton" && c.IsVisibleInTree());

    private static string RelicName(Control holder) =>
        Prop(Prop(holder, "Relic") ?? holder, "Model")?.GetType().Name ?? holder.Name;

    private static string CardName(Control holder) => holder.GetType().GetProperty("CardModel")?.GetValue(holder)?.GetType().Name ?? holder.Name;

    private static string DescribeTask(Task task)
    {
        if (!task.IsCompleted) return "taken=none";
        if (!task.IsCompletedSuccessfully) return $"taken=cancelled";
        object? result = task.GetType().GetProperty("Result")?.GetValue(task);
        string names = result is IEnumerable items ? string.Join(",", items.Cast<object>().Select(i => i.GetType().Name)) : "?";
        return $"taken=[{names}]";
    }

    private static string DescribeChoice(Control screen, List<Control> items, Task task)
    {
        int reticle = items.FindIndex(i => i.GetNodeOrNull<Node>(PhoneSelectConfirm.ReticleName) is { } o && !o.IsQueuedForDeletion());
        string skip = screen.GetNodeOrNull<Control>("SkipButton") is { } s ? $"{(s.Visible ? "" : "hidden ")}{CanvasRect(s)}" : "none";
        return $"items={items.Count} reticle={reticle} selected={PhoneSelectConfirm.SelectedName ?? "none"} " +
            $"confirms={PhoneSelectConfirm.Confirms} inspects={PhoneSelectConfirm.HoldInspects} clears={PhoneSelectConfirm.Clears} {DescribeTask(task)} skip={skip}";
    }

    private static string DescribePeek(Control screen, List<Control> items)
    {
        if (Descendants(screen).OfType<Control>().FirstOrDefault(c => c.GetType().Name == "NPeekButton") is not { } peek) return "peek=none";
        Rect2 peekRect = CanvasRectOf(peek);
        int covered = items.Count(i => CanvasRectOf(i.GetNodeOrNull<Control>("%Hitbox") ?? i).Intersects(peekRect));
        return $"peek={(peek.IsVisibleInTree() ? "shown" : "hidden")} {CanvasRect(peek)} covers-cards={covered}";
    }

    private static string DescribeGrid(Control grid, Task task)
    {
        int selected = grid.GetType().GetField("_selectedCards", AnyInstance)?.GetValue(grid) is IEnumerable s ? s.Cast<object>().Count() : -1;
        string selectedNames = grid.GetType().GetField("_selectedCards", AnyInstance)?.GetValue(grid) is IEnumerable n
            ? string.Join(",", n.Cast<object>().Select(c => c.GetType().Name)) : "";
        Control? confirm = ConfirmOf(grid);
        string confirmText = confirm == null ? "none" : $"enabled={confirm.GetType().GetProperty("IsEnabled")?.GetValue(confirm)} {CanvasRect(confirm)}";
        return $"selected={selected}[{selectedNames}] {DescribeTask(task)} confirm[{confirmText}]";
    }

    private static List<Control> ShopSlots(SceneTree tree) =>
        Descendants(tree.Root).OfType<Control>().Where(c => IsMerchantSlot(c.GetType()) && c.IsVisibleInTree()).ToList();

    private static bool IsMerchantSlot(Type type)
    {
        for (Type? t = type; t != null; t = t.BaseType)
            if (t.Name == "NMerchantSlot") return true;
        return false;
    }

    private static int RelicCount(object player) => Prop(player, "Relics") is ICollection c ? c.Count : -1;

    private static string DescribeShop(object player, List<Control> slots)
    {
        Control? selected = PhoneSelectConfirm.SelectedOwner;
        string kinds = string.Join(",", slots.GroupBy(s => s.GetType().Name).Select(g => $"{g.Key}x{g.Count()}"));
        string page = PortraitShopLayout.ShopPage switch { 0 => "cards", 1 => "items", _ => "none" };
        return $"page={page} slots=[{kinds}] selected={selected?.GetType().Name ?? "none"} gold={Prop(player, "Gold")} deck={DeckCards(player).Count} " +
            $"relics={RelicCount(player)} confirms={PhoneSelectConfirm.Confirms} inspects={PhoneSelectConfirm.HoldInspects} clears={PhoneSelectConfirm.Clears}";
    }

    private static string DescribeTreasure(Control? collection, List<Control> holders, object player, int relicsBefore)
    {
        if (collection == null) return "collection=none";
        int reticle = holders.FindIndex(i => i.GetNodeOrNull<Node>(PhoneSelectConfirm.ReticleName) is { } o && !o.IsQueuedForDeletion());
        return $"relics-on-offer={holders.Count} reticle={reticle} selected={PhoneSelectConfirm.SelectedName ?? "none"} " +
            $"relics={relicsBefore}->{RelicCount(player)} confirms={PhoneSelectConfirm.Confirms} clears={PhoneSelectConfirm.Clears}";
    }

    private static Control? EventOption(SceneTree tree, string text) =>
        Descendants(tree.Root).OfType<Control>().FirstOrDefault(c => c.GetType().Name.Contains("EventOptionButton", StringComparison.Ordinal) &&
            c.IsVisibleInTree() && Descendants(c).Any(d => (d as RichTextLabel)?.Text.Contains(text, StringComparison.Ordinal) == true ||
                (d as Label)?.Text.Contains(text, StringComparison.Ordinal) == true));

    /// <summary>The event's art, title, description and options on the canvas, and whether the description's text fits its box.</summary>
    private static string DescribeEventText(SceneTree tree)
    {
        Control? layout = Descendants(tree.Root).OfType<Control>().FirstOrDefault(c => c.GetType().Name == "NEventLayout" && c.IsVisibleInTree());
        if (layout == null) return "event=none";
        string Rect(string path) => layout.GetNodeOrNull<Control>(path) is { } c ? CanvasRect(c) : "none";
        // The portrait composition moves the title and the copy into its slot and the options into their scroll; before it
        // runs (or for an event without a picture) they are still where the game put them.
        string slot = PortraitBetweenLayout.EventTextSlotName;
        string copy = slot + "/Panel/Text/Copy/";
        bool composed = layout.GetNodeOrNull<Control>(slot) != null;
        string title = Rect(layout.GetNodeOrNull<Control>(copy + "Title") != null ? copy + "Title" : "VBoxContainer/Title");
        string description = "none";
        RichTextLabel? label = layout.GetNodeOrNull<RichTextLabel>(copy + "EventDescription") ??
            layout.GetNodeOrNull<RichTextLabel>("%EventDescription") ?? layout.GetNodeOrNull<RichTextLabel>("VBoxContainer/EventDescription");
        if (label != null)
            description = $"{CanvasRect(label)} content-height={label.GetContentHeight()} size-y={label.Size.Y:F0} font={label.GetThemeFontSize("normal_font_size")} chars={label.GetTotalCharacterCount()} visible-ratio={label.VisibleRatio:F2}";
        string textScroll = "none";
        if (layout.GetNodeOrNull<ScrollContainer>(slot + "/Panel/Text") is { } scroll)
            textScroll = $"slot={Rect(slot)} scroll={CanvasRect(scroll)} {Overflow(scroll)}";
        string choices = "none";
        if (layout.GetNodeOrNull<ScrollContainer>(PortraitBetweenLayout.EventChoicesScrollName) is { } rows)
        {
            Control? options = layout.GetNodeOrNull<Control>("%OptionsContainer");
            List<Control> buttons = options?.GetChildren().OfType<Control>()
                .Where(c => c.GetType().FullName == PortraitBetweenLayout.EventOptionTypeName).ToList() ?? [];
            string sizes = string.Join(",", buttons.Select(b => $"{b.Size.Y:F0}"));
            choices = $"scroll={CanvasRect(rows)} {Overflow(rows)} rows={buttons.Count}[{sizes}] first={((buttons.Count > 0) ? CanvasRect(buttons[0]) : "none")}";
        }
        return $"canvas={tree.Root.GetVisibleRect().Size} composed={composed} art={Rect("%Portrait")} " +
            $"clip={Rect(PortraitBetweenLayout.EventArtClipName)} text={Rect(composed ? slot : "VBoxContainer")} title={title} " +
            $"description[{description}] text-scroll[{textScroll}] options={Rect("%OptionsContainer")} choices[{choices}]";
    }

    /// <summary>A scroll region's content against its page: whether it actually has more to show.</summary>
    private static string Overflow(ScrollContainer scroll)
    {
        var bar = scroll.GetVScrollBar();
        return $"content={bar.MaxValue:F0} page={bar.Page:F0} at={scroll.ScrollVertical}";
    }
}
