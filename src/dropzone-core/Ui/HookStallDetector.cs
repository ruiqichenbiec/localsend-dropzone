using System;
using System.Drawing;

namespace Dropzone.Core.Ui;

/// <summary>
/// Spots a low-level mouse hook that Windows dropped without telling: between two samples the primary button is held
/// and the cursor moved, yet the hook saw no message at all. Fed one sample per tick by the drag watcher; reports at
/// most once a minute, since a drag inside an elevated window (whose input the hook cannot see) looks the same.
/// Not thread-safe.
/// </summary>
public sealed class HookStallDetector
{
    /// <summary>Shortest time between two reports.</summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(1);

    private Point? _lastCursor;
    private long _lastMessages;
    private TimeSpan? _lastReport;

    /// <param name="now">Monotonic time of this sample.</param>
    /// <param name="primaryHeld">The primary mouse button is down now.</param>
    /// <param name="cursor">Cursor position now; null when it cannot be read (another desktop is active).</param>
    /// <param name="hookMessages">How many messages the hook has received so far.</param>
    /// <returns>True when the hook should be reinstalled.</returns>
    public bool Check(TimeSpan now, bool primaryHeld, Point? cursor, long hookMessages)
    {
        bool stalled = primaryHeld
                       && cursor is not null
                       && _lastCursor is not null
                       && cursor != _lastCursor
                       && hookMessages == _lastMessages;
        _lastCursor = cursor;
        _lastMessages = hookMessages;
        if (!stalled || (_lastReport is { } last && now - last < Cooldown))
        {
            return false;
        }

        _lastReport = now;
        return true;
    }
}
