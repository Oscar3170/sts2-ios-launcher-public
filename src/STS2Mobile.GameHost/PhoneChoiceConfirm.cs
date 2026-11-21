using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;

namespace STS2Mobile.GameHost;

/// <summary>
/// The simple and combat pile card grids, on screens that open in portrait: when the game would close as soon as enough cards
/// are picked, it waits for its own confirm button instead, and a tap on another card while the selection is full moves it.
/// Bundles and the deck's select, upgrade, transform and enchant screens already preview the pick with a confirm button. The
/// single-item screens (shop, card rewards, choose-a-card, relic choice, treasure) use <see cref="PhoneSelectConfirm"/>; the
/// grids' own confirm buttons are unchanged. The game's types are reached by name, as the host compiles against a stub that
/// names only what it must.
/// </summary>
internal sealed class PhoneChoiceConfirm
{
    private const string Cs = "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.";
    private const string SimpleGridTypeName = Cs + "NSimpleCardSelectScreen";
    private const string CombatPileGridTypeName = Cs + "NCombatPileCardSelectScreen";
    private const BindingFlags InstAll = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private sealed class Grid
    {
        internal bool Required;
    }

    private readonly Dictionary<ulong, Grid> _grids = new();
    private readonly HashSet<ulong> _wired = new();
    private bool _failed;

    internal static PhoneChoiceConfirm? Instance { get; private set; }

    public PhoneChoiceConfirm() => Instance = this;

    /// <summary>Grids and their confirm waits now installed (for the proofs): 0 in a launch that stayed in landscape.</summary>
    internal static string TrackedForProof() =>
        Instance == null ? "none" : $"{Instance._grids.Count}+{Instance._wired.Count}grids";

    public void OnNodeAdded(Node node)
    {
        if (_failed) return;
        string? name = node.GetType().FullName;
        if (name != SimpleGridTypeName && name != CombatPileGridTypeName) return;
        if (node is not Control screen) return;
        ulong id = screen.GetInstanceId();
        if (_grids.ContainsKey(id)) return;
        _grids[id] = new Grid();
        screen.TreeExiting += () => _grids.Remove(id);
    }

    public void OnProcessFrame()
    {
        if (_failed || _grids.Count == 0) return;
        try
        {
            List<ulong>? dead = null;
            foreach ((ulong id, Grid grid) in _grids)
            {
                if (GodotObject.InstanceFromId(id) is not Control screen || !GodotObject.IsInstanceValid(screen) || !screen.IsInsideTree())
                {
                    (dead ??= new List<ulong>()).Add(id);
                    continue;
                }
                if (!grid.Required) grid.Required = RequireConfirm(screen);
                WireGrid(screen);
            }
            if (dead != null) foreach (ulong id in dead) _grids.Remove(id);
        }
        catch (Exception error)
        {
            Fail(error);
        }
    }

    private void Fail(Exception error)
    {
        _failed = true;
        GD.PrintErr("Phone choice confirm: turned off after an error: ", error);
    }

    /// <summary>The grid waits for its own confirm button rather than closing as soon as enough cards are picked.</summary>
    private static bool RequireConfirm(Control screen)
    {
        FieldInfo? prefsField = screen.GetType().GetField("_prefs", InstAll);
        if (prefsField?.GetValue(screen) is not { } prefs) return false;
        PropertyInfo? manual = prefs.GetType().GetProperty("RequireManualConfirmation", InstAll);
        if (manual == null || manual.GetValue(prefs) is true) return true;
        manual.SetValue(prefs, true);
        prefsField.SetValue(screen, prefs);
        GD.Print($"Phone choice confirm: {screen.GetType().Name} waits for its confirm button.");
        return true;
    }

    private void WireGrid(Control screen)
    {
        if (screen.GetType().GetField("_grid", InstAll)?.GetValue(screen) is not Control grid) return;
        ulong gridId = grid.GetInstanceId();
        if (!_wired.Add(gridId)) return;
        ulong screenId = screen.GetInstanceId();
        grid.Connect("HolderPressed", Callable.From((Control holder) => OnGridHolderPressed(screenId, holder)));
        grid.TreeExiting += () => _wired.Remove(gridId);
    }

    /// <summary>
    /// After the game's own handler: a card tapped while the selection is full (the game ignores it) replaces a selected card, so
    /// a second tap moves a one-card selection.
    /// </summary>
    private void OnGridHolderPressed(ulong screenId, Control holder)
    {
        if (_failed || GodotObject.InstanceFromId(screenId) is not Control screen || !GodotObject.IsInstanceValid(screen)) return;
        try
        {
            Type type = screen.GetType();
            object? card = holder.GetType().GetProperty("CardModel", InstAll)?.GetValue(holder);
            if (card == null || type.GetField("_selectedCards", InstAll)?.GetValue(screen) is not IEnumerable selected) return;
            List<object> picked = selected.Cast<object>().ToList();
            if (picked.Any(c => ReferenceEquals(c, card))) return;
            object? prefs = type.GetField("_prefs", InstAll)?.GetValue(screen);
            if (prefs?.GetType().GetProperty("MaxSelect", InstAll)?.GetValue(prefs) is not int max || max < 1 || picked.Count < max) return;
            MethodInfo? click = type.GetMethod("OnCardClicked", InstAll);
            if (click == null) return;
            click.Invoke(screen, [picked[^1]]);
            click.Invoke(screen, [card]);
            GD.Print($"Phone choice confirm: {type.Name} selection moved to {card.GetType().Name}.");
        }
        catch (Exception error)
        {
            Fail(error);
        }
    }
}
