using System.Drawing;
using Dropzone.Core.Ui;

namespace Dropzone.Core.Tests;

public class HookStallDetectorTests
{
    private static readonly Point Start = new(100, 100);
    private static readonly Point Moved = new(180, 140);

    [Fact]
    public void Check_FirstSample_False()
    {
        HookStallDetector detector = new();

        Assert.False(detector.Check(TimeSpan.FromSeconds(1), primaryHeld: true, Moved, hookMessages: 0));
    }

    [Fact]
    public void Check_DragMovesCursorWithoutHookMessages_True()
    {
        HookStallDetector detector = new();
        detector.Check(TimeSpan.FromSeconds(1), primaryHeld: true, Start, hookMessages: 42);

        Assert.True(detector.Check(TimeSpan.FromSeconds(2), primaryHeld: true, Moved, hookMessages: 42));
    }

    [Fact]
    public void Check_DragWithHookMessages_False()
    {
        HookStallDetector detector = new();
        detector.Check(TimeSpan.FromSeconds(1), primaryHeld: true, Start, hookMessages: 42);

        Assert.False(detector.Check(TimeSpan.FromSeconds(2), primaryHeld: true, Moved, hookMessages: 57));
    }

    [Fact]
    public void Check_ButtonNotHeld_False()
    {
        // Plain moves without hook messages also happen over elevated windows; only a drag counts.
        HookStallDetector detector = new();
        detector.Check(TimeSpan.FromSeconds(1), primaryHeld: false, Start, hookMessages: 42);

        Assert.False(detector.Check(TimeSpan.FromSeconds(2), primaryHeld: false, Moved, hookMessages: 42));
    }

    [Fact]
    public void Check_HeldButCursorStill_False()
    {
        HookStallDetector detector = new();
        detector.Check(TimeSpan.FromSeconds(1), primaryHeld: true, Start, hookMessages: 42);

        Assert.False(detector.Check(TimeSpan.FromSeconds(2), primaryHeld: true, Start, hookMessages: 42));
    }

    [Fact]
    public void Check_AgainWithinOneMinute_FalseUntilTheMinuteIsOver()
    {
        HookStallDetector detector = new();
        detector.Check(TimeSpan.FromSeconds(1), primaryHeld: true, Start, hookMessages: 42);
        Assert.True(detector.Check(TimeSpan.FromSeconds(2), primaryHeld: true, Moved, hookMessages: 42));

        Assert.False(detector.Check(TimeSpan.FromSeconds(3), primaryHeld: true, Start, hookMessages: 42));
        Assert.False(detector.Check(TimeSpan.FromSeconds(61), primaryHeld: true, Moved, hookMessages: 42));
        Assert.True(detector.Check(TimeSpan.FromSeconds(62), primaryHeld: true, Start, hookMessages: 42));
    }

    [Fact]
    public void Check_CursorUnknown_FalseAndStartsOver()
    {
        // GetCursorPos fails while another desktop (UAC prompt, lock screen) is active.
        HookStallDetector detector = new();
        detector.Check(TimeSpan.FromSeconds(1), primaryHeld: true, Start, hookMessages: 42);

        Assert.False(detector.Check(TimeSpan.FromSeconds(2), primaryHeld: true, cursor: null, hookMessages: 42));
        Assert.False(detector.Check(TimeSpan.FromSeconds(3), primaryHeld: true, Moved, hookMessages: 42));
    }
}
