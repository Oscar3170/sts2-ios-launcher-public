using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using STS2Mobile.Platform;

namespace STS2Mobile.GameHost;

/// <summary>
/// The treasure room in portrait: the act's rest-site art behind the room, the chest and its gold burst framed around the
/// middle of the screen, the banner under the relic strip, the relic holders on three columns above the chest — with the
/// collection's own entrance animation still ending where the grid puts each holder — and the labels for an empty chest and a
/// fought-over relic. The game's own values are captured before the first move and put back when Portrait Mode is turned off.
/// </summary>
internal sealed partial class PortraitBetweenLayout
{
    public const string TreasureRoomTypeName = "MegaCrit.Sts2.Core.Nodes.Rooms.NTreasureRoom";

    /// <summary>The act art behind the room; named so a later layout pass can find it again.</summary>
    public const string TreasureArtName = "PortraitTreasureArt";

    private static readonly Color TreasureArtTint = new(0.68f, 0.66f, 0.72f);

    /// <summary>One treasure room's composition, its cached nodes and the game's own values to put back.</summary>
    private sealed class TreasureState
    {
        public ulong Chest, Gold, Floor, Banner, Collection, Empty, Fight;
        public Vector2 Canvas;
        public float Cap;
        public bool Built, ArtDone, GoldTweaked;

        // The game's banner measures where it shows from the window, so the portrait place is re-asserted whenever it drifts.
        public Vector2 BannerTop, BannerTarget, BannerShow, BannerHide;
        public FieldInfo? ShowField, HideField, TweenField;
        public Color ChestModulate;
        public Material? GoldMaterial;
        public Vector2 GoldPosition, FloorPosition;
    }

    private readonly HashSet<ulong> _treasures = new();
    private readonly Dictionary<ulong, TreasureState> _treasureStates = new();

    // ---- hooks ----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The room is added with all its children built but before its <c>_Ready</c>: the act art goes in behind everything now,
    /// so the first frame the room fades in over already has the environment instead of black bands. Returns whether it added
    /// the cover.
    /// </summary>
    private bool BeginTreasureRoom(Control room)
    {
        ulong id = room.GetInstanceId();
        if (!_treasureStates.TryGetValue(id, out TreasureState? state))
        {
            _treasureStates[id] = state = new TreasureState();
            _treasures.Add(id);
        }
        return EnsureTreasureArt(room, room.GetViewportRect().Size);
    }

    /// <summary>The room's composition; cheap to call again, it only rebuilds when the canvas or the cap changes.</summary>
    private void UpdateTreasurePortrait(Control room, float capHeight, Vector2 canvas)
    {
        ulong id = room.GetInstanceId();
        if (!_treasureStates.TryGetValue(id, out TreasureState? state))
        {
            _treasureStates[id] = state = new TreasureState();
            _treasures.Add(id);
        }
        if (state.Built && state.Canvas.IsEqualApprox(canvas) && Math.Abs(state.Cap - capHeight) < 0.01f)
        {
            // The banner recomputes where it shows and where it slides from whenever the window changes; put the portrait
            // place back when it did, and start its slide again if one was under way.
            if (GodotObject.InstanceFromId(state.Banner) is Control banner && GodotObject.IsInstanceValid(banner) &&
                banner.IsNodeReady() && TreasureBannerDrifted(banner, state))
                PlaceTreasureBanner(banner, state);
            if (!state.ArtDone) state.ArtDone = EnsureTreasureArt(room, canvas);
            return;
        }
        BuildTreasureRoom(room, capHeight, canvas, state);
    }

    private void RestoreTreasureLandscape()
    {
        foreach (ulong id in _treasures)
        {
            if (GodotObject.InstanceFromId(id) is Control room && GodotObject.IsInstanceValid(room) &&
                room.GetNodeOrNull<TextureRect>(TreasureArtName) is { } art)
            {
                art.Visible = false;
                art.QueueFree();
            }
            if (!_treasureStates.TryGetValue(id, out TreasureState? state)) continue;
            // The banner's place and slide targets are its own; the snapshot restores where it was laid out.
            if (GodotObject.InstanceFromId(state.Banner) is Control banner && GodotObject.IsInstanceValid(banner))
            {
                state.ShowField?.SetValue(banner, state.BannerShow);
                state.HideField?.SetValue(banner, state.BannerHide);
            }
            if (GodotObject.InstanceFromId(state.Chest) is Control chest && GodotObject.IsInstanceValid(chest))
                chest.Modulate = state.ChestModulate;
            if (GodotObject.InstanceFromId(state.Gold) is GpuParticles2D gold && GodotObject.IsInstanceValid(gold))
            {
                gold.Position = state.GoldPosition;
                if (state.GoldMaterial != null) gold.ProcessMaterial = state.GoldMaterial;
            }
            if (GodotObject.InstanceFromId(state.Floor) is Node2D floor && GodotObject.IsInstanceValid(floor))
                floor.Position = state.FloorPosition;
        }
        _treasures.Clear();
        _treasureStates.Clear();
    }

    private void PruneTreasureInstances()
    {
        _treasures.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        foreach (ulong id in _treasureStates.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList())
            _treasureStates.Remove(id);
    }

    // ---- composition ----------------------------------------------------------------------------------------------------

    private bool BuildTreasureRoom(Control room, float capHeight, Vector2 canvas, TreasureState state)
    {
        if (!CaptureTreasureNatives(room, state)) return false;
        if (GodotObject.InstanceFromId(state.Chest) is not Control chest ||
            GodotObject.InstanceFromId(state.Gold) is not GpuParticles2D gold ||
            GodotObject.InstanceFromId(state.Floor) is not Node2D floor ||
            GodotObject.InstanceFromId(state.Banner) is not Control banner ||
            GodotObject.InstanceFromId(state.Collection) is not Control collection ||
            GodotObject.InstanceFromId(state.Empty) is not Control empty ||
            GodotObject.InstanceFromId(state.Fight) is not Control fight)
            return false;
        // The banner is measured once its own _Ready has run; before that its size is unknown and it has no show place yet.
        if (!banner.IsNodeReady() || banner.Size.X < 1f) return false;

        TreasureScene scene = PortraitTreasureMath.Plan(
            canvas.X, canvas.Y, PortraitBands.HeaderBottom(capHeight), PortraitBands.RelicBandBottom(capHeight),
            banner.Size.X, empty.Size.X, fight.Size.X);

        // The chest: its sprite covers the canvas and its mouth is the gold burst's own place, so the vines and the open lid
        // overhang the control evenly instead of the authored absolute burst landing above the chest on a tall canvas.
        Capture(chest, captureVisible: false);
        PlaceTreasureControl(chest, new Vector2(scene.ChestX, scene.ChestY), scene.ChestScale);
        chest.Modulate = new Color(
            PortraitTreasureMath.ChestTint, PortraitTreasureMath.ChestTint, PortraitTreasureMath.ChestTint, chest.Modulate.A);
        gold.Position = new Vector2(scene.GoldX, scene.GoldY);
        floor.Position = new Vector2(scene.FloorX, scene.FloorY);
        if (!state.GoldTweaked && gold.ProcessMaterial is ParticleProcessMaterial native)
        {
            // A copy: the coins keep their bounce and sprites, but the burst narrows to the portrait chest instead of the
            // full-width one and stays off the screen edges.
            var burst = (ParticleProcessMaterial)native.Duplicate();
            burst.EmissionShapeScale = new Vector3(1.2f, 0.72f, 1f);
            burst.InitialVelocityMax *= 0.82f;
            burst.Spread *= 0.82f;
            gold.ProcessMaterial = burst;
            state.GoldTweaked = true;
        }

        Capture(banner, captureVisible: false);
        PlaceTreasureControl(banner, new Vector2(scene.BannerX, scene.BannerY), scene.BannerScale);
        state.BannerTop = banner.Position;
        state.BannerTarget = banner.GlobalPosition;
        PlaceTreasureBanner(banner, state);

        // The collection is the holders' coordinate space: anchored to the canvas's top-left at its origin, a holder's own
        // position is a canvas position.
        Capture(collection, captureVisible: false);
        PlaceTreasureControl(collection, Vector2.Zero, 1f);
        float factor = PortraitTreasureMath.HolderFactor(canvas.X);
        float step = PortraitTreasureMath.HolderStep(factor);
        float box = PortraitTreasureMath.HolderBox * factor;
        float bannerBottom = scene.BannerY + banner.Size.Y * scene.BannerScale;
        int holders = 0;
        if (collection.GetNodeOrNull<Control>("Container") is { } container)
        {
            foreach (Control layout in container.GetChildren().OfType<Control>())
            {
                List<Control> row = layout.GetChildren().OfType<Control>().Where(h => !h.IsQueuedForDeletion()).ToList();
                if (row.Count == 0) continue;
                float firstRow = PortraitTreasureMath.FirstHolderRow(
                    scene.MouthY, row.Count, step, factor, bannerBottom);
                IReadOnlyList<TreasureHolderSlot> slots =
                    PortraitTreasureMath.HolderSlots(row.Count, canvas.X * 0.5f, firstRow, step);
                for (int i = 0; i < row.Count && i < slots.Count; i++)
                {
                    Capture(row[i], captureVisible: false);
                    PlaceTreasureControl(
                        row[i],
                        new Vector2(slots[i].CenterX - box * 0.5f, slots[i].CenterY - box * 0.5f),
                        factor,
                        new Vector2(PortraitTreasureMath.HolderBox, PortraitTreasureMath.HolderBox));
                }
                holders = Math.Max(holders, row.Count);
            }
        }

        // The two labels keep their own text and fades; only their frame and size change.
        Capture(empty, captureVisible: false);
        PlaceTreasureControl(empty, new Vector2(scene.EmptyX, scene.EmptyY), scene.EmptyScale);
        Capture(fight, captureVisible: false);
        PlaceTreasureControl(fight, new Vector2(scene.FightX, scene.FightY), scene.FightScale);

        state.ArtDone = EnsureTreasureArt(room, canvas);
        state.Canvas = canvas;
        state.Cap = capHeight;
        state.Built = true;
        GD.Print($"Portrait between layout: treasure room, mouth {scene.MouthY:F0}, chest {800f * scene.ChestScale:F0} wide, " +
                 $"{holders} relic holders at {factor:F2} ({PortraitTreasureMath.HolderBox * factor:F0} units).");
        return true;
    }

    /// <summary>The game's own chest, burst, banner and holder values, read before anything moves.</summary>
    private static bool CaptureTreasureNatives(Control room, TreasureState state)
    {
        if (state.Chest != 0) return true;
        if (room.GetNodeOrNull<Control>("%Chest") is not { } chest ||
            room.GetNodeOrNull<GpuParticles2D>("%GoldExplosion") is not { } gold ||
            room.GetNodeOrNull<Node2D>("GoldExplosionCollider") is not { } floor ||
            room.GetNodeOrNull<Control>("%Banner") is not { } banner ||
            room.GetNodeOrNull<Control>("%RelicCollection") is not { } collection ||
            room.GetNodeOrNull<Control>("%EmptyLabel") is not { } empty ||
            room.GetNodeOrNull<Control>("%FightLabel") is not { } fight)
            return false;

        state.Chest = chest.GetInstanceId();
        state.Gold = gold.GetInstanceId();
        state.Floor = floor.GetInstanceId();
        state.Banner = banner.GetInstanceId();
        state.Collection = collection.GetInstanceId();
        state.Empty = empty.GetInstanceId();
        state.Fight = fight.GetInstanceId();

        state.ChestModulate = chest.Modulate;
        state.GoldPosition = gold.Position;
        state.FloorPosition = floor.Position;
        state.GoldMaterial = gold.ProcessMaterial;

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        Type type = banner.GetType();
        state.ShowField = type.GetField("_showPos", flags);
        state.HideField = type.GetField("_hidePos", flags);
        state.TweenField = type.GetField("_tween", flags);
        if (state.ShowField?.GetValue(banner) is Vector2 show) state.BannerShow = show;
        if (state.HideField?.GetValue(banner) is Vector2 hide) state.BannerHide = hide;
        return true;
    }

    /// <summary>
    /// Moves a control to a canvas position at a scale, keeping its own size (or giving it the shared holder box). The game's
    /// controls are anchored to their parents' middles, which a taller canvas would move; anchoring them to the top-left makes
    /// the position the canvas position the composition works in.
    /// </summary>
    private static void PlaceTreasureControl(Control control, Vector2 position, float scale, Vector2? size = null)
    {
        Vector2 kept = size ?? control.Size;
        control.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopLeft);
        control.PivotOffset = Vector2.Zero;
        control.Size = kept;
        control.Scale = new Vector2(scale, scale);
        control.Position = position;
    }

    /// <summary>Whether the banner has gone back to the game's own place (it measures it from the window's size).</summary>
    private static bool TreasureBannerDrifted(Control banner, TreasureState state)
    {
        if (state.ShowField?.GetValue(banner) is Vector2 show && show.DistanceSquaredTo(state.BannerTarget) > 0.25f) return true;
        bool sliding = state.TweenField?.GetValue(banner) is Tween { } tween && tween.IsValid() && tween.IsRunning();
        return !sliding && banner.Position.DistanceSquaredTo(state.BannerTop) > 0.25f;
    }

    /// <summary>Writes the banner's portrait place as both where it shows and where it slides from, and starts that slide.</summary>
    private static void PlaceTreasureBanner(Control banner, TreasureState state)
    {
        state.ShowField?.SetValue(banner, state.BannerTarget);
        state.HideField?.SetValue(banner, state.BannerTarget + new Vector2(0f, PortraitTreasureMath.BannerHideDrop));
        if (state.TweenField?.GetValue(banner) is Tween { } tween && tween.IsValid() && tween.IsRunning())
            banner.Call("AnimateIn");
        else
            banner.Position = state.BannerTop;
    }

    /// <summary>
    /// The act's rest-site art behind the room, dimmed, so the bands the native room's black backdrop cannot fill are the
    /// environment. Under every control the room draws (the game's own black rectangle stays beneath it). Returns whether the
    /// art has its picture: the act is read from the run, so the caller retries until it is known.
    /// </summary>
    private static bool EnsureTreasureArt(Control room, Vector2 canvas)
    {
        TextureRect? art = room.GetNodeOrNull<TextureRect>(TreasureArtName);
        if (art == null)
        {
            if (ActRestTexture() is not { } texture) return false;
            art = new TextureRect
            {
                Name = TreasureArtName,
                Texture = texture,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                Modulate = TreasureArtTint,
                Position = Vector2.Zero,
                Size = canvas,
            };
            room.AddChild(art);
            if (room.GetNodeOrNull<Control>("ColorRect") is { } backdrop) room.MoveChild(art, backdrop.GetIndex() + 1);
        }
        else if (art.Texture == null && ActRestTexture() is { } late)
        {
            art.Texture = late;
        }
        if (!art.Visible) art.Visible = true;
        if (!art.Size.IsEqualApprox(canvas))
        {
            art.Position = Vector2.Zero;
            art.Size = canvas;
        }
        return art.Texture != null;
    }
}
