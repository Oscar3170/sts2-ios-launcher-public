using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Nodes;

namespace STS2Mobile.GameHost;

/// <summary>
/// Lets go of the press under a swipe: the one place that knows how the game records one, for every surface where a scroll
/// and a tap share the screen (card grids and lists, a card screen's footer, the map, the loot list, the events, and the
/// select-then-confirm items when they take their own press down at touch down). A card holder records in
/// <c>_currentPressedAction</c> the press whose release picks or inspects it (<c>NCardHolder.OnMouseReleased</c> acts only
/// while it is set); a button records it in <c>_isPressed</c> (<c>NClickableControl.OnReleaseHandler</c> acts only while it
/// is set); a shop slot acts on the release unless <c>_ignoreMouseRelease</c> is set. Clearing those before the finger
/// lifts is what makes a swipe scroll rather than pick, select, toggle, buy or travel, and it is what the game's own
/// <c>_ignoreDragThreshold</c> does for the map's points and the shop's drag.
/// <para>
/// Nothing is put back: every one of these is written again by the next press (a holder's press action, a button's pressed
/// flag, a shop slot's <c>OnMousePressed</c> clears its ignore flag), and the game clears them itself on its own release.
/// One shared instance serves every surface, and it never runs per frame: a release happens once, when a press has become
/// a swipe.
/// </para>
/// </summary>
internal sealed class PhoneSwipeRelease
{
    private const BindingFlags InstAll = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const string HolderTypeName = "MegaCrit.Sts2.Core.Nodes.Cards.Holders.NCardHolder";
    private const string ClickableTypeName = "MegaCrit.Sts2.Core.Nodes.GodotExtensions.NClickableControl";
    private const string SlotTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.Shops.NMerchantSlot";

    private const byte Holder = 1;
    private const byte Clickable = 2;
    private const byte Slot = 4;

    /// <summary>How many presses swipes have let go since launch (for the proof runs and the harness).</summary>
    public static int Released { get; private set; }

    /// <summary>The one instance every surface uses; it is built on first use.</summary>
    public static PhoneSwipeRelease Shared { get; } = new();

    private readonly Type? _holderType, _clickableType, _slotType;
    private readonly FieldInfo? _holderPress, _clickablePressed, _slotIgnoreRelease;
    private readonly Dictionary<Type, byte> _kinds = new();
    private readonly Stack<Node> _walk = new(64);
    private readonly bool _ready;

    public PhoneSwipeRelease()
    {
        try
        {
            Assembly game = typeof(NGame).Assembly;
            _holderType = game.GetType(HolderTypeName);
            _clickableType = game.GetType(ClickableTypeName);
            _slotType = game.GetType(SlotTypeName);
            _holderPress = _holderType?.GetField("_currentPressedAction", InstAll);
            _clickablePressed = _clickableType?.GetField("_isPressed", InstAll);
            _slotIgnoreRelease = _slotType?.GetField("_ignoreMouseRelease", InstAll);
            _ready = _holderPress != null && _clickablePressed != null;
            if (!_ready)
                GD.PrintErr("Phone swipe release: the game's cards and buttons no longer record their press; a swipe lets go of nothing.");
        }
        catch (Exception error)
        {
            GD.PrintErr("Phone swipe release: off: ", error.Message);
        }
    }

    /// <summary>Whether the game's press records were found (for the proofs).</summary>
    public bool Ready => _ready;

    /// <summary>Lets go of every press in the tree under <paramref name="root"/>; returns how many were let go.</summary>
    public int Release(Node? root)
    {
        if (!_ready || root == null || !GodotObject.IsInstanceValid(root)) return 0;
        int released = 0;
        _walk.Clear();
        _walk.Push(root);
        while (_walk.Count > 0)
        {
            Node node = _walk.Pop();
            if (!GodotObject.IsInstanceValid(node)) continue;
            if (ReleaseOne(node)) released++;
            foreach (Node child in node.GetChildren()) _walk.Push(child);
        }
        return released;
    }

    /// <summary>Lets go of one node's own press; returns whether it had one to let go of.</summary>
    public bool ReleaseOne(Node node)
    {
        if (!_ready || node == null || !GodotObject.IsInstanceValid(node)) return false;
        byte kinds = Kinds(node.GetType());
        bool released = false;
        if ((kinds & Holder) != 0 && _holderPress!.GetValue(node) != null)
        {
            _holderPress.SetValue(node, null);
            released = true;
        }
        if ((kinds & Clickable) != 0 && _clickablePressed!.GetValue(node) is true)
        {
            _clickablePressed.SetValue(node, false);
            released = true;
        }
        // The slot acts on the release unless this is set; its own next press clears it again
        // (NMerchantSlot.OnMousePressed), so a swipe never buys and the next tap does.
        if ((kinds & Slot) != 0 && _slotIgnoreRelease?.GetValue(node) is false)
        {
            _slotIgnoreRelease.SetValue(node, true);
            released = true;
        }
        if (released) Released++;
        return released;
    }

    /// <summary>What a node is, by its type; remembered, as a card screen holds thousands of nodes.</summary>
    private byte Kinds(Type type)
    {
        if (_kinds.TryGetValue(type, out byte kinds)) return kinds;
        kinds = 0;
        if (_holderType != null && _holderType.IsAssignableFrom(type)) kinds |= Holder;
        if (_clickableType != null && _clickableType.IsAssignableFrom(type)) kinds |= Clickable;
        if (_slotType != null && _slotType.IsAssignableFrom(type)) kinds |= Slot;
        _kinds[type] = kinds;
        return kinds;
    }
}
