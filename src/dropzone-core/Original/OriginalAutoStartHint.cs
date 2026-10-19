namespace Dropzone.Core.Original;

/// <summary>When to suggest turning on the official app's start with Windows.</summary>
public static class OriginalAutoStartHint
{
    /// <summary>
    /// True when the official app does not start with Windows and did at the last check, or this is the first check
    /// (<paramref name="lastSeen"/> null). While it stays off, the hint is not repeated at every start.
    /// </summary>
    public static bool ShouldShow(bool? lastSeen, bool startsWithWindows) => !startsWithWindows && lastSeen != false;
}
