using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using STS2Mobile.Platform;

namespace STS2Mobile.GameHost;

/// <summary>
/// The scale a portrait card grid draws its cards at. The game's holders rest at <c>SmallScale</c> (0.8) and hover at
/// <c>HoverScale</c> (1.0); a portrait grid's three columns need another size, and correcting the scale after the game each
/// frame lost the race to the frame drawn on the phone (a card shrank for a frame as the next touch landed). The patched game
/// asks <see cref="CardHolderScaleGate"/> for both, so its own hover, unhover and row assignment use the column size. The game
/// sets a new holder's scale before adding it to the grid, when the holder cannot yet say which grid it is in, so the grid's
/// scroll content gives each holder that enters it the column size at once, inside the add.
/// <para>A layout <see cref="Track"/>s the scroll content of each grid it sizes and <see cref="Untrack"/>s it on the way back
/// to landscape.</para>
/// </summary>
internal static class PortraitGridHolderScale
{
    private const string GridHolderTypeName = "MegaCrit.Sts2.Core.Nodes.Cards.Holders.NGridCardHolder";

    /// <summary>One tracked grid's scroll content: the scale its cards are drawn at, and whether holders entered since asked.</summary>
    private sealed class Entry
    {
        public float Scale;
        public bool Entered;
        public Callable OnEntered;
    }

    private static readonly Dictionary<ulong, Entry> Scrolls = new();
    private static Type? _holderType;
    private static float _nativeScale;

    public static void Attach() => CardHolderScaleGate.Scale = ScaleFor;

    public static void Detach() => CardHolderScaleGate.Scale = null;

    /// <summary>The gate: a holder in a tracked grid's scroll content takes its scale; anything else the game's own.</summary>
    private static float ScaleFor(object holder) =>
        holder is Control control && control.GetParent() is Node scroll &&
        Scrolls.TryGetValue(scroll.GetInstanceId(), out Entry? entry) ? entry.Scale : 0f;

    /// <summary>
    /// Draws the grid's cards at <paramref name="scale"/>: every holder now, every holder added later as it enters, and every
    /// hover and unhover through the gate. Cheap when nothing changed (one lookup and a compare).
    /// </summary>
    public static void Track(Control scroll, float scale)
    {
        Resolve();
        ulong id = scroll.GetInstanceId();
        if (Scrolls.TryGetValue(id, out Entry? entry))
        {
            if (Math.Abs(entry.Scale - scale) < 0.0005f) return;
            entry.Scale = scale;
        }
        else
        {
            Entry tracked = entry = new Entry { Scale = scale };
            entry.OnEntered = Callable.From((Node child) =>
            {
                if (child is not Control holder || holder.GetType() != _holderType) return;
                tracked.Entered = true;
                SetScale(holder, tracked.Scale);
            });
            scroll.Connect(Node.SignalName.ChildEnteredTree, entry.OnEntered);
            Scrolls[id] = entry;
        }
        SetAll(scroll, scale);
    }

    /// <summary>Whether holders entered the grid since the last ask (the grid built its rows again), and forgets it.</summary>
    public static bool TakeRebuilt(Control scroll)
    {
        if (!Scrolls.TryGetValue(scroll.GetInstanceId(), out Entry? entry) || !entry.Entered) return false;
        entry.Entered = false;
        return true;
    }

    /// <summary>Gives the grid's cards the game's own scale again and stops sizing them.</summary>
    public static void Untrack(Control scroll)
    {
        if (!Scrolls.Remove(scroll.GetInstanceId(), out Entry? entry)) return;
        if (scroll.IsConnected(Node.SignalName.ChildEnteredTree, entry.OnEntered))
            scroll.Disconnect(Node.SignalName.ChildEnteredTree, entry.OnEntered);
        SetAll(scroll, _nativeScale);
    }

    public static void PruneDead()
    {
        List<ulong>? dead = null;
        foreach (ulong id in Scrolls.Keys)
            if (!GodotObject.IsInstanceIdValid(id)) (dead ??= new List<ulong>()).Add(id);
        if (dead != null)
            foreach (ulong id in dead) Scrolls.Remove(id);
    }

    private static void SetAll(Control scroll, float scale)
    {
        int count = scroll.GetChildCount();
        for (int i = 0; i < count; i++)
            if (scroll.GetChild(i) is Control holder && holder.GetType() == _holderType) SetScale(holder, scale);
    }

    private static void SetScale(Control holder, float scale)
    {
        if (Math.Abs(holder.Scale.X - scale) >= 0.0005f || Math.Abs(holder.Scale.Y - scale) >= 0.0005f)
            holder.Scale = new Vector2(scale, scale);
    }

    /// <summary>The grid holder type and NCardHolder.smallScale, the game's own resting scale (0.8 at every size it ships).</summary>
    private static void Resolve()
    {
        if (_holderType != null) return;
        _holderType = typeof(NGame).Assembly.GetType(GridHolderTypeName);
        _nativeScale = 0.8f;
        for (Type? level = _holderType; level != null && level != typeof(object); level = level.BaseType)
        {
            if (level.GetField("smallScale", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly) is not { } field) continue;
            if (field.GetValue(null) is Vector2 native && native.X > 0.01f) _nativeScale = native.X;
            break;
        }
    }
}
