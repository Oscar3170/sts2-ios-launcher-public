using System;

namespace STS2Mobile.Platform;

/// <summary>One arranged block of a creature's power/status icons on the portrait canvas.</summary>
/// <param name="Columns">Icons per row (at most two rows are used).</param>
/// <param name="Rows">Rows used (one or two).</param>
/// <param name="Count">Icons in the block; the first row holds min(Columns, Count).</param>
/// <param name="IconWidth">Drawn width of every icon, canvas units.</param>
/// <param name="RowWidth">Width of the widest row, canvas units.</param>
/// <param name="Left">Left edge of the block after centring on the hit box, neighbour limits and the canvas clamp.</param>
/// <param name="LeftOverhang">How far the block reaches left of the hit box/container, canvas units.</param>
/// <param name="RightOverhang">How far the block reaches right of the hit box/container, canvas units.</param>
public readonly record struct StatusIconLayout(
    int Columns,
    int Rows,
    int Count,
    float IconWidth,
    float RowWidth,
    float Left,
    float LeftOverhang,
    float RightOverhang);

/// <summary>
/// Pure placement for the status (power) icons under a creature's health bar, matching the game's
/// <c>NPowerContainer.UpdatePositions</c>: the container starts at the creature's hit-box left and is as wide as the hit
/// box plus the native 25-unit padding; columns are the larger of the width's capacity and half the status count (so
/// never more than two rows); icons run row-major; a block wider than the container is centred on it. Icons are drawn at
/// TouchSpireUI's 82/1680 of the canvas width, shrunk only when the overhang exceeds 50 units per side or the gap to a
/// neighbouring block on the same status row, never below 60% of that size.
/// </summary>
public static class StatusIconMath
{
    /// <summary>TouchSpireUI's icon width as a share of the design width (82 of 1680).</summary>
    public const float IconWidthShare = 82f / 1680f;

    /// <summary>The native <c>NPowerContainer</c> padding: 25 display units at the 1.33 state-display scale.</summary>
    public const float ContainerPadding = 25f * 1.33f;

    /// <summary>The most a block may overhang its container on a side when nothing else constrains it.</summary>
    public const float MaxOverhang = 50f;

    /// <summary>The smallest icon size, as a share of the normal one.</summary>
    public const float MinIconScale = 0.6f;

    /// <summary>The horizontal canvas margin a block keeps.</summary>
    public const float EdgeMargin = 8f;

    /// <summary>The native block for <paramref name="count"/> icons over a hit box at <paramref name="hitboxLeft"/>.</summary>
    public static StatusIconLayout Compute(int count, float hitboxLeft, float hitboxWidth, float canvasWidth)
    {
        if (count <= 0)
            return new StatusIconLayout(0, 0, 0, 0f, 0f, 0f, 0f, 0f);

        float normal = canvasWidth * IconWidthShare;
        float container = Math.Max(1f, hitboxWidth) + ContainerPadding;
        int columns = Math.Max((int)MathF.Ceiling(count / 2f), (int)MathF.Ceiling(container / normal));
        columns = Math.Max(1, columns);
        return Build(count, columns, normal, container, hitboxLeft, canvasWidth);
    }

    /// <summary>
    /// The block after applying the gaps to the neighbouring blocks on its status row. Each side may overhang by half its
    /// gap at most (so two blocks meet, never overlap); when the 60% floor cannot satisfy a tighter side the block is
    /// shifted away from that neighbour as far as the other side's limit and the canvas allow, and the remaining excess is
    /// reported through the returned overhangs.
    /// </summary>
    public static StatusIconLayout ApplyNeighbourLimits(
        StatusIconLayout layout,
        float hitboxLeft,
        float hitboxWidth,
        float canvasWidth,
        float leftGap,
        float rightGap)
    {
        if (layout.Count <= 0)
            return layout;

        float normal = canvasWidth * IconWidthShare;
        float container = Math.Max(1f, hitboxWidth) + ContainerPadding;
        int used = Math.Min(layout.Columns, layout.Count);
        float limitLeft = Math.Min(MaxOverhang, Math.Max(0f, leftGap) * 0.5f);
        float limitRight = Math.Min(MaxOverhang, Math.Max(0f, rightGap) * 0.5f);
        float ceiling = Math.Min(limitLeft, limitRight);
        float rowWidth = layout.RowWidth;
        float overhang = Math.Max(0f, (rowWidth - container) * 0.5f);
        float iconWidth = layout.IconWidth;
        if (overhang > ceiling)
        {
            float target = (container + 2f * ceiling) / Math.Max(1, used);
            iconWidth = Math.Max(normal * MinIconScale, Math.Min(layout.IconWidth, target));
            rowWidth = used * iconWidth;
            overhang = Math.Max(0f, (rowWidth - container) * 0.5f);
        }

        float left = hitboxLeft - overhang;
        // A tighter side at the floor: move away from it while the opposite side still has room.
        if (overhang > limitLeft + 0.01f && limitRight > limitLeft)
        {
            float shift = Math.Min(overhang - limitLeft, Math.Max(0f, limitRight - overhang));
            left += shift;
        }
        else if (overhang > limitRight + 0.01f && limitLeft > limitRight)
        {
            float shift = Math.Min(overhang - limitRight, Math.Max(0f, limitLeft - overhang));
            left -= shift;
        }

        if (rowWidth <= canvasWidth - 2f * EdgeMargin)
            left = Math.Clamp(left, EdgeMargin, canvasWidth - EdgeMargin - rowWidth);
        else
            left = EdgeMargin;

        float leftOverhang = Math.Max(0f, hitboxLeft - left);
        float rightOverhang = Math.Max(0f, left + rowWidth - (hitboxLeft + container));
        return layout with
        {
            IconWidth = iconWidth,
            RowWidth = rowWidth,
            Left = left,
            LeftOverhang = leftOverhang,
            RightOverhang = rightOverhang,
        };
    }

    private static StatusIconLayout Build(
        int count,
        int columns,
        float normal,
        float container,
        float hitboxLeft,
        float canvasWidth)
    {
        int used = Math.Min(columns, count);
        int rows = (count + columns - 1) / columns;
        float iconWidth = normal;
        float rowWidth = used * iconWidth;
        float overhang = Math.Max(0f, (rowWidth - container) * 0.5f);
        if (overhang > MaxOverhang)
        {
            float target = (container + 2f * MaxOverhang) / Math.Max(1, used);
            iconWidth = Math.Max(normal * MinIconScale, target);
            rowWidth = used * iconWidth;
            overhang = Math.Max(0f, (rowWidth - container) * 0.5f);
        }

        float left = hitboxLeft - overhang;
        if (rowWidth <= canvasWidth - 2f * EdgeMargin)
            left = Math.Clamp(left, EdgeMargin, canvasWidth - EdgeMargin - rowWidth);
        else
            left = EdgeMargin;

        return new StatusIconLayout(
            columns,
            rows,
            count,
            iconWidth,
            rowWidth,
            left,
            Math.Max(0f, hitboxLeft - left),
            Math.Max(0f, left + rowWidth - (hitboxLeft + container)));
    }
}
