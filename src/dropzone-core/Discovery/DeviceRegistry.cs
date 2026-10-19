using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Dropzone.Core.Protocol;

namespace Dropzone.Core.Discovery;

/// <summary>
/// The devices seen on the network, keyed by fingerprint (compared case-insensitively). Devices whose fingerprint is
/// one of this PC's own (the official app's, this program's private one) are never listed. Thread-safe; events are
/// raised outside the lock, on the caller's thread.
/// </summary>
/// <param name="ownFingerprints">This PC's fingerprints, asked again on every call because they can become known later.</param>
public sealed class DeviceRegistry(Func<IReadOnlySet<string>> ownFingerprints)
{
    /// <summary>A device not heard from for longer than this is dropped from <see cref="Snapshot"/>.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(3);

    private readonly object _gate = new();
    private readonly Dictionary<string, DiscoveredDevice> _devices = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A device appeared for the first time, or again after it had expired.</summary>
    public event Action<DiscoveredDevice>? DeviceAdded;

    /// <summary>Every accepted <see cref="Upsert"/>, including plain refreshes of a known device.</summary>
    public event Action<DiscoveredDevice>? DeviceSeen;

    /// <summary>The list changed: a device was added, or a shown field (not just the last-seen time) changed.</summary>
    public event Action? Changed;

    /// <summary>Adds or refreshes the device with <paramref name="info"/>'s fingerprint, now at <paramref name="ip"/>.</summary>
    public void Upsert(DeviceInfo info, IPAddress ip, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(info.Fingerprint) || IsOwn(info.Fingerprint))
        {
            return;
        }

        DiscoveredDevice device;
        bool added;
        bool changed;
        lock (_gate)
        {
            _devices.TryGetValue(info.Fingerprint, out DiscoveredDevice? old);
            added = old is null || IsExpired(old, now);
            device = new DiscoveredDevice(info.Alias, info.DeviceType, info.Fingerprint, ip, info.Port, info.Protocol,
                old?.PrivateFingerprint, now);
            changed = added || device with { LastSeen = old!.LastSeen } != old;
            _devices[info.Fingerprint] = device;
        }

        DeviceSeen?.Invoke(device);
        if (added)
        {
            DeviceAdded?.Invoke(device);
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>Records what probing the dedicated port at <paramref name="ip"/> found (null: this program is not there).</summary>
    public void SetPrivate(IPAddress ip, string? privateFingerprint)
    {
        bool changed = false;
        lock (_gate)
        {
            foreach (DiscoveredDevice device in _devices.Values.Where(d => d.Ip.Equals(ip)).ToList())
            {
                if (!string.Equals(device.PrivateFingerprint, privateFingerprint, StringComparison.OrdinalIgnoreCase))
                {
                    _devices[device.Fingerprint] = device with { PrivateFingerprint = privateFingerprint };
                    changed = true;
                }
            }
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>Devices heard from within <see cref="Lifetime"/>, minus this PC, sorted by alias.</summary>
    public IReadOnlyList<DiscoveredDevice> Snapshot(DateTimeOffset now)
    {
        List<DiscoveredDevice> devices;
        lock (_gate)
        {
            devices = _devices.Values.Where(d => !IsExpired(d, now)).ToList();
        }

        return devices
            .Where(d => !IsOwn(d.Fingerprint))
            .OrderBy(d => d.Alias, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(d => d.Fingerprint, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsExpired(DiscoveredDevice device, DateTimeOffset now) => now - device.LastSeen > Lifetime;

    /// <summary>True when <paramref name="fingerprint"/> is one of this PC's own (case-insensitive).</summary>
    public bool IsOwnFingerprint(string fingerprint) => IsOwn(fingerprint);

    private bool IsOwn(string fingerprint) =>
        ownFingerprints().Any(own => string.Equals(own, fingerprint, StringComparison.OrdinalIgnoreCase));
}
