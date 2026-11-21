namespace STS2Mobile.Platform;

/// <summary>How a player's orb queue is arranged on the portrait canvas.</summary>
public enum PortraitOrbArcKind
{
    /// <summary>The game's own 125° arc above the owner, for one to three orbs.</summary>
    Native,

    /// <summary>One shallow arc with even horizontal spacing, for four to ten orbs.</summary>
    Single,
}

/// <summary>
/// One player's orb arc on the portrait canvas: where its anchor is, how far apart the orbs sit, and how large they and
/// their touch targets and value labels are drawn. Positions are canvas units on the 1080-wide portrait canvas (2.88
/// units are one point), native orb index 0 stays the rightmost/front orb and the last index the leftmost/back one.
/// </summary>
public readonly record struct PortraitOrbArcPlan(
    PortraitOrbArcKind Kind,
    int Count,
    float Radius,
    float Pitch,
    float Depth,
    float CenterX,
    float CenterY,
    float OrbWorldScale,
    float IconWorld,
    float TouchWorld,
    float ValueWorldHeight)
{
    /// <summary>Whether the orbs sit on the launcher's own single shallow arc.</summary>
    public bool IsSingle => Kind == PortraitOrbArcKind.Single;

    /// <summary>Half the horizontal distance from the first orb to the last (zero for the native arc).</summary>
    public float HalfWidth => Pitch * Math.Max(0, Count - 1) * 0.5f;

    /// <summary>
    /// Half the square an orb occupies on the canvas: the larger of its art and its invisible touch region (the scene's
    /// <c>Bounds</c>), which is what the harness measures and the finger presses. The layout keeps this whole square off
    /// the canvas edges, the orbit line and the rail, not just the smaller art inside it.
    /// </summary>
    public float BoxHalf => PortraitOrbMath.BoxHalfFor(IconWorld, TouchWorld);

    /// <summary>
    /// The centre of orb <paramref name="index"/> on the canvas. The native arc runs from −150° to −25° about the anchor,
    /// so its ends hang below its middle; the single arc keeps its ends on the anchor line and lifts the middle by
    /// <see cref="Depth"/>.
    /// </summary>
    public (float X, float Y) Position(int index)
    {
        int i = Math.Clamp(index, 0, Math.Max(0, Count - 1));
        if (Kind == PortraitOrbArcKind.Native)
        {
            float angle = Count == 1
                ? -90f
                : -87.5f - PortraitOrbMath.NativeSpread * 0.5f + i * PortraitOrbMath.NativeSpread / (Count - 1);
            float radians = angle * (MathF.PI / 180f);
            return (CenterX - MathF.Cos(radians) * Radius, CenterY + MathF.Sin(radians) * Radius);
        }
        float u = Count <= 1 ? 0f : 1f - 2f * i / (Count - 1f);
        return (CenterX + HalfWidth * u, CenterY - Depth * (1f - u * u));
    }

    /// <summary>
    /// The orb's drawn square on the canvas, as the harness measures it: its centre is the orb node's own place and its
    /// side is <see cref="BoxHalf"/> — the touch region the player's finger hits (the art sits inside it).
    /// </summary>
    public (float Left, float Top, float Right, float Bottom) IconBox(int index)
    {
        (float x, float y) = Position(index);
        float half = BoxHalf;
        return (x - half, y - half, x + half, y + half);
    }
}

/// <summary>
/// Pure portrait-mode orb geometry on the 1080-wide canvas, free of engine types so the arrangement rules are unit-tested.
/// The rules are the phone's version of TouchSpireUI's C4 arc: one to three orbs keep the game's tight native-radius arc;
/// four take one wide arc that leaves no gap between the art's edges; five to ten take one compact arc whose pitch is the
/// phone's overlap spacing, widened by the value labels and narrowed by an enemy lane and by the safe width. The orbit
/// top is the caller's: below the relic band on a tall phone, under the stone header at 9:16.
/// </summary>
public static class PortraitOrbMath
{
    /// <summary>The orb manager's own scale: the accepted phone size of an orb (the manager hangs off the body).</summary>
    public const float OrbWorldScale = 1.48f;

    /// <summary><c>NOrb._Ready</c> scales a local orb down by this before the manager's scale applies.</summary>
    public const float OrbLocalScale = 0.85f;

    /// <summary>The orb scene's <c>Bounds</c> side at scale 1: the icon's drawn size, and the touch target's base.</summary>
    public const float OrbBoundsUnits = 60f;

    /// <summary>The orb art's width at scale 1 (the figure the game's own orb art is measured with).</summary>
    public const float OrbArtUnits = 104f;

    /// <summary>The native arc's spread about the owner.</summary>
    public const float NativeSpread = 125f;

    /// <summary>
    /// The native arc's radius on the phone: a 44-point arc about the owner, so three orbs sit over the owner's head
    /// instead of reaching across the stage (the game's own handler grows it to 225–300 for a desktop canvas).
    /// </summary>
    public const float NativeRadius = 141f;

    /// <summary>The dense queue's spacing between neighbouring orbs: 15.3 points, the phone's overlapping "queue" pitch.</summary>
    public const float DensePitch = 44f;

    /// <summary>
    /// A four-orb arc's pitch: the orb art plus a gap, so the four icons never touch. Five or more orbs overlap by design,
    /// their value labels staying separate across the dense pitch.
    /// </summary>
    public const float WidePitch = OrbArtUnits * OrbWorldScale + 10f;

    /// <summary>
    /// A three-orb arc displaced by an enemy still gets the phone-sized spacing of an ordinary row (the native arc's own
    /// chord is tighter once its radius is the phone's).
    /// </summary>
    public const float ShallowWidePitch = 125f;

    /// <summary>The narrowest pitch a lane may squeeze the queue to before its labels would collide.</summary>
    public const float MinimumLanePitch = 28f;

    /// <summary>The gap a dense arc keeps between neighbouring value labels.</summary>
    public const float LabelPitchGap = 8f;

    /// <summary>How far the middle of a single arc rises above its ends: 29 points, so the queue reads as one arc.</summary>
    public const float SingleArcDepth = 84f;

    /// <summary>The room the arc leaves between its own centre range and the canvas's side edges.</summary>
    public const float SingleArcSide = 99f;

    /// <summary>
    /// The distance the safe width reserves for the arc's own ends: a dense queue is capped by
    /// <c>(canvasWidth − this) / (count − 1)</c> so the whole row stays inside the canvas's margins.
    /// </summary>
    public const float SingleArcPitchMargin = 198f;

    /// <summary>How far above the owner's hit-box top the single arc's ends sit.</summary>
    public const float SingleArcBodyRise = 106f;

    /// <summary>How far below the orbit top the single arc's anchor sits (its middle rises a further depth).</summary>
    public const float SingleArcOrbitGap = 152f;

    /// <summary>How far above the owner's hit-box top the native arc's centre sits.</summary>
    public const float NativeBodyRise = 45f;

    /// <summary>How far below the orbit top the native arc's centre sits at least (a radius plus this).</summary>
    public const float NativeOrbitGap = 80f;

    /// <summary>
    /// The gap every orb box keeps from the canvas edges: the 8 units the harness measures plus a half-unit cushion,
    /// because the box is placed from the manager's transform and re-measured through the canvas a frame later.
    /// </summary>
    public const float CanvasEdgeMargin = 8.5f;

    /// <summary>The value labels' drawn height: the 10-point floor the dense queue must never go under.</summary>
    public const float ValueWorldHeight = 29f;

    /// <summary>An orb's touch target for the native arc: 51 points (the game's own hover region, kept phone-sized).</summary>
    public const float NativeTouchWorld = 148f;

    /// <summary>An orb's touch target on a wide single arc: 42 points.</summary>
    public const float SingleTouchWorld = 121f;

    /// <summary>An orb's touch target in a dense queue: 32 points, as TouchSpireUI keeps them (neighbours overlap).</summary>
    public const float DenseTouchWorld = 93f;

    /// <summary>The tween a retarget runs: one sine ease in, one out, 0.45 s — the game's own orb layout speed.</summary>
    public const float TweenSeconds = 0.45f;

    /// <summary>The drawn icon's world size for a manager drawn at <paramref name="orbWorldScale"/>.</summary>
    public static float IconWorldFor(float orbWorldScale) =>
        OrbBoundsUnits * MathF.Max(0.01f, orbWorldScale) * OrbLocalScale;

    /// <summary>
    /// Half the square an orb is drawn and pressed in: the larger of its art and its touch region (the scene's
    /// <c>Bounds</c> at the orb's own scale). Everything the layout keeps inside the canvas is this square, not the
    /// smaller art inside it.
    /// </summary>
    public static float BoxHalfFor(float iconWorld, float touchWorld) =>
        MathF.Max(iconWorld, touchWorld * OrbLocalScale) * 0.5f;

    /// <summary>
    /// The arc for one player's queue. <paramref name="ownerCenterX"/> and <paramref name="ownerTopY"/> are the owner's
    /// hit-box centre and top on the canvas; <paramref name="orbitTopY"/> is the first line the arc may not cross (the
    /// stone header's bottom at 9:16, the relic band's bottom on a tall phone) and <paramref name="railTopY"/> the hand
    /// rail the arc must stay above. <paramref name="labelWorldWidth"/> is the widest visible value label's drawn width,
    /// which widens the dense pitch. Passing <paramref name="singleArc"/> replaces the native arc with the single one (the
    /// enemy probe's fallback). <paramref name="laneLeft"/> and <paramref name="laneRight"/> are the allowed range for orb
    /// centres when the caller has found an enemy lane to keep the queue out of (zero: the whole canvas).
    /// </summary>
    public static PortraitOrbArcPlan ComputeOrbArc(
        int count,
        float ownerCenterX,
        float ownerTopY,
        float canvasWidth,
        float orbitTopY,
        float railTopY,
        float labelWorldWidth = 0f,
        bool singleArc = false,
        float laneLeft = 0f,
        float laneRight = 0f,
        float orbWorldScale = OrbWorldScale)
    {
        int orbs = Math.Max(1, count);
        float world = MathF.Max(0.01f, orbWorldScale);
        float iconWorld = IconWorldFor(world);
        bool single = singleArc || orbs >= 4;
        float touch = orbs >= 5 ? DenseTouchWorld : single ? SingleTouchWorld : NativeTouchWorld;
        // The square the layout has to fit is the touch region (never smaller than the art); the fan of the native arc is
        // measured with it too, so a finger's target is what stays on the canvas.
        float boxHalf = BoxHalfFor(iconWorld, touch);
        if (!single)
            return NativeArc(orbs, ownerCenterX, ownerTopY, canvasWidth, orbitTopY, railTopY, world, iconWorld, boxHalf,
                touch);
        return SingleArc(orbs, ownerCenterX, ownerTopY, canvasWidth, orbitTopY, railTopY,
            MathF.Max(0f, labelWorldWidth), laneLeft, laneRight, world, iconWorld, boxHalf, touch);
    }

    private static PortraitOrbArcPlan NativeArc(
        int orbs,
        float ownerCenterX,
        float ownerTopY,
        float canvasWidth,
        float orbitTopY,
        float railTopY,
        float world,
        float iconWorld,
        float boxHalf,
        float touch)
    {
        float radius = NativeRadius;
        // The arc's own reach, measured from its anchor: −cos(−150°)r right of it, −cos(−25°)r left of it and sin(−25°)r
        // below it (its middle is a full radius above).
        float rightReach = 0.8660254f * radius;
        float leftReach = 0.9063078f * radius;
        float belowReach = 0.4226183f * radius;
        float left = leftReach + boxHalf + CanvasEdgeMargin;
        float right = canvasWidth - rightReach - boxHalf - CanvasEdgeMargin;
        float desiredX = MathF.Max(ownerCenterX, radius + 103f);
        float centerX = Math.Clamp(desiredX, left, MathF.Max(left, right));
        // The arc's top keeps its edge clearance under the orbit line and its lowest box its clearance above the rail: the
        // centre is the one line both bounds can agree on. A room too short for the arc keeps the top bound (readability
        // wins).
        float lowestY = orbitTopY + radius + NativeOrbitGap;
        float highestY = railTopY - CanvasEdgeMargin - boxHalf - belowReach;
        float desiredY = MathF.Max(ownerTopY - NativeBodyRise, lowestY);
        float centerY = MathF.Min(desiredY, MathF.Max(lowestY, highestY));
        return new PortraitOrbArcPlan(PortraitOrbArcKind.Native, orbs, radius, 0f, 0f, centerX, centerY, world,
            iconWorld, touch, ValueWorldHeight);
    }

    private static PortraitOrbArcPlan SingleArc(
        int orbs,
        float ownerCenterX,
        float ownerTopY,
        float canvasWidth,
        float orbitTopY,
        float railTopY,
        float labelWorldWidth,
        float laneLeft,
        float laneRight,
        float world,
        float iconWorld,
        float boxHalf,
        float touch)
    {
        // The pitch: the labels never collide, a four-orb row leaves no art touching, and a five-to-ten queue overlaps on
        // the phone's own dense spacing. The safe width then caps the whole row, and an enemy lane may narrow it further.
        float pitch = MathF.Max(labelWorldWidth + LabelPitchGap, BasePitch(orbs, world));
        if (orbs > 1)
        {
            float capped = (canvasWidth - SingleArcPitchMargin) / (orbs - 1);
            pitch = MathF.Min(pitch, MathF.Max(MinimumLanePitch, capped));
        }
        float left = SingleArcSide;
        float right = canvasWidth - SingleArcSide;
        if (laneRight > laneLeft + 1f && orbs > 1)
        {
            float lanePitch = (laneRight - laneLeft) / (orbs - 1);
            // A lane is used whenever it can hold the queue without stacking its labels: it re-centres the queue inside
            // itself (so the row stays left of a body that is closer than the canvas edge) and narrows the pitch if needed.
            if (lanePitch >= MathF.Max(MinimumLanePitch, labelWorldWidth + LabelPitchGap))
            {
                if (lanePitch < pitch)
                    pitch = lanePitch;
                left = laneLeft;
                right = laneRight;
            }
        }
        float half = pitch * (orbs - 1) * 0.5f;
        float centerX = orbs <= 1
            ? Math.Clamp(ownerCenterX, CanvasEdgeMargin + boxHalf, canvasWidth - CanvasEdgeMargin - boxHalf)
            : Math.Clamp(ownerCenterX, left + half, MathF.Max(left + half, right - half));
        // The queue floats above the owner's head; its ends rest on the anchor line and its middle rises by the depth.
        float lowestY = orbitTopY + SingleArcDepth + boxHalf + CanvasEdgeMargin;
        float highestY = railTopY - CanvasEdgeMargin - boxHalf;
        float desiredY = MathF.Max(ownerTopY - SingleArcBodyRise, orbitTopY + SingleArcOrbitGap);
        float centerY = MathF.Min(desiredY, MathF.Max(lowestY, highestY));
        return new PortraitOrbArcPlan(PortraitOrbArcKind.Single, orbs, 0f, pitch, SingleArcDepth, centerX, centerY,
            world, iconWorld, touch, ValueWorldHeight);
    }

    /// <summary>The pitch a count asks for before the labels, the lane and the safe width have their say.</summary>
    private static float BasePitch(int orbs, float world) => orbs switch
    {
        1 => 0f,
        2 or 3 => ShallowWidePitch,
        4 => MathF.Max(WidePitch, OrbArtUnits * world + 10f),
        _ => DensePitch,
    };
}
