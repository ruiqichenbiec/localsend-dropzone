using System;
using Dropzone.Core.Protocol;

namespace Dropzone.Core.Original;

/// <summary>What one lookup changed.</summary>
public enum IdentityChange
{
    /// <summary>Nothing worth reporting.</summary>
    None,

    /// <summary>The official app answers again (or for the first time).</summary>
    Found,

    /// <summary>It still answers, with another alias, fingerprint or port.</summary>
    Changed,

    /// <summary>It stopped answering: <see cref="OriginalIdentityTracker.Live"/> has just become null.</summary>
    Lost,
}

/// <summary>
/// Keeps two views of the official app's identity from repeated lookups. <see cref="Live"/> is the identity while the
/// official app answers; it becomes null after <see cref="FailuresUntilLost"/> lookups in a row fail, and gates
/// everything that tells other devices "this PC can receive" (announcing, registering, answering announcements).
/// <see cref="LastKnown"/> is the last identity ever seen and is what sends carry as <c>info</c>. Thread-safe.
/// </summary>
public sealed class OriginalIdentityTracker
{
    public const int FailuresUntilLost = 2;

    /// <summary>Time between lookups while the identity is not (or may no longer be) live.</summary>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);

    /// <summary>Time between lookups while the official app answers.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);

    private readonly object _gate = new();
    private DeviceInfo? _live;
    private DeviceInfo? _lastKnown;
    private int _failures;

    public DeviceInfo? Live
    {
        get { lock (_gate) { return _live; } }
    }

    public DeviceInfo? LastKnown
    {
        get { lock (_gate) { return _lastKnown; } }
    }

    /// <summary>
    /// When to look again: every 5 seconds until the first success, while the identity is not live, and after a
    /// failed lookup (so a stopped official app is noticed within seconds); otherwise every 60 seconds.
    /// </summary>
    public TimeSpan NextDelay
    {
        get
        {
            lock (_gate)
            {
                return _live is null || _failures > 0 ? RetryInterval : RefreshInterval;
            }
        }
    }

    /// <summary>Records one lookup: the identity found, or null when the official app did not answer.</summary>
    public IdentityChange Record(DeviceInfo? resolved)
    {
        lock (_gate)
        {
            if (resolved is null)
            {
                _failures++;
                if (_live is not null && _failures >= FailuresUntilLost)
                {
                    _live = null;
                    return IdentityChange.Lost;
                }

                return IdentityChange.None;
            }

            _failures = 0;
            DeviceInfo? previous = _live;
            _live = resolved;
            _lastKnown = resolved;
            if (previous is null)
            {
                return IdentityChange.Found;
            }

            return previous == resolved ? IdentityChange.None : IdentityChange.Changed;
        }
    }
}
