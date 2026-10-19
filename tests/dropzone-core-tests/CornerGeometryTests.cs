using System.Drawing;
using Dropzone.Core.Settings;
using Dropzone.Core.Ui;

namespace Dropzone.Core.Tests;

public class CornerGeometryTests
{
    // A 1920×1080 monitor with a 40-pixel taskbar at the bottom.
    private static readonly Rectangle WorkArea = new(0, 0, 1920, 1040);

    [Fact]
    public void TriggerRegion_BottomRight()
    {
        Assert.Equal(new Rectangle(1770, 890, 150, 150), CornerGeometry.TriggerRegion(WorkArea, Corner.BottomRight));
    }

    [Fact]
    public void TriggerRegion_TopLeft()
    {
        Assert.Equal(new Rectangle(0, 0, 150, 150), CornerGeometry.TriggerRegion(WorkArea, Corner.TopLeft));
    }

    [Fact]
    public void ZoneRect_BottomRight()
    {
        Assert.Equal(new Rectangle(1840, 960, 68, 68), CornerGeometry.ZoneRect(WorkArea, Corner.BottomRight));
    }

    [Fact]
    public void ZoneRect_TopLeft()
    {
        Assert.Equal(new Rectangle(12, 12, 68, 68), CornerGeometry.ZoneRect(WorkArea, Corner.TopLeft));
    }

    [Fact]
    public void PanelRect_BottomRight_ThreeDevices()
    {
        // 12 pixels left of the zone, bottom edge level with the zone's (1028).
        Assert.Equal(new Rectangle(1668, 892, 160, 136), CornerGeometry.PanelRect(WorkArea, Corner.BottomRight, 3));
    }

    [Fact]
    public void PanelRect_TopLeft_ThreeDevices()
    {
        Assert.Equal(new Rectangle(92, 12, 160, 136), CornerGeometry.PanelRect(WorkArea, Corner.TopLeft, 3));
    }

    [Fact]
    public void PanelRect_NoDevices_HasRoomForOneLine()
    {
        // The "没有发现设备" line takes one item's height.
        Assert.Equal(new Rectangle(1668, 988, 160, 40), CornerGeometry.PanelRect(WorkArea, Corner.BottomRight, 0));
    }

    [Fact]
    public void ZoneRect_SecondMonitor_BottomRight()
    {
        Rectangle second = new(1920, 0, 2560, 1400);
        Assert.Equal(new Rectangle(4400, 1320, 68, 68), CornerGeometry.ZoneRect(second, Corner.BottomRight));
    }

    [Fact]
    public void AllRects_InsideWorkArea()
    {
        Rectangle[] workAreas = [WorkArea, new(1920, 0, 2560, 1400), new(-1280, 200, 1280, 984)];
        foreach (Rectangle workArea in workAreas)
        {
            foreach (Corner corner in Enum.GetValues<Corner>())
            {
                Assert.True(workArea.Contains(CornerGeometry.TriggerRegion(workArea, corner)), $"{workArea} {corner} trigger");
                Rectangle zone = CornerGeometry.ZoneRect(workArea, corner);
                Assert.True(workArea.Contains(zone), $"{workArea} {corner} zone");
                for (int devices = 0; devices <= 10; devices++)
                {
                    Rectangle panel = CornerGeometry.PanelRect(workArea, corner, devices);
                    Assert.True(workArea.Contains(panel), $"{workArea} {corner} {devices} devices: {panel}");
                    Assert.False(panel.IntersectsWith(zone), $"{workArea} {corner} {devices} devices overlap the zone");
                }
            }
        }
    }

    [Fact]
    public void PanelRect_TooManyDevices_IsCappedToTheWorkArea()
    {
        Rectangle small = new(0, 0, 800, 300);
        int fit = CornerGeometry.MaxPanelItems(small);
        // 300 minus the 12-pixel margins leaves 276: five items (5 × 40 + 4 × 8 = 232), not six (280).
        Assert.Equal(5, fit);
        Rectangle panel = CornerGeometry.PanelRect(small, Corner.BottomRight, 50);
        Assert.True(small.Contains(panel));
        Assert.Equal(CornerGeometry.PanelRect(small, Corner.BottomRight, fit), panel);
    }
}
