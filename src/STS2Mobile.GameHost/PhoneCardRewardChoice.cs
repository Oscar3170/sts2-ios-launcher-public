using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using STS2Mobile.Platform;

namespace STS2Mobile.GameHost;

/// <summary>
/// Card rewards picked by a tap and then a second tap (<see cref="PhoneSelectConfirm"/>), on screens that open in portrait. On
/// the game's card reward screen a click takes a card and closes the screen, so on a phone a touch meant to read a card took
/// it. Here the first tap selects a card (its reticle, with its keyword tips), a tap on another card moves the selection, and a
/// second tap on the selected card takes it. Skip, Reroll and the other alternatives work as before.
/// <para>How: the screen answers the reward through its <c>_completionSource</c>. While the screen waits, that field holds a
/// stand-in, so an answer from anywhere (a card's press, an alternative, the keyboard) can be read here: a card's index
/// becomes a selection; an alternative's answer is handed to the game's own source at once. The second tap answers the game's
/// source itself. The game's types are reached by name (the host compiles against a stub that names only what it must).</para>
/// </summary>
internal sealed class PhoneCardRewardChoice
{
    private const string ScreenTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NCardRewardSelectionScreen";
    private const BindingFlags InstAll = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private readonly Dictionary<ulong, ScreenState> _screens = new();
    private readonly FieldInfo? _source;
    private readonly FieldInfo? _options;
    private bool _failed;
    private bool _wasPortrait;

    private sealed class ScreenState
    {
        public TaskCompletionSource<int?>? Real;
        public TaskCompletionSource<int?>? StandIn;
        public RewardAdapter? Adapter;
    }

    internal static PhoneCardRewardChoice? Instance { get; private set; }

    /// <summary>The selected card's index on a screen, or -1 (for the proofs).</summary>
    public static int SelectedIndex(Control screen)
    {
        if (Instance is not { } self || PhoneSelectConfirm.SelectedItemOn(screen) is not { } holder) return -1;
        return self.IndexOfHolder(screen, holder.GetInstanceId());
    }

    /// <summary>Card reward screens now under the two taps (for the proofs): 0 in a launch that stayed in landscape.</summary>
    internal static int TrackedForProof() => Instance?._screens.Count ?? 0;

    /// <summary>The option a card holder on the screen shows, or -1 (the portrait layout orders the holders by it).</summary>
    public static int OptionIndexOf(Control screen, Control holder) => Instance?.IndexOfHolder(screen, holder.GetInstanceId()) ?? -1;

    public PhoneCardRewardChoice()
    {
        Instance = this;
        Assembly sts2 = typeof(NGame).Assembly;
        Type? screenType = sts2.GetType(ScreenTypeName);
        _source = screenType?.GetField("_completionSource", InstAll);
        _options = screenType?.GetField("_options", InstAll);
        if (_source?.FieldType != typeof(TaskCompletionSource<int?>) || _options == null)
        {
            _failed = true;
            GD.PrintErr("Phone card reward choice: the game's card reward screen changed; one tap takes a card as in the game.");
        }
    }

    public void OnNodeAdded(Node node)
    {
        if (_failed || node.GetType().FullName != ScreenTypeName) return;
        ulong id = node.GetInstanceId();
        if (_screens.ContainsKey(id)) return;
        _screens[id] = new ScreenState();
        node.TreeExiting += () => OnScreenExiting(id);
    }

    public void OnProcessFrame()
    {
        if (_failed) return;
        try
        {
            // A reward screen opened in landscape is taken over when Portrait Mode is turned on over it (a rotation, or the
            // Graphics row): the two taps then work without the screen being reopened.
            bool portrait = SafeAreaCanvas.IsPortrait;
            if (portrait && !_wasPortrait && PhoneSelectConfirm.Installed) AdoptOpenScreens();
            _wasPortrait = portrait;
            if (_screens.Count == 0) return;
            List<ulong>? dead = null;
            foreach ((ulong id, ScreenState state) in _screens)
            {
                if (GodotObject.InstanceFromId(id) is not Control screen || !GodotObject.IsInstanceValid(screen))
                {
                    (dead ??= new List<ulong>()).Add(id);
                    continue;
                }
                if (screen.IsInsideTree()) Step(screen, state);
            }
            if (dead != null) foreach (ulong id in dead) _screens.Remove(id);
        }
        catch (Exception error)
        {
            _failed = true;
            GD.PrintErr("Phone card reward choice: turned off after an error: ", error);
        }
    }

    /// <summary>Registers every reward screen already open, once each (idempotent).</summary>
    private void AdoptOpenScreens()
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

    private void Step(Control screen, ScreenState state)
    {
        PhoneSelectConfirm? policy = PhoneSelectConfirm.Instance;
        // Landscape plays as the game does: the real source goes back, so a tap takes the card natively.
        if (policy is not { } select || !SafeAreaCanvas.IsPortrait || !PhoneSelectConfirm.Installed)
        {
            Restore(screen, state);
            return;
        }
        state.Adapter ??= new RewardAdapter(this, select, state);
        select.Track(screen, state.Adapter);

        // The game asks again for each pick (OptionSelected makes a new source): put a stand-in in front of each one.
        if (_source!.GetValue(screen) is TaskCompletionSource<int?> current && current != state.StandIn && !current.Task.IsCompleted)
        {
            // A new question (the first, or one after a reroll) ends any selection made on the old cards.
            PhoneSelectConfirm.ClearFor(screen, "card reward rebuilt");
            state.Real = current;
            state.StandIn = new TaskCompletionSource<int?>();
            _source.SetValue(screen, state.StandIn);
        }

        if (state.StandIn is { Task.IsCompleted: true } done && state.Real is { } real)
        {
            int? index = done.Task.IsCompletedSuccessfully ? done.Task.Result : null;
            int cards = (_options!.GetValue(screen) as ICollection)?.Count ?? 0;
            if (index is int card && card >= 0 && card < cards)
            {
                // An answer from elsewhere (the keyboard or controller): make it a selection, not a take.
                state.StandIn = new TaskCompletionSource<int?>();
                _source.SetValue(screen, state.StandIn);
                if (HolderAt(screen, card) is { } holder) select.SelectFrom(screen, holder);
            }
            else if (done.Task.IsCompletedSuccessfully)
            {
                // Skip, Reroll or another alternative: the game's own answer, at once.
                Answer(screen, state, real, index);
            }
        }
    }

    /// <summary>The second tap on the selected card: the game's own source answers with its option index.</summary>
    private bool Confirm(Control screen, ScreenState state, Control holder)
    {
        if (state.Real is not { Task.IsCompleted: false } real)
        {
            GD.PrintErr("Phone card reward choice: the reward is no longer waiting for a card.");
            return false;
        }
        int index = IndexOfHolder(screen, holder.GetInstanceId());
        if (index < 0)
        {
            GD.PrintErr("Phone card reward choice: the selected card is not one of the reward's options.");
            return false;
        }
        GD.Print($"Phone card reward choice: card {index + 1} taken with a second tap.");
        Answer(screen, state, real, index);
        return true;
    }

    private void Answer(Control screen, ScreenState state, TaskCompletionSource<int?> real, int? index)
    {
        state.StandIn = null;
        state.Real = null;
        _source!.SetValue(screen, real);
        real.TrySetResult(index);
    }

    /// <summary>Hands the screen back to the game: the real source, no stand-in and no selection.</summary>
    private void Restore(Control screen, ScreenState state)
    {
        if (_source!.GetValue(screen) is TaskCompletionSource<int?> current && current == state.StandIn &&
            state.Real is { Task.IsCompleted: false } real)
            _source.SetValue(screen, real);
        state.StandIn = null;
        state.Real = null;
    }

    private void OnScreenExiting(ulong id)
    {
        // Closed without an answer (the game cancels its own source in _ExitTree; here that is the stand-in): cancel the real one.
        if (_screens.TryGetValue(id, out ScreenState? state) && state.Real is { } real && !real.Task.IsCompleted)
            real.TrySetException(new TaskCanceledException());
        _screens.Remove(id);
    }

    /// <summary>
    /// The option a card holder shows, as the game counts it (its <c>_options</c>, matched by card). Not the holder's place in
    /// the row: the game moves a hovered card to the end of the row so it draws on top.
    /// </summary>
    private int IndexOfHolder(Control screen, ulong holderId)
    {
        if (GodotObject.InstanceFromId(holderId) is not Control holder || !GodotObject.IsInstanceValid(holder)) return -1;
        object? card = holder.GetType().GetProperty("CardModel", InstAll)?.GetValue(holder);
        if (card == null || _options!.GetValue(screen) is not IList options) return -1;
        for (int i = 0; i < options.Count; i++)
            if (ReferenceEquals(options[i]?.GetType().GetProperty("Card", InstAll)?.GetValue(options[i]), card)) return i;
        return -1;
    }

    private Control? HolderAt(Control screen, int index)
    {
        if (screen.GetNodeOrNull<Control>("UI/CardRow") is not { } row) return null;
        foreach (Node child in row.GetChildren())
            if (child is Control holder && !holder.IsQueuedForDeletion() && IndexOfHolder(screen, holder.GetInstanceId()) == index)
                return holder;
        return null;
    }

    /// <summary>The reward screen's cards in <see cref="PhoneSelectConfirm"/>: press, hold and second tap, answered by <see cref="Confirm"/>.</summary>
    private sealed class RewardAdapter(PhoneCardRewardChoice choice, PhoneSelectConfirm select, ScreenState state)
        : PhoneSelectConfirm.SelectAdapter(select)
    {
        internal override string Label => "card reward";
        internal override bool IsCard => true;
        internal override int ItemCount(Control owner) => Row(owner, "UI/CardRow")?.GetChildCount() ?? 0;
        internal override Control? ItemAt(Control owner, int index) => Row(owner, "UI/CardRow")?.GetChild(index) as Control;
        internal override Control PressTarget(Control item) => item.GetNodeOrNull<Control>("%Hitbox") ?? item;
        internal override Rect2 HitRect(Control owner, Control item) => PressTarget(item).GetGlobalRect();
        internal override object? Model(Control item) => Owner.Prop(item, "CardModel");

        internal override Control AttachReticle(Control item) => PhoneSelectConfirm.AttachReticle(item, PhoneSelectConfirm.CardRect(item));

        internal override void ShowTip(Control owner, Control item)
        {
            Owner.HoverOnly(item);
            Owner.ShowCardTip(item);
        }

        internal override void HideTip(Control owner, Control item) => Owner.ClearCardTip(item);

        internal override void Inspect(Control owner, Control item) => Owner.CallScreen(owner, "InspectCard", item);

        internal override void Commit(Control owner, Control item) => choice.Confirm(owner, state, item);

        internal override bool CanConfirm(Control owner, Control item) => state.Real is { Task.IsCompleted: false } && state.StandIn != null;

        internal override void Suppress(Control owner, Control item) => Owner.ClearCardPress(item);
    }
}
