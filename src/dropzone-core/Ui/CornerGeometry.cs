using System;
using System.Drawing;
using Dropzone.Core.Settings;

namespace Dropzone.Core.Ui;

/// <summary>
/// Where the drop zone and its device panel go inside a monitor's work area (the area without the taskbar). All
/// rectangles are in physical pixels, in the same virtual-screen coordinates as the work area.
/// </summary>
public static class CornerGeometry
{
    /// <summary>Side of the square in the corner that makes the zone appear when a drag enters it.</summary>
    public const int TriggerSize = 150;

    /// <summary>Side of the drop zone.</summary>
    public const int ZoneSize = 68;

    /// <summary>Gap between the zone and both work-area edges, and between the zone and the panel.</summary>
    public const int Margin = 12;

    public const int PanelWidth = 160;

    public const int ItemHeight = 40;

    public const int ItemSpacing = 8;

    /// <summary>The <see cref="TriggerSize"/> square in <paramref name="corner"/> of the work area.</summary>
    public static Rectangle TriggerRegion(Rectangle workArea, Corner corner) =>
        Place(workArea, corner, TriggerSize, TriggerSize, inset: 0);

    /// <summary>The <see cref="ZoneSize"/> square, <see cref="Margin"/> pixels from both edges of the corner.</summary>
    public static Rectangle ZoneRect(Rectangle workArea, Corner corner) =>
        Place(workArea, corner, ZoneSize, ZoneSize, inset: Margin);

    /// <summary>How many device items fit in the work area's height (at least one).</summary>
    public static int MaxPanelItems(Rectangle workArea)
    {
        int available = workArea.Height - 2 * Margin;
        return Math.Max(1, (available + ItemSpacing) / (ItemHeight + ItemSpacing));
    }

    /// <summary>
    /// The device panel beside the zone, towards the inside of the screen, <see cref="Margin"/> pixels away from it,
    /// its bottom edge level with the zone's (bottom corners) or its top edge (top corners). No devices still leaves
    /// room for one line; more devices than <see cref="MaxPanelItems"/> are capped to it.
    /// </summary>
    public static Rectangle PanelRect(Rectangle workArea, Corner corner, int deviceCount)
    {
        int items = Math.Clamp(deviceCount, 1, MaxPanelItems(workArea));
        int height = items * ItemHeight + (items - 1) * ItemSpacing;
        Rectangle zone = ZoneRect(workArea, corner);
        int x = IsRight(corner) ? zone.Left - Margin - PanelWidth : zone.Right + Margin;
        int y = IsBottom(corner) ? zone.Bottom - height : zone.Top;
        return new Rectangle(x, y, PanelWidth, height);
    }

    private static Rectangle Place(Rectangle workArea, Corner corner, int width, int height, int inset)
    {
        int x = IsRight(corner) ? workArea.Right - inset - width : workArea.Left + inset;
        int y = IsBottom(corner) ? workArea.Bottom - inset - height : workArea.Top + inset;
        return new Rectangle(x, y, width, height);
    }

    private static bool IsRight(Corner corner) => corner is Corner.BottomRight or Corner.TopRight;

    private static bool IsBottom(Corner corner) => corner is Corner.BottomRight or Corner.BottomLeft;
}
