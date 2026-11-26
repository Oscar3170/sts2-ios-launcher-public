using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using STS2Mobile.Platform;

namespace STS2Mobile.GameHost;

/// <summary>
/// One tap selects, a second tap confirms, on the screens whose items a touch would otherwise take at once (portrait only;
/// landscape plays as the desktop game does):
/// <list type="bullet">
/// <item>Shop items (cards, relics, potions, card removal): the first press shows the item's selection reticle and its tip, a press on
/// another item moves the selection, a press elsewhere lets it go, and the release of a second tap on the selected item buys
/// it through the slot's own <c>OnSelected</c>.</item>
/// <item>Card rewards (<see cref="PhoneCardRewardChoice"/>, which also answers the reward), choose-a-card, relic choice and
/// the treasure chest's relics: the release of a second tap takes the item through the screen's own commit method.</item>
/// </list>
/// A hold (still for <see cref="SelectConfirmMath.HoldSeconds"/>) opens the item's native inspection and never confirms; moving
/// past <see cref="SelectConfirmMath.ConfirmSlop"/> cancels the confirmation, so a swipe never buys or takes. The release is
/// read from the window's own touch and mouse events, not the game's release signals, which need a hover that touch does not
/// always set on the simulator. Every press in portrait also lets go of the game's record of it through
/// <see cref="PhoneSwipeRelease"/> (<c>_ignoreMouseRelease</c>, <c>_currentPressedAction</c>, <c>_isPressed</c>), so the game
/// cannot take the item on its own; the slot's ignore flag is put back when the selection goes, and the game writes the
/// other two again on the next press.
/// <para>The selection remembers the item and its current model (a shop entry's card, relic or potion, a card, a relic): a
/// Courier restock, a reroll or a rebuilt choice swaps the model, which clears the selection instead of confirming a
/// replacement. Each screen only supplies where its items are, how to read and take one, and how to show its tip.</para>
/// </summary>
internal sealed class PhoneSelectConfirm
{
    /// <summary>The game's selection reticle, added to the selected item (the name the proofs and the harness look for).</summary>
    internal const string ReticleName = "PhoneSelectedReticle";

    private const string ReticleScenePath = "res://scenes/ui/selection_reticle.tscn";
    // OnDeselect fades over 0.2 s; the reticle is freed just after, so the fade is seen and nothing is left behind.
    private const double ReticleFadeSeconds = 0.22;

    private const string CardSelectionNamespace = "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection";
    private const string ChooseCardTypeName = CardSelectionNamespace + ".NChooseACardSelectionScreen";
    private const string ChooseRelicTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.NChooseARelicSelection";
    private const string TreasureTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic.NTreasureRoomRelicCollection";
    private const string RestSiteRoomTypeName = "MegaCrit.Sts2.Core.Nodes.Rooms.NRestSiteRoom";
    private const string RestSiteButtonTypeName = "MegaCrit.Sts2.Core.Nodes.RestSite.NRestSiteButton";
    private const string MerchantSlotTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.Shops.NMerchantSlot";
    private const string CardHolderTypeName = "MegaCrit.Sts2.Core.Nodes.Cards.Holders.NCardHolder";
    private const string HoverTipSetTypeName = "MegaCrit.Sts2.Core.Nodes.HoverTips.NHoverTipSet";
    private const string HoverTipAlignmentTypeName = "MegaCrit.Sts2.Core.HoverTips.HoverTipAlignment";
    private const string HoverTipTypeName = "MegaCrit.Sts2.Core.HoverTips.IHoverTip";
    private const BindingFlags InstAll = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags StaticAll = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    // The game's own reticle scene, loaded once (one instance per selection; nothing is rebuilt per frame).
    private static PackedScene? _reticleScene;

    // ---- The per-screen adapters -----------------------------------------------------------------------------------------

    /// <summary>
    /// What one screen's items are and what confirming, inspecting and tipping one means. The policy owns the selection,
    /// reticle, tip lifetime, press tracking and thresholds; an adapter only answers for its items.
    /// </summary>
    internal abstract class SelectAdapter(PhoneSelectConfirm owner)
    {
        protected PhoneSelectConfirm Owner { get; } = owner;

        private readonly Dictionary<ulong, Control> _rows = new();

        /// <summary>For the log ("shop item", "card reward").</summary>
        internal abstract string Label { get; }

        /// <summary>Whether an item is a whole card, whose drawn rectangle is its hitbox about the holder's middle.</summary>
        internal abstract bool IsCard { get; }

        /// <summary>Whether <see cref="Model"/> may change under a selected item (a restock, a reroll).</summary>
        internal virtual bool ModelCanChange => true;

        /// <summary>
        /// Whether the item's native tip belongs to its hover state, so the game clears it when the pointer or focus leaves.
        /// Such a tip is put back on the hover-exit signal while the item stays selected; a tip the game keeps up itself is
        /// left alone.
        /// </summary>
        internal virtual bool TipFollowsHover => false;

        /// <summary>
        /// Whether the screen's choice needs a second tap. A screen whose every item can be taken keeps the game's own single
        /// tap: selecting first would only add a tap, as there is nothing to choose between.
        /// </summary>
        internal virtual bool Confirms(Control screen) => true;

        internal abstract int ItemCount(Control screen);

        internal abstract Control? ItemAt(Control screen, int index);

        internal abstract Control PressTarget(Control item);

        internal abstract Rect2 HitRect(Control screen, Control item);

        /// <summary>The item's current model: what must stay the same between the press and the confirming tap.</summary>
        internal abstract object? Model(Control item);

        /// <summary>The game's reticle for this item, already added to it and selected.</summary>
        internal abstract Control AttachReticle(Control item);

        /// <summary>The item's native tip (and, for cards, the hover that comes with it).</summary>
        internal abstract void ShowTip(Control screen, Control item);

        internal abstract void HideTip(Control screen, Control item);

        /// <summary>The item's native inspection (the hold).</summary>
        internal abstract void Inspect(Control screen, Control item);

        /// <summary>The screen's own way of taking the item.</summary>
        internal abstract void Commit(Control screen, Control item);

        /// <summary>Whether the item may be taken now (the chest's relics stay untouchable through its entrance and awards).</summary>
        internal virtual bool CanConfirm(Control screen, Control item) => true;

        internal virtual bool StillShown(Control screen, Control item) => item.IsVisibleInTree();

        /// <summary>Stops the game taking the item on the release of the press that is selecting it.</summary>
        internal virtual void Suppress(Control screen, Control item) { }

        /// <summary>Puts back what <see cref="Suppress"/> changed.</summary>
        internal virtual void Restore(Control screen, Control item) { }

        /// <summary>Portrait now owns the screen: its items may need their native take taken off its release.</summary>
        internal virtual void Attach(Control screen) { }

        /// <summary>Portrait lets the screen go: its native take must work again.</summary>
        internal virtual void Detach(Control screen) { }

        /// <summary>A node under the screen that holds the items, remembered once (never searched by path per frame).</summary>
        protected Control? Row(Control screen, string path)
        {
            ulong id = screen.GetInstanceId();
            if (_rows.TryGetValue(id, out Control? row) && GodotObject.IsInstanceValid(row)) return row;
            row = screen.GetNodeOrNull<Control>(path);
            if (row != null) _rows[id] = row;
            return row;
        }
    }

    private sealed class Tracked(Control screen, SelectAdapter adapter)
    {
        internal readonly Control Screen = screen;
        internal readonly ulong ScreenId = screen.GetInstanceId();
        internal readonly SelectAdapter Adapter = adapter;
        internal bool Attached;
    }

    private sealed class Selection
    {
        internal Control Screen = null!;
        internal ulong ScreenId;
        internal Control Item = null!;
        internal SelectAdapter Adapter = null!;
        internal object? Model;
        internal Control? Reticle;
    }

    // ---- Reflection into the game ----------------------------------------------------------------------------------------

    private readonly Type? _merchantSlotType;
    private readonly FieldInfo? _isHovered;
    private readonly FieldInfo? _ignoreMouseRelease;
    private readonly FieldInfo? _restUnclickable;
    private readonly MethodInfo? _slotOnSelected;
    private readonly MethodInfo? _slotClearTip;
    private readonly MethodInfo? _createTips;
    private readonly MethodInfo? _removeTips;
    private readonly MethodInfo? _alignCardHolder;
    private readonly MethodInfo? _alignRelic;
    private readonly object? _tipNone;
    private readonly Dictionary<(Type Type, string Name), PropertyInfo?> _props = new();
    private readonly Dictionary<(Type Type, string Name), MethodInfo?> _methods = new();
    private readonly Dictionary<Type, MethodInfo?> _slotTips = new();
    private readonly Dictionary<Type, MethodInfo?> _slotPreviews = new();
    private bool _failed;
    private bool _wasPortrait;

    private readonly List<Tracked> _screens = new();
    private readonly HashSet<ulong> _wired = new();
    private readonly HashSet<ulong> _wiredSlots = new();
    private readonly ShopAdapter _shop;
    private readonly CardChoiceAdapter _chooseCard;
    private readonly RelicChoiceAdapter _chooseRelic;
    private readonly TreasureAdapter _treasure;
    private readonly RestAdapter _rest;

    private Selection? _selected;

    // The selected item's hover-exit guard: the exact signal after which the game clears its native tip.
    private Control? _tipGuardItem;
    private Control? _tipGuardHitbox;
    private Callable? _tipGuardExit;

    // The press being tracked: where it began, on what, and how far it has wandered since.
    private bool _pressed;
    private bool _pressOnSelected;
    private bool _holdDone;
    private Control? _pressScreen;
    private Control? _pressItem;
    private SelectAdapter? _pressAdapter;
    private Vector2 _pressCanvas;
    private Vector2 _lastCanvas;
    private Vector2 _windowPressCanvas;
    private float _maxMove;
    private ulong _pressMsec;
    private int _pressTouch = -1;
    private bool _pressSeen;
    private bool _itemPressed;

    // A press that makes a selection owns it only while it stays a tap: a swipe lets it go again and puts back what stood
    // there before (a second tap on the selected item only ever confirms, so it owns nothing).
    private bool _pressMadeSelection;
    private bool _pressSwiped;
    private Selection? _pressPrevious;

    private static int _confirms;
    private static int _holdInspects;
    private static int _clears;

    internal static PhoneSelectConfirm? Instance { get; private set; }

    /// <summary>Whether selection waits for a second tap (false once the game's screens no longer match, or after an error).</summary>
    internal static bool Installed => Instance is { _failed: false };

    /// <summary>The selected item's node name, or null (for the tests).</summary>
    public static string? SelectedName
    {
        get
        {
            Selection? selection = Instance?._selected;
            return selection != null && GodotObject.IsInstanceValid(selection.Item) ? selection.Item.Name : null;
        }
    }

    /// <summary>The selected item itself, or null (for the tests and the proofs).</summary>
    public static Control? SelectedOwner
    {
        get
        {
            Selection? selection = Instance?._selected;
            return selection != null && GodotObject.IsInstanceValid(selection.Item) && selection.Item.IsInsideTree() ? selection.Item : null;
        }
    }

    /// <summary>How many items the second tap has confirmed (for the tests and the proofs).</summary>
    public static int Confirms => _confirms;

    /// <summary>How many holds have opened an item's inspection (for the tests and the proofs).</summary>
    public static int HoldInspects => _holdInspects;

    /// <summary>How many selections have been let go without a confirm (for the tests and the proofs).</summary>
    public static int Clears => _clears;

    /// <summary>Lets the selection go (the shop's pages call it when they change page); safe with nothing selected.</summary>
    public static void Clear(string reason) => Instance?.Drop(reason, counted: true);

    /// <summary>The selection of one screen, or null (the card reward's option lookup and the proofs use it).</summary>
    internal static Control? SelectedItemOn(Control screen)
    {
        Selection? selection = Instance?._selected;
        return selection != null && ReferenceEquals(selection.Screen, screen) && GodotObject.IsInstanceValid(selection.Item) ? selection.Item : null;
    }

    /// <summary>Screens and shop slots under the two taps (for the proofs): 0 in a launch that stayed in landscape.</summary>
    internal static string TrackedForProof() =>
        Instance == null ? "none" : $"{Instance._screens.Count}+{Instance._wiredSlots.Count}slots";

    public PhoneSelectConfirm()
    {
        Instance = this;
        Assembly sts2 = typeof(NGame).Assembly;
        _merchantSlotType = sts2.GetType(MerchantSlotTypeName);
        Type? cardHolderType = sts2.GetType(CardHolderTypeName);
        _isHovered = cardHolderType?.GetField("_isHovered", InstAll);
        _ignoreMouseRelease = _merchantSlotType?.GetField("_ignoreMouseRelease", InstAll);
        _restUnclickable = sts2.GetType(RestSiteButtonTypeName)?.GetField("_isUnclickable", InstAll);
        _slotOnSelected = _merchantSlotType?.GetMethod("OnSelected", InstAll, Type.EmptyTypes);
        _slotClearTip = _merchantSlotType?.GetMethod("ClearHoverTip", InstAll, Type.EmptyTypes);
        Type? tipSet = sts2.GetType(HoverTipSetTypeName);
        Type? alignment = sts2.GetType(HoverTipAlignmentTypeName);
        Type? hoverTip = sts2.GetType(HoverTipTypeName);
        if (tipSet != null && alignment != null)
        {
            // The set-of-tips overload (the one whose second parameter is the list, not a single tip).
            _createTips = tipSet.GetMethods(StaticAll).FirstOrDefault(m =>
            {
                ParameterInfo[] parameters = m.GetParameters();
                return m.Name == "CreateAndShow" && parameters.Length == 3 && parameters[2].ParameterType == alignment &&
                       parameters[1].ParameterType != hoverTip;
            });
            _removeTips = tipSet.GetMethod("Remove", StaticAll, [typeof(Control)]);
            _alignCardHolder = tipSet.GetMethod("SetAlignmentForCardHolder", InstAll);
            _alignRelic = tipSet.GetMethod("SetAlignmentForRelic", InstAll);
            _tipNone = Enum.ToObject(alignment, 0);
        }
        _shop = new ShopAdapter(this);
        _chooseCard = new CardChoiceAdapter(this);
        _chooseRelic = new RelicChoiceAdapter(this);
        _treasure = new TreasureAdapter(this);
        _rest = new RestAdapter(this);
        if (!PhoneSwipeRelease.Shared.Ready || _ignoreMouseRelease == null || _slotOnSelected == null)
        {
            _failed = true;
            GD.PrintErr("Phone select confirm: the game's choice screens changed; one tap takes a card, relic or shop item as in the game.");
        }
    }

    // ---- Node discovery and input ----------------------------------------------------------------------------------------

    public void OnNodeAdded(Node node)
    {
        if (_failed) return;
        try
        {
            string? name = node.GetType().FullName;
            if (name == ChooseCardTypeName && node is Control chooseCard) Track(chooseCard, _chooseCard);
            else if (name == ChooseRelicTypeName && node is Control chooseRelic) Track(chooseRelic, _chooseRelic);
            else if (name == TreasureTypeName && node is Control treasure) Track(treasure, _treasure);
            else if (name == RestSiteRoomTypeName && node is Control rest && _restUnclickable != null) Track(rest, _rest);
            else if (_merchantSlotType != null && _merchantSlotType.IsInstanceOfType(node))
            {
                // A slot already in the shop when Portrait Mode is turned on is ready; a new one is not yet.
                if (node.IsNodeReady()) WireSlot((Control)node);
                else node.Connect(Node.SignalName.Ready, Callable.From(() => WireSlot((Control)node)), (uint)GodotObject.ConnectFlags.OneShot);
            }
        }
        catch (Exception error)
        {
            Fail(error);
        }
    }

    /// <summary>
    /// Takes over every choice screen and shop slot already open (Portrait Mode turned on with the game running, or a phone
    /// rotation). Idempotent: a screen or slot already under the two taps is left alone.
    /// </summary>
    public void AdoptOpenScreens()
    {
        if (Engine.GetMainLoop() is not SceneTree tree || !GodotObject.IsInstanceValid(tree.Root)) return;
        var pending = new Stack<Node>();
        pending.Push(tree.Root);
        while (pending.Count > 0)
        {
            Node node = pending.Pop();
            OnNodeAdded(node);
            foreach (Node child in node.GetChildren()) pending.Push(child);
        }
    }

    /// <summary>Adds a screen whose items now wait for a second tap (the card reward does this itself).</summary>
    internal void Track(Control screen, SelectAdapter adapter)
    {
        if (_failed || !SafeAreaCanvas.IsPortrait || !GodotObject.IsInstanceValid(screen)) return;
        ulong id = screen.GetInstanceId();
        foreach (Tracked tracked in _screens)
        {
            if (tracked.ScreenId != id) continue;
            if (tracked.Attached) return;
            tracked.Attached = true;
            tracked.Adapter.Attach(screen);
            return;
        }
        _screens.Add(new Tracked(screen, adapter) { Attached = true });
        adapter.Attach(screen);
        screen.TreeExiting += () => OnScreenExiting(id);
    }

    /// <summary>Leaves the screen to the game: everything <see cref="SelectAdapter.Attach"/> took off must be put back.</summary>
    private void DetachAll()
    {
        foreach (Tracked tracked in _screens)
        {
            if (!tracked.Attached) continue;
            tracked.Attached = false;
            if (!GodotObject.IsInstanceValid(tracked.Screen)) continue;
            try
            {
                tracked.Adapter.Detach(tracked.Screen);
            }
            catch (Exception error)
            {
                GD.PrintErr("Phone select confirm: the screen's native input could not be put back: ", error.Message);
            }
        }
    }

    private void OnScreenExiting(ulong screenId)
    {
        for (int i = _screens.Count - 1; i >= 0; i--)
            if (_screens[i].ScreenId == screenId) _screens.RemoveAt(i);
        if (_selected != null && _selected.ScreenId == screenId) Drop("screen closed", counted: true);
    }

    /// <summary>Every press and release of the window's own input, before the game's controls see it.</summary>
    public void OnWindowInput(InputEvent input)
    {
        if (_failed) return;
        try
        {
            HandleWindowInput(input);
        }
        catch (Exception error)
        {
            Fail(error);
        }
    }

    private void HandleWindowInput(InputEvent input)
    {
        switch (input)
        {
            case InputEventScreenTouch { Pressed: true } touch:
                _pressSeen = true;
                _pressTouch = touch.Index;
                _windowPressCanvas = ToCanvas(touch.Position);
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } press:
                _pressSeen = true;
                _pressTouch = -1;
                _windowPressCanvas = ToCanvas(press.Position);
                break;
            case InputEventScreenTouch { Pressed: false } release when _pressed && (_pressTouch < 0 || release.Index == _pressTouch):
                Release(release.Position);
                break;
            case InputEventMouseButton { Pressed: false, ButtonIndex: MouseButton.Left } release when _pressed:
                // A touch's release is also delivered as this; the first release has already ended the press.
                Release(release.Position);
                break;
            case InputEventScreenDrag drag when _pressed:
                Moved(drag.Position);
                break;
            case InputEventMouseMotion motion when _pressed && (motion.ButtonMask & MouseButtonMask.Left) != 0:
                Moved(motion.Position);
                break;
        }
    }

    public void OnProcessFrame()
    {
        if (_failed) return;
        try
        {
            bool portrait = SafeAreaCanvas.IsPortrait;
            // Portrait Mode turned on with a screen already open (a phone rotation, or the host's live switch): take it over
            // now, before the first tap on it.
            if (portrait && !_wasPortrait) AdoptOpenScreens();
            _wasPortrait = portrait;
            if (!portrait)
            {
                if (_selected != null) Drop("landscape", counted: true);
                DetachAll();
                ResetPress();
                return;
            }
            // A press that lands on no item lets the selection go.
            if (_pressSeen && !_itemPressed) Drop("elsewhere", counted: true);
            _pressSeen = _itemPressed = false;

            for (int i = _screens.Count - 1; i >= 0; i--)
            {
                Tracked tracked = _screens[i];
                if (!GodotObject.IsInstanceValid(tracked.Screen) || !tracked.Screen.IsInsideTree())
                {
                    _screens.RemoveAt(i);
                    continue;
                }
                WireItems(tracked.Screen, tracked.Adapter);
            }
            if (_selected is { } kept && !StillSelected(kept, out string why)) Drop($"item changed ({why})", counted: true);
            StepHold();
        }
        catch (Exception error)
        {
            Fail(error);
        }
    }

    private void Fail(Exception error)
    {
        _failed = true;
        GD.PrintErr("Phone select confirm: turned off after an error: ", error);
        if (_selected != null) Drop("turned off", counted: false);
        DetachAll();
    }

    // ---- Press, hold, release ---------------------------------------------------------------------------------------------

    private void OnItemPressed(Control screen, Control item, SelectAdapter adapter, InputEvent e)
    {
        if (_failed || !SafeAreaCanvas.IsPortrait) return;
        if (e is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } press) return;
        if (!GodotObject.IsInstanceValid(screen) || !GodotObject.IsInstanceValid(item)) return;
        if (!adapter.Confirms(screen)) return;
        BeginPress(screen, item, adapter, press.Position);
    }

    /// <summary>
    /// Starts tracking a press. <paramref name="guiPosition" /> is the press point the item's own signal carries (the canvas's
    /// coordinates); the window's own press point is used instead when there was one, so that it is measured in the same space
    /// as the window's drag and release points.
    /// </summary>
    private void BeginPress(Control screen, Control item, SelectAdapter adapter, Vector2 guiPosition)
    {
        try
        {
            _itemPressed = true;
            // The game takes these items on the release of a press; never here.
            adapter.Suppress(screen, item);
            bool second = IsSelected(screen, item, adapter);
            Selection? previous = _selected;
            if (second)
            {
                if (adapter.IsCard) HoverOnly(item);
                GD.Print($"Phone select confirm: {adapter.Label} {item.Name} tapped again; releasing confirms.");
            }
            else
            {
                Drop("other item", counted: true);
                Select(screen, item, adapter);
            }
            _pressed = true;
            _pressOnSelected = second;
            _pressMadeSelection = !second;
            _pressSwiped = false;
            _pressPrevious = previous;
            _holdDone = false;
            _pressScreen = screen;
            _pressItem = item;
            _pressAdapter = adapter;
            _pressCanvas = _lastCanvas = _pressSeen ? _windowPressCanvas : guiPosition;
            _maxMove = 0f;
            _pressMsec = Time.GetTicksMsec();
        }
        catch (Exception error)
        {
            Fail(error);
        }
    }

    private void Moved(Vector2 windowPosition)
    {
        _lastCanvas = ToCanvas(windowPosition);
        _maxMove = Math.Max(_maxMove, _pressCanvas.DistanceTo(_lastCanvas));
        CancelSwipedSelection();
    }

    /// <summary>
    /// A press that has become a swipe never made a selection: it lets go of the one the press made and puts back the one
    /// that stood before, so scrolling a card grid or a shop page never moves the reticle, and the next tap selects afresh.
    /// A second tap on the selected item owns nothing, so a swipe from it only cancels its confirmation (the selection stays).
    /// </summary>
    private void CancelSwipedSelection()
    {
        if (!_pressed || _pressSwiped || !_pressMadeSelection || SelectConfirmMath.WithinSlop(_maxMove)) return;
        _pressSwiped = true;
        _holdDone = true;
        // The press's own release is still on its way: keep the item's release suppression until it has gone (a shop slot's
        // ignore flag is cleared by its next press), or the swipe itself would buy.
        Drop("swipe", counted: true, restore: false);
        Selection? previous = _pressPrevious;
        _pressPrevious = null;
        if (previous == null) return;
        if (!GodotObject.IsInstanceValid(previous.Screen) || !GodotObject.IsInstanceValid(previous.Item)) return;
        if (!previous.Screen.IsInsideTree() || !previous.Item.IsInsideTree() || previous.Item.IsQueuedForDeletion()) return;
        if (!previous.Adapter.StillShown(previous.Screen, previous.Item)) return;
        Select(previous.Screen, previous.Item, previous.Adapter);
    }

    private void StepHold()
    {
        if (!_pressed || _holdDone || _pressItem == null || _pressAdapter == null) return;
        double elapsed = SelectConfirmMath.SecondsSince(_pressMsec, Time.GetTicksMsec());
        if (elapsed < SelectConfirmMath.HoldSeconds) return;
        SelectPress action = SelectConfirmMath.Decide(_maxMove, elapsed, released: false);
        if (action != SelectPress.Inspect)
        {
            if (action == SelectPress.Cancel) _holdDone = true;
            return;
        }
        _holdDone = true;
        if (_selected == null || !ReferenceEquals(_selected.Item, _pressItem)) return;
        if (_pressScreen == null) return;
        try
        {
            _pressAdapter.Inspect(_pressScreen, _pressItem);
            _holdInspects++;
            GD.Print($"Phone select confirm: {_pressAdapter.Label} {_pressItem.Name} held; its inspection opened.");
        }
        catch (Exception error)
        {
            // A tip or an inspection that cannot open leaves the item under the two taps as it was.
            GD.PrintErr("Phone select confirm: the item's inspection could not open: ", error.Message);
        }
    }

    private void Release(Vector2 windowPosition)
    {
        Control? item = _pressItem;
        Control? screen = _pressScreen;
        SelectAdapter? adapter = _pressAdapter;
        bool second = _pressOnSelected;
        bool held = _holdDone;
        double elapsed = SelectConfirmMath.SecondsSince(_pressMsec, Time.GetTicksMsec());
        Moved(windowPosition);
        float moved = _maxMove;
        ResetPress();
        if (!second || held || item == null || screen == null || adapter == null) return;
        if (SelectConfirmMath.Decide(moved, elapsed, released: true) != SelectPress.Confirm) return;
        if (_selected == null || !ReferenceEquals(_selected.Item, item)) return;
        if (!StillSelected(_selected, out string changed))
        {
            Drop($"item changed ({changed})", counted: true);
            return;
        }
        if (!adapter.HitRect(screen, item).HasPoint(_lastCanvas)) return;
        if (!adapter.CanConfirm(screen, item)) return;
        Confirm(screen, item, adapter);
    }

    private void ResetPress()
    {
        _pressed = false;
        _pressOnSelected = false;
        _pressMadeSelection = false;
        _pressSwiped = false;
        _pressPrevious = null;
        _holdDone = false;
        _pressScreen = null;
        _pressItem = null;
        _pressAdapter = null;
        _maxMove = 0f;
    }

    /// <summary>
    /// Keeps the selected item's native tip up when the game's hover state takes it down. A shop slot owns one tip
    /// (<c>NHoverTipSet.CreateAndShow(this, …)</c>) and <c>NMerchantSlot.OnUnfocus</c> calls <c>ClearHoverTip</c> on the
    /// hitbox's <c>MouseExited</c> and the slot's <c>FocusExited</c>; a selecting tap's release (the emulated mouse leaving the
    /// slot while it settles) runs that path and removed a relic's or potion's description while the item was still selected.
    /// The guard is connected after the game's own handler, so it re-creates the tip in the same signal emission — no frame
    /// exists without it — and only while that same item is still the selection. Nothing runs per frame.
    /// </summary>
    private void HookSelectedTip(Control screen, Control item, SelectAdapter adapter)
    {
        UnhookSelectedTip();
        try
        {
            Callable keep = Callable.From(() => RefreshSelectedTip(screen, item, adapter));
            _tipGuardItem = item;
            _tipGuardExit = keep;
            if (!item.IsConnected(Control.SignalName.FocusExited, keep))
                item.Connect(Control.SignalName.FocusExited, keep);
            Control? hitbox = adapter.PressTarget(item);
            if (hitbox != null && GodotObject.IsInstanceValid(hitbox) && !ReferenceEquals(hitbox, item))
            {
                _tipGuardHitbox = hitbox;
                if (!hitbox.IsConnected(Control.SignalName.MouseExited, keep))
                    hitbox.Connect(Control.SignalName.MouseExited, keep);
            }
        }
        catch (Exception error)
        {
            // The tip is a courtesy: a selection without one still takes two taps.
            GD.PrintErr("Phone select confirm: the selected item's tip could not be kept up: ", error.Message);
        }
    }

    private void UnhookSelectedTip()
    {
        if (_tipGuardExit is not { } keep) return;
        if (_tipGuardItem is { } item && GodotObject.IsInstanceValid(item) && item.IsConnected(Control.SignalName.FocusExited, keep))
            item.Disconnect(Control.SignalName.FocusExited, keep);
        if (_tipGuardHitbox is { } hitbox && GodotObject.IsInstanceValid(hitbox) && hitbox.IsConnected(Control.SignalName.MouseExited, keep))
            hitbox.Disconnect(Control.SignalName.MouseExited, keep);
        _tipGuardItem = null;
        _tipGuardHitbox = null;
        _tipGuardExit = null;
    }

    /// <summary>The game has just taken the hover tip down: put the selected item's tip back, in the same emission.</summary>
    private void RefreshSelectedTip(Control screen, Control item, SelectAdapter adapter)
    {
        try
        {
            if (_failed || _selected is not { } chosen) return;
            if (!ReferenceEquals(chosen.Item, item) || !ReferenceEquals(chosen.Screen, screen)) return;
            if (!GodotObject.IsInstanceValid(item) || !item.IsInsideTree()) return;
            adapter.ShowTip(screen, item);
        }
        catch (Exception error)
        {
            GD.PrintErr($"Phone select confirm: the {adapter.Label}'s tip could not be shown again: ", error.Message);
        }
    }

    // ---- The selection ----------------------------------------------------------------------------------------------------

    private bool IsSelected(Control screen, Control item, SelectAdapter adapter) =>
        _selected is { } selection && ReferenceEquals(selection.Item, item) && ReferenceEquals(selection.Screen, screen) &&
        (adapter.ModelCanChange
            ? SelectConfirmMath.IsSameItem(item.GetInstanceId(), adapter.Model(item), selection.Item.GetInstanceId(), selection.Model)
            : item.GetInstanceId() == selection.Item.GetInstanceId());

    private void Select(Control screen, Control item, SelectAdapter adapter)
    {
        Control reticle = adapter.AttachReticle(item);
        object? model = adapter.Model(item);
        _selected = new Selection
        {
            Screen = screen, ScreenId = screen.GetInstanceId(), Item = item, Adapter = adapter, Model = model, Reticle = reticle,
        };
        try
        {
            adapter.ShowTip(screen, item);
        }
        catch (Exception error)
        {
            // The tip is a courtesy: a selection without one still takes two taps.
            GD.PrintErr($"Phone select confirm: the {adapter.Label}'s tip could not be shown: ", error.Message);
        }
        if (adapter.TipFollowsHover) HookSelectedTip(screen, item, adapter);
        GD.Print($"Phone select confirm: {adapter.Label} {item.Name} selected; a second tap confirms it.");
    }

    /// <summary>Selects an item without a press (the card reward turns an answer from elsewhere into a selection).</summary>
    internal void SelectFrom(Control screen, Control item)
    {
        if (_failed || !SafeAreaCanvas.IsPortrait) return;
        foreach (Tracked tracked in _screens)
        {
            if (tracked.ScreenId != screen.GetInstanceId() || !GodotObject.IsInstanceValid(item)) continue;
            Drop("other item", counted: true);
            Select(screen, item, tracked.Adapter);
            return;
        }
    }

    /// <summary>Lets one screen's selection go (a reroll rebuilds the card reward's cards).</summary>
    internal static void ClearFor(Control screen, string reason)
    {
        if (Instance?._selected is { } selection && ReferenceEquals(selection.Screen, screen)) Clear(reason);
    }

    private void Confirm(Control screen, Control item, SelectAdapter adapter)
    {
        // The game's own release of this press is still on its way: the item keeps suppressing it until the next press.
        Drop("confirmed", counted: false, restore: false);
        _confirms++;
        GD.Print($"Phone select confirm: {adapter.Label} {item.Name} confirmed.");
        adapter.Commit(screen, item);
    }

    private void Drop(string reason, bool counted) => Drop(reason, counted, restore: true);

    private void Drop(string reason, bool counted, bool restore)
    {
        Selection? selection = _selected;
        if (selection == null) return;
        _selected = null;
        UnhookSelectedTip();
        if (counted)
        {
            _clears++;
            GD.Print($"Phone select confirm: selection let go ({reason}).");
        }
        if (selection.Reticle != null) DeselectReticle(selection.Reticle, selection.Item);
        try
        {
            selection.Adapter.HideTip(selection.Screen, selection.Item);
            if (restore) selection.Adapter.Restore(selection.Screen, selection.Item);
        }
        catch (Exception error)
        {
            GD.PrintErr("Phone select confirm: the selection could not be put back: ", error.Message);
        }
    }

    /// <summary>
    /// Whether the selection still stands on the item it was made on, with the reason when it does not (logged with the drop).
    /// A screen that has gone, a hidden item and, where the adapter allows it, a swapped model end the selection; screen
    /// state that changes during an animation (a holder's mouse filter) does not.
    /// </summary>
    private bool StillSelected(Selection selection, out string why)
    {
        Control item = selection.Item, screen = selection.Screen;
        if (!GodotObject.IsInstanceValid(item)) { why = "item gone"; return false; }
        if (!item.IsInsideTree()) { why = "item left the tree"; return false; }
        if (item.IsQueuedForDeletion()) { why = "item freed"; return false; }
        if (!GodotObject.IsInstanceValid(screen)) { why = "screen gone"; return false; }
        if (!screen.IsInsideTree()) { why = "screen left the tree"; return false; }
        if (!selection.Adapter.StillShown(screen, item)) { why = "item hidden"; return false; }
        if (selection.Adapter.ModelCanChange && !ReferenceEquals(selection.Adapter.Model(item), selection.Model))
        {
            why = "model changed";
            return false;
        }
        why = "";
        return true;
    }

    private void WireItems(Control screen, SelectAdapter adapter)
    {
        int count = adapter.ItemCount(screen);
        for (int i = 0; i < count; i++)
        {
            Control? item = adapter.ItemAt(screen, i);
            if (item == null || !GodotObject.IsInstanceValid(item) || item.IsQueuedForDeletion()) continue;
            Control press = adapter.PressTarget(item);
            if (press == null || !GodotObject.IsInstanceValid(press)) continue;
            ulong id = press.GetInstanceId();
            if (!_wired.Add(id)) continue;
            press.Connect("MousePressed", Callable.From((InputEvent e) => OnItemPressed(screen, item, adapter, e)));
            item.TreeExiting += () => _wired.Remove(id);
        }
    }

    // ---- The shop's press -------------------------------------------------------------------------------------------------

    private void WireSlot(Control slot)
    {
        if (_failed || !GodotObject.IsInstanceValid(slot) || slot.GetNodeOrNull<Control>("%Hitbox") is not { } hitbox) return;
        ulong id = slot.GetInstanceId();
        if (!_wiredSlots.Add(id)) return;
        hitbox.Connect("MousePressed", Callable.From((InputEvent e) => OnSlotPressed(slot, e)));
        slot.TreeExiting += () => _wiredSlots.Remove(id);
    }

    private void OnSlotPressed(Control slot, InputEvent e)
    {
        if (_failed || !SafeAreaCanvas.IsPortrait) return;
        if (e is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } press) return;
        if (!GodotObject.IsInstanceValid(slot)) return;
        Control? inventory = InventoryOf(slot);
        if (inventory == null) return;
        BeginPress(inventory, slot, _shop, press.Position);
    }

    /// <summary>The merchant inventory a slot stands in (its <c>IsOpen</c> says whether the shop still shows).</summary>
    private Control? InventoryOf(Control slot)
    {
        for (Node? node = slot.GetParent(); node != null; node = node.GetParent())
        {
            if (node is not Control control) continue;
            (Type, string) key = (control.GetType(), "IsOpen");
            if (!_props.TryGetValue(key, out PropertyInfo? isOpen))
                _props[key] = isOpen = control.GetType().GetProperty("IsOpen", InstAll);
            if (isOpen != null) return control;
        }
        return null;
    }

    private bool InventoryOpen(Control inventory) => Prop(inventory, "IsOpen") is true;

    // ---- Adapters -------------------------------------------------------------------------------------------------------

    /// <summary>Shop cards, relics, potions and the card removal: the slot's own tip, preview and purchase.</summary>
    private sealed class ShopAdapter(PhoneSelectConfirm owner) : SelectAdapter(owner)
    {
        internal override string Label => "shop item";
        internal override bool IsCard => false;
        // The slot's tip is created by its own hover path and its OnUnfocus clears it; keep it while the item is selected.
        internal override bool TipFollowsHover => true;
        internal override int ItemCount(Control screen) => 0;
        internal override Control? ItemAt(Control screen, int index) => null;
        internal override Control PressTarget(Control item) => item.GetNodeOrNull<Control>("%Hitbox") ?? item;
        internal override Rect2 HitRect(Control screen, Control item) => PressTarget(item).GetGlobalRect();

        internal override object? Model(Control item)
        {
            object? entry = Owner.Prop(item, "Entry");
            if (entry == null) return null;
            object? creation = Owner.Prop(entry, "CreationResult");
            if (creation != null) return Owner.Prop(creation, "Card") ?? creation;
            return Owner.Prop(entry, "Model") ?? entry;
        }

        internal override Control AttachReticle(Control item)
        {
            // The slot's hitbox is the item's whole drawn rectangle (its card, relic or potion), as the old border was.
            Control target = PressTarget(item);
            return PhoneSelectConfirm.AttachReticle(target, new Rect2(Vector2.Zero, target.Size));
        }

        internal override void ShowTip(Control screen, Control item) => Owner.SlotTip(item)?.Invoke(item, null);

        internal override void HideTip(Control screen, Control item) => Owner._slotClearTip?.Invoke(item, null);

        internal override void Inspect(Control screen, Control item) => Owner.SlotPreview(item)?.Invoke(item, null);

        internal override void Commit(Control screen, Control item)
        {
            if (Owner._slotOnSelected!.Invoke(item, null) is Task buying)
                buying.ContinueWith(t => GD.PrintErr("Phone select confirm: purchase failed: ", t.Exception),
                    TaskContinuationOptions.OnlyOnFaulted);
        }

        internal override bool CanConfirm(Control screen, Control item) => Owner.InventoryOpen(screen);

        internal override bool StillShown(Control screen, Control item)
        {
            if (!Owner.InventoryOpen(screen)) return false;
            Control hitbox = PressTarget(item);
            return GodotObject.IsInstanceValid(hitbox) && hitbox.IsVisibleInTree();
        }

        internal override void Suppress(Control screen, Control item) => PhoneSwipeRelease.Shared.ReleaseSlot(item);

        internal override void Restore(Control screen, Control item)
        {
            if (GodotObject.IsInstanceValid(item)) Owner._ignoreMouseRelease?.SetValue(item, false);
        }
    }

    /// <summary>Choose a card: the row's card holders; the reticle follows the card's drawn rect, the hover gives its keyword tips.</summary>
    private class CardChoiceAdapter(PhoneSelectConfirm owner) : SelectAdapter(owner)
    {
        internal override string Label => "choose a card";
        internal override bool IsCard => true;

        protected virtual string RowPath => "CardRow";
        protected virtual string CommitMethod => "SelectHolder";
        protected virtual string InspectMethod => "OpenPreviewScreen";

        internal override int ItemCount(Control screen) => Row(screen, RowPath)?.GetChildCount() ?? 0;
        internal override Control? ItemAt(Control screen, int index) => Row(screen, RowPath)?.GetChild(index) as Control;
        internal override Control PressTarget(Control item) => item.GetNodeOrNull<Control>("%Hitbox") ?? item;
        internal override Rect2 HitRect(Control screen, Control item) => PressTarget(item).GetGlobalRect();
        internal override object? Model(Control item) => Owner.Prop(item, "CardModel");

        internal override Control AttachReticle(Control item)
        {
            // The card's drawn rectangle about the holder's middle; the holder's own scale (focus, row) carries the reticle too.
            return PhoneSelectConfirm.AttachReticle(item, CardRect(item));
        }

        internal override void ShowTip(Control screen, Control item)
        {
            Owner.HoverOnly(item);
            Owner.ShowCardTip(item);
        }

        internal override void HideTip(Control screen, Control item) => Owner.ClearCardTip(item);

        internal override void Inspect(Control screen, Control item) => Owner.CallScreen(screen, InspectMethod, item);

        internal override void Commit(Control screen, Control item) => Owner.CallScreen(screen, CommitMethod, item);

        internal override void Suppress(Control screen, Control item) => Owner.ClearCardPress(item);
    }

    /// <summary>Relic choice: the row's relics, taken by the screen's own <c>SelectHolder</c>.</summary>
    private sealed class RelicChoiceAdapter(PhoneSelectConfirm owner) : SelectAdapter(owner)
    {
        internal override string Label => "relic choice";
        internal override bool IsCard => false;
        internal override int ItemCount(Control screen) => Row(screen, "RelicRow")?.GetChildCount() ?? 0;
        internal override Control? ItemAt(Control screen, int index) => Row(screen, "RelicRow")?.GetChild(index) as Control;
        internal override Control PressTarget(Control item) => item;
        internal override Rect2 HitRect(Control screen, Control item) => item.GetGlobalRect();
        internal override object? Model(Control item) => Owner.RelicModel(item);

        internal override Control AttachReticle(Control item) =>
            PhoneSelectConfirm.AttachReticle(item, new Rect2(Vector2.Zero, item.Size));

        internal override void ShowTip(Control screen, Control item) => Owner.ShowRelicTip(item);
        internal override void HideTip(Control screen, Control item) => Owner.HideTip(item);

        internal override void Inspect(Control screen, Control item) => Owner.OpenRelicInspect(item);

        internal override void Commit(Control screen, Control item) => Owner.CallScreen(screen, "SelectHolder", item);

        internal override void Suppress(Control screen, Control item) => Owner.ClearPress(item);
    }

    /// <summary>The treasure chest's relics: the collection's own <c>PickRelic</c>, shared in multiplayer.</summary>
    private sealed class TreasureAdapter(PhoneSelectConfirm owner) : SelectAdapter(owner)
    {
        private readonly Dictionary<Type, FieldInfo?> _holdersFields = new();
        private readonly Dictionary<ulong, int> _knownCounts = new();

        // Each holder's own Released -> collection.PickRelic connection, taken off while portrait owns the chest (the second
        // tap calls PickRelic itself). The native signal fires from the holder's release whatever the press flag says, so the
        // press's whole release has to be kept from the collection.
        private readonly Dictionary<ulong, List<(Control Holder, Callable Callable)>> _blocked = new();

        internal override string Label => "treasure relic";
        internal override bool IsCard => false;

        private IList? Holders(Control screen)
        {
            Type type = screen.GetType();
            if (!_holdersFields.TryGetValue(type, out FieldInfo? field))
                _holdersFields[type] = field = type.GetField("_holdersInUse", InstAll);
            return field?.GetValue(screen) as IList;
        }

        internal override int ItemCount(Control screen)
        {
            IList? holders = Holders(screen);
            int count = holders?.Count ?? 0;
            // The collection connects its holders' release when the chest opens; take it off then, before the first press,
            // unless the player can take every relic, which keeps the game's own tap.
            ulong id = screen.GetInstanceId();
            if (!_knownCounts.TryGetValue(id, out int known) || known != count)
            {
                _knownCounts[id] = count;
                if (count > 0 && Confirms(screen)) Block(screen);
                else Unblock(screen);
            }
            return count;
        }

        /// <summary>
        /// Only a choice between relics takes two taps. A single player picks one relic per round; when the chest shows no
        /// more relics than the rounds left (a normal chest's one relic), every relic is taken and there is nothing to choose.
        /// With other players a relic can be contested, so the choice always counts. Unknown rules keep the second tap.
        /// </summary>
        internal override bool Confirms(Control screen)
        {
            int count = Holders(screen)?.Count ?? 0;
            return count <= 0 || !(RelicPicks.LocalPicksLeft() is int picks && count <= picks);
        }

        internal override Control? ItemAt(Control screen, int index)
        {
            IList? holders = Holders(screen);
            return holders != null && index >= 0 && index < holders.Count ? holders[index] as Control : null;
        }

        internal override Control PressTarget(Control item) => item;
        internal override Rect2 HitRect(Control screen, Control item) => item.GetGlobalRect();
        internal override object? Model(Control item) => Owner.RelicModel(item);

        // A chest holder is made once for its relic and never shows another; the collection is built again for the next
        // chest. Its identity is the holder itself, not its relic node, so nothing read from the animating relic can drop it.
        internal override bool ModelCanChange => false;

        /// <summary>The selecting press must not reach the collection: its own release would pick the relic straight away.</summary>
        internal override void Suppress(Control screen, Control item)
        {
            Owner.ClearPress(item);
            Block(screen);
        }

        internal override void Attach(Control screen)
        {
            if (Confirms(screen)) Block(screen);
        }

        internal override void Detach(Control screen) => Unblock(screen);

        /// <summary>
        /// Takes every holder's <c>Released</c> connection off while portrait owns the chest. The collection connects it in
        /// <c>InitializeRelics</c> (after the collection is tracked), so it is scanned again on every press; only connections
        /// made since the last scan are new to block.
        /// </summary>
        private void Block(Control screen)
        {
            // A chest that has gone leaves nothing to put back; the holder nodes went with it.
            foreach (ulong dead in _blocked.Keys.Where(id => !GodotObject.IsInstanceIdValid(id)).ToList()) _blocked.Remove(dead);
            foreach (ulong dead in _knownCounts.Keys.Where(id => !GodotObject.IsInstanceIdValid(id)).ToList()) _knownCounts.Remove(dead);
            ulong screenId = screen.GetInstanceId();
            if (!_blocked.TryGetValue(screenId, out List<(Control Holder, Callable Callable)>? blocked))
                _blocked[screenId] = blocked = new();
            IList? holders = Holders(screen);
            if (holders == null) return;
            foreach (object? entry in holders)
            {
                if (entry is not Control holder || !GodotObject.IsInstanceValid(holder) || !holder.IsInsideTree()) continue;
                foreach (Godot.Collections.Dictionary connection in holder.GetSignalConnectionList("Released"))
                {
                    Variant value = connection["callable"];
                    if (value.VariantType != Variant.Type.Callable) continue;
                    Callable callable = value.AsCallable();
                    if (blocked.Any(b => ReferenceEquals(b.Holder, holder) && b.Callable.Equals(callable))) continue;
                    holder.Disconnect("Released", callable);
                    blocked.Add((holder, callable));
                    GD.Print("Phone select confirm: the treasure relic waits for the second tap.");
                }
            }
        }

        private void Unblock(Control screen)
        {
            if (!_blocked.Remove(screen.GetInstanceId(), out List<(Control Holder, Callable Callable)>? blocked)) return;
            foreach ((Control holder, Callable callable) in blocked)
            {
                if (!GodotObject.IsInstanceValid(holder) || !holder.IsInsideTree()) continue;
                try
                {
                    holder.Connect("Released", callable);
                }
                catch (Exception error)
                {
                    GD.PrintErr("Phone select confirm: the treasure relic's native pick could not be put back: ", error.Message);
                }
            }
        }

        internal override Control AttachReticle(Control item) =>
            PhoneSelectConfirm.AttachReticle(item, new Rect2(Vector2.Zero, item.Size));

        internal override void ShowTip(Control screen, Control item) => Owner.ShowRelicTip(item);
        internal override void HideTip(Control screen, Control item) => Owner.HideTip(item);
        internal override void Inspect(Control screen, Control item) => Owner.OpenRelicInspect(item);
        internal override void Commit(Control screen, Control item) => Owner.CallScreen(screen, "PickRelic", item);

        // The holders ignore touches through the entrance, so a press cannot select one then; the collection also ignores
        // picks for its first 200 ms (its own guard). The entrance state is not part of the selection's life: a live
        // MouseFilter read there would let a closing animation drop a selection that is still on screen.
        internal override bool CanConfirm(Control screen, Control item) => item.MouseFilter != Control.MouseFilterEnum.Ignore;
    }

    /// <summary>
    /// Rest-site options: the room's own choice buttons. The first tap selects one and keeps the game's option description on
    /// screen (an option's focus is what makes the room show it); the second tap on the same option runs the game's own
    /// release, which takes the option. The selecting press is stopped by the button's own unclickable guard, so its release
    /// cannot take the option; the guard is put back on the confirming tap and whenever the selection is let go.
    /// </summary>
    private sealed class RestAdapter(PhoneSelectConfirm owner) : SelectAdapter(owner)
    {
        // Buttons whose clickable guard this file turned off, and what it was.
        private readonly Dictionary<ulong, bool> _suppressed = new();

        internal override string Label => "rest option";
        internal override bool IsCard => false;

        internal override int ItemCount(Control screen) => Row(screen, "%ChoicesContainer")?.GetChildCount() ?? 0;

        internal override Control? ItemAt(Control screen, int index) =>
            Row(screen, "%ChoicesContainer")?.GetChild(index) as Control;

        internal override Control PressTarget(Control item) => item;
        internal override Rect2 HitRect(Control screen, Control item) => item.GetGlobalRect();
        internal override object? Model(Control item) => Owner.Prop(item, "Option");

        internal override Control AttachReticle(Control item) =>
            PhoneSelectConfirm.AttachReticle(item, new Rect2(Vector2.Zero, item.Size));

        // An option's focus is what makes the room show its description (`NRestSiteButton.RefreshTextState`); losing it fades
        // the copy out again. Both are the game's own paths, so the text and its fade stay native.
        internal override void ShowTip(Control screen, Control item) => item.GrabFocus();
        internal override void HideTip(Control screen, Control item) => item.ReleaseFocus();
        internal override void Inspect(Control screen, Control item) => item.GrabFocus();

        internal override void Suppress(Control screen, Control item)
        {
            if (Owner._restUnclickable == null) return;
            foreach (ulong dead in _suppressed.Keys.Where(id => !GodotObject.IsInstanceIdValid(id)).ToList()) _suppressed.Remove(dead);
            ulong id = item.GetInstanceId();
            if (!_suppressed.ContainsKey(id))
                _suppressed[id] = Owner._restUnclickable.GetValue(item) is true;
            Owner._restUnclickable.SetValue(item, true);
        }

        internal override void Restore(Control screen, Control item)
        {
            if (Owner._restUnclickable == null) return;
            if (!_suppressed.Remove(item.GetInstanceId(), out bool was)) return;
            if (GodotObject.IsInstanceValid(item)) Owner._restUnclickable.SetValue(item, was);
        }

        internal override void Detach(Control screen)
        {
            if (Owner._restUnclickable == null) return;
            foreach ((ulong id, bool was) in _suppressed)
                if (GodotObject.InstanceFromId(id) is Control button && GodotObject.IsInstanceValid(button))
                    Owner._restUnclickable.SetValue(button, was);
            _suppressed.Clear();
        }

        /// <summary>The confirming tap: the button is clickable again, and its own release is what takes the option.</summary>
        internal override void Commit(Control screen, Control item)
        {
            Restore(screen, item);
            item.Call("OnRelease");
        }

        /// <summary>A button the game disabled for its fade, or an option it turned off, cannot be confirmed.</summary>
        internal override bool CanConfirm(Control screen, Control item)
        {
            if (Owner.Prop(item, "IsEnabled") is not true) return false;
            object? option = Owner.Prop(item, "Option");
            return option == null || Owner.Prop(option, "IsEnabled") is true;
        }
    }

    // ---- Shared item knowledge --------------------------------------------------------------------------------------------

    /// <summary>A property of a game object, remembered by its runtime type.</summary>
    internal object? Prop(object target, string name)
    {
        Type type = target.GetType();
        (Type, string) key = (type, name);
        if (!_props.TryGetValue(key, out PropertyInfo? property))
            _props[key] = property = type.GetProperty(name, InstAll);
        return property?.GetValue(target);
    }

    /// <summary>A one-argument method of a screen (the native commit and inspect paths), remembered per type.</summary>
    private MethodInfo? ScreenMethod(Control screen, string name)
    {
        (Type, string) key = (screen.GetType(), name);
        if (_methods.TryGetValue(key, out MethodInfo? known)) return known;
        MethodInfo? found = screen.GetType().GetMethods(InstAll).FirstOrDefault(m => m.Name == name && m.GetParameters().Length == 1);
        _methods[key] = found;
        return found;
    }

    internal bool CallScreen(Control screen, string name, Control item)
    {
        MethodInfo? method = ScreenMethod(screen, name);
        if (method == null)
        {
            GD.PrintErr($"Phone select confirm: {screen.GetType().Name}.{name} not found.");
            return false;
        }
        method.Invoke(screen, [item]);
        return true;
    }

    private MethodInfo? SlotTip(Control slot)
    {
        Type type = slot.GetType();
        if (!_slotTips.TryGetValue(type, out MethodInfo? method))
            _slotTips[type] = method = type.GetMethod("CreateHoverTip", InstAll, Type.EmptyTypes);
        return method;
    }

    private MethodInfo? SlotPreview(Control slot)
    {
        Type type = slot.GetType();
        if (!_slotPreviews.TryGetValue(type, out MethodInfo? method))
            _slotPreviews[type] = method = type.GetMethod("OnPreview", InstAll, Type.EmptyTypes);
        return method;
    }

    internal object? CardModel(Control holder) => Prop(holder, "CardModel");

    /// <summary>The relic node of a choice holder or chest holder.</summary>
    internal Control? RelicNode(Control item)
    {
        if (Prop(item, "Relic") is Control relic) return relic;
        if (item.GetType().Name == "NRelic") return item;
        foreach (Node child in item.GetChildren())
            if (child is Control control && control.GetType().Name == "NRelic") return control;
        return Prop(item, "Model") != null ? item : null;
    }

    /// <summary>The relic's model (the selection's identity, its tips and its inspection).</summary>
    internal object? RelicModel(Control item)
    {
        Control? relic = RelicNode(item);
        return relic == null ? null : Prop(relic, "Model");
    }

    /// <summary>A card holder hovered (the game's enlarged look and its keyword tips), every other card in its row not.</summary>
    internal void HoverOnly(Control pressed)
    {
        if (_isHovered == null || pressed.GetParent() is not Node row) return;
        foreach (Node child in row.GetChildren())
        {
            if (child is not Control holder || holder.GetNodeOrNull<Control>("%Hitbox") is not { } hitbox) continue;
            bool hovered = _isHovered.GetValue(holder) is true;
            if (holder == pressed)
            {
                if (!hovered) hitbox.EmitSignal("Focused", hitbox);
            }
            else if (hovered)
            {
                hitbox.EmitSignal("Unfocused", hitbox);
            }
        }
    }

    /// <summary>The pressed card lets go of the press the game would take on: its own release takes it.</summary>
    internal void ClearCardPress(Control holder) => PhoneSwipeRelease.Shared.ReleaseHolder(holder);

    /// <summary>A relic or chest holder lets go of its press, so its own release takes nothing.</summary>
    internal void ClearPress(Control item) => PhoneSwipeRelease.Shared.ReleasePress(item);

    internal void ShowCardTip(Control holder)
    {
        object? model = CardModel(holder);
        if (model != null) ShowTip(holder, model, cardHolder: true);
    }

    internal void ShowRelicTip(Control item)
    {
        Control? relic = RelicNode(item);
        object? model = relic == null ? null : Prop(relic, "Model");
        if (model != null) ShowTip(relic ?? item, model, cardHolder: false);
    }

    private void ShowTip(Control owner, object model, bool cardHolder)
    {
        if (_createTips == null || _tipNone == null) return;
        object? tips = Prop(model, "HoverTips");
        if (tips == null) return;
        object? set = _createTips.Invoke(null, [owner, tips, _tipNone]);
        if (set == null) return;
        if (cardHolder) _alignCardHolder?.Invoke(set, [owner]);
        else if (_alignRelic != null && owner.GetType().Name == "NRelic") _alignRelic.Invoke(set, [owner]);
    }

    internal void ClearCardTip(Control holder)
    {
        HideTip(holder);
        if (_isHovered?.GetValue(holder) is true && holder.GetNodeOrNull<Control>("%Hitbox") is { } hitbox)
            hitbox.EmitSignal("Unfocused", hitbox);
    }

    /// <summary>
    /// Takes down the tip a selection put up. The game keys each tip set by the control it is anchored to, and a relic's tip
    /// is anchored to the relic node inside the holder (its alignment is measured from it), while selections are dropped by
    /// the holder: both owners are cleared, or the relic's tip would outlive the selection.
    /// </summary>
    internal void HideTip(Control owner)
    {
        if (_removeTips == null || !GodotObject.IsInstanceValid(owner)) return;
        _removeTips.Invoke(null, [owner]);
        if (RelicNode(owner) is { } relic && !ReferenceEquals(relic, owner) && GodotObject.IsInstanceValid(relic))
            _removeTips.Invoke(null, [relic]);
    }

    /// <summary>The relic's own inspection screen, as a hold on a relic opens it.</summary>
    internal void OpenRelicInspect(Control item)
    {
        Control? relic = RelicNode(item);
        object? model = relic == null ? null : Prop(relic, "Model");
        if (model == null) return;
        PropertyInfo? instance = typeof(NGame).GetProperty("Instance", StaticAll);
        if (instance?.GetValue(null) is not NGame game) return;
        MethodInfo? getScreen = typeof(NGame).GetMethod("GetInspectRelicScreen", InstAll, Type.EmptyTypes);
        if (getScreen?.Invoke(game, null) is not Node inspect) return;
        Type listType = typeof(List<>).MakeGenericType(model.GetType());
        var list = (IList)Activator.CreateInstance(listType)!;
        list.Add(model);
        foreach (MethodInfo method in inspect.GetType().GetMethods(InstAll))
        {
            if (method.Name != "Open") continue;
            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length != 2 || !parameters[0].ParameterType.IsInstanceOfType(list) || !parameters[1].ParameterType.IsInstanceOfType(model))
                continue;
            method.Invoke(inspect, [list, model]);
            return;
        }
        GD.PrintErr("Phone select confirm: the relic inspection screen has no Open method for a relic.");
    }

    // ---- Placement helpers ------------------------------------------------------------------------------------------------

    /// <summary>The touch's point on the portrait canvas (the finger is the authority, not the engine's emulated mouse).</summary>
    private static Vector2 ToCanvas(Vector2 windowPosition)
    {
        if (Engine.GetMainLoop() is not SceneTree tree || !GodotObject.IsInstanceValid(tree.Root)) return windowPosition;
        return tree.Root.GetFinalTransform().AffineInverse() * windowPosition;
    }

    /// <summary>
    /// Adds the game's own selection reticle (<c>selection_reticle.tscn</c>: four gold corners and their shadow) as a child of
    /// <paramref name="owner" />, covering <paramref name="rect" /> in the owner's coordinates, and selects it. The scene anchors
    /// its corners 10 units outside the root's rectangle, so the corners sit just outside the item, as they do on the game's
    /// reward buttons. It is the last child, so it draws above the item, and it takes no touch input.
    /// </summary>
    internal static Control AttachReticle(Control owner, Rect2 rect)
    {
        PackedScene scene = _reticleScene ??= ResourceLoader.Load<PackedScene>(ReticleScenePath) ??
            throw new InvalidOperationException($"The game's selection reticle is missing ({ReticleScenePath}).");
        if (scene.Instantiate() is not Control reticle)
            throw new InvalidOperationException($"The game's selection reticle is not a control ({ReticleScenePath}).");
        reticle.Name = ReticleName;
        reticle.MouseFilter = Control.MouseFilterEnum.Ignore;
        reticle.Position = rect.Position;
        reticle.Size = rect.Size;
        // The node enters the tree here, so its _Ready (transparent, pivot at the rectangle's middle) runs before OnSelect.
        owner.AddChild(reticle);
        if (!reticle.HasMethod("OnSelect"))
            throw new InvalidOperationException("The game's selection reticle has no OnSelect.");
        reticle.Call("OnSelect");
        return reticle;
    }

    /// <summary>
    /// Fades a reticle out through the game's own OnDeselect and frees it once that 0.2 s fade has run, so it never pops off.
    /// If the item or screen is already gone there is nothing to see: the reticle is freed at once (the item would free it
    /// with itself either way).
    /// </summary>
    internal static void DeselectReticle(Control reticle, Control owner)
    {
        if (!GodotObject.IsInstanceValid(reticle)) return;
        if (!GodotObject.IsInstanceValid(owner) || !owner.IsInsideTree() || owner.IsQueuedForDeletion() || !reticle.IsInsideTree())
        {
            reticle.QueueFree();
            return;
        }
        if (reticle.HasMethod("OnDeselect")) reticle.Call("OnDeselect");
        SceneTreeTimer? fade = reticle.GetTree()?.CreateTimer(ReticleFadeSeconds);
        if (fade == null)
        {
            reticle.QueueFree();
            return;
        }
        fade.Timeout += () =>
        {
            if (GodotObject.IsInstanceValid(reticle)) reticle.QueueFree();
        };
    }

    /// <summary>A card holder's card: the 300 x 422 rectangle its hitbox covers, about the holder's middle.</summary>
    internal static Rect2 CardRect(Control holder) =>
        holder.GetNodeOrNull<Control>("%Hitbox") is { } hitbox
            ? new Rect2(hitbox.Position, hitbox.Size)
            : new Rect2(-150f, -211f, 300f, 422f);

    /// <summary>
    /// The local player's relic picks left in the open chest, read from the game's <c>TreasureRoomRelicSynchronizer</c>:
    /// one per round. v0.113 counts the rounds left (<c>_roundsLeft</c>); later builds count rounds and the current round
    /// (<c>_rounds</c>, <c>_currentRound</c>). Null with other players in the run (a relic can then be contested) or when
    /// neither form is found.
    /// </summary>
    private static class RelicPicks
    {
        internal static int? LocalPicksLeft()
        {
            try
            {
                Type? manager = typeof(NGame).Assembly.GetType("MegaCrit.Sts2.Core.Runs.RunManager");
                object? instance = manager?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                object? sync = instance?.GetType().GetProperty("TreasureRoomRelicSynchronizer", InstAll)?.GetValue(instance);
                if (sync == null) return null;
                Type type = sync.GetType();
                object? state = type.GetField("_runState", InstAll)?.GetValue(sync);
                if (state?.GetType().GetProperty("Players", InstAll)?.GetValue(state) is not IEnumerable players) return null;
                int count = 0;
                foreach (object? _ in players) count++;
                if (count != 1) return null;
                if (type.GetField("_roundsLeft", InstAll)?.GetValue(sync) is int left) return left;
                if (type.GetField("_rounds", InstAll)?.GetValue(sync) is int rounds &&
                    type.GetField("_currentRound", InstAll)?.GetValue(sync) is int current)
                    return rounds - current;
                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
