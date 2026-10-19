using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Dropzone.Core.Send;

namespace Dropzone.Core.Discovery;

/// <summary>
/// Keeps <see cref="DeviceRegistry"/> filled: multicast (listening and re-announcing the official app), plus a scan
/// every 45 seconds that registers to the known hosts and sweeps the local /24-or-smaller subnets. Every
/// newly added device is probed for this program's dedicated port, so devices heard only through multicast are probed
/// too. Every device seen is recorded in the known-hosts list.
/// </summary>
public sealed class DiscoveryService : IDisposable
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(45);

    private readonly DeviceRegistry _registry;
    private readonly MulticastListener? _listener;
    private readonly SubnetScanner _scanner;
    private readonly TransferClient _client;
    private readonly KnownHostsStore? _knownHosts;
    private readonly int _subnetPort;
    private readonly Func<IReadOnlyList<LocalAddress>> _localAddresses;
    private readonly Func<DateTimeOffset> _clock;

    // Cancelled on dispose but never disposed: background work may still read its token.
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private Task? _refresh;
    private Task? _scan;
    private bool _started;
    private bool _disposed;

    /// <summary>
    /// Hooks the registry at once (probe on add, record in known hosts), so devices are handled even before
    /// <see cref="Start"/>.
    /// </summary>
    /// <param name="listener">Null when the shared multicast binding is not used.</param>
    /// <param name="knownHosts">Persisted hosts to register to on each refresh; null to skip.</param>
    /// <param name="localAddresses">This PC's interfaces; defaults to <see cref="LocalInterfaces.Ipv4"/>.</param>
    /// <param name="clock">Defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    /// <param name="subnetPort">Port swept on subnet hosts: LocalSend's default, 53317.</param>
    public DiscoveryService(
        DeviceRegistry registry,
        MulticastListener? listener,
        SubnetScanner scanner,
        TransferClient client,
        KnownHostsStore? knownHosts = null,
        Func<IReadOnlyList<LocalAddress>>? localAddresses = null,
        Func<DateTimeOffset>? clock = null,
        int subnetPort = 53317)
    {
        _registry = registry;
        _listener = listener;
        _scanner = scanner;
        _client = client;
        _knownHosts = knownHosts;
        _subnetPort = subnetPort;
        _localAddresses = localAddresses ?? LocalInterfaces.Ipv4;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _registry.DeviceAdded += OnDeviceAdded;
        _registry.DeviceSeen += OnDeviceSeen;
    }

    /// <summary>Starts multicast and the 45-second refresh (the first one runs at once). Idempotent.</summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                return;
            }

            _started = true;
        }

        _listener?.Start();
        CancellationToken ct = _stop.Token;
        _ = Task.Run(() => RefreshLoopAsync(ct));
    }

    /// <summary>
    /// For outside callers (a drag starting): announces, then registers to the known hosts and sweeps the subnets.
    /// Callers arriving while one is running share it; a periodic scan already running is joined rather than repeated.
    /// Never faults.
    /// </summary>
    public Task RefreshNowAsync()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_refresh is { IsCompleted: false })
            {
                return _refresh;
            }

            CancellationToken ct = _stop.Token;
            _refresh = Task.Run(() => AnnounceThenScanAsync(ct));
            return _refresh;
        }
    }

    /// <summary>Stops discovery and disposes the listener passed in, which this service owns.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _registry.DeviceAdded -= OnDeviceAdded;
        _registry.DeviceSeen -= OnDeviceSeen;
        _stop.Cancel();
        _listener?.Dispose();
        SaveKnownHosts();
    }

    /// <summary>The 45-second scans. They do not announce: the listener's own 30-second timer does that.</summary>
    private async Task RefreshLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(RefreshInterval);
        do
        {
            try
            {
                await ScanSharedAsync(ct).ConfigureAwait(false);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Never let one failure end the loop.
            }
        }
        while (await WaitForTickAsync(timer, ct).ConfigureAwait(false));
    }

    private static async Task<bool> WaitForTickAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task AnnounceThenScanAsync(CancellationToken ct)
    {
        try
        {
            if (_listener is not null)
            {
                await _listener.AnnounceNowAsync(ct).ConfigureAwait(false);
            }

            await ScanSharedAsync(ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best effort; callers only wait for it to end.
        }
    }

    /// <summary>The running scan if there is one, else a new one.</summary>
    private Task ScanSharedAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_scan is not { IsCompleted: false })
            {
                _scan = Task.Run(() => ScanCoreAsync(ct));
            }

            return _scan;
        }
    }

    private async Task ScanCoreAsync(CancellationToken ct)
    {
        try
        {
            IReadOnlyList<LocalAddress> locals = _localAddresses();
            var own = new HashSet<IPAddress>(locals.Select(l => l.Address));
            var targets = new Dictionary<IPAddress, ScanTarget>();
            foreach (KnownHost host in _knownHosts?.Hosts(_clock()) ?? [])
            {
                targets.TryAdd(host.Ip, new ScanTarget(host.Ip, host.Port, host.Protocol));
            }

            foreach (LocalAddress local in locals)
            {
                foreach (IPAddress ip in SubnetMath.Hosts(local.Address, local.PrefixLength))
                {
                    targets.TryAdd(ip, new ScanTarget(ip, _subnetPort, null));
                }
            }

            await _scanner.ScanAsync(targets.Values.Where(t => !own.Contains(t.Ip)), ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A refresh is best effort: the timer runs the next one, and callers only wait for it to end.
        }

        SaveKnownHosts();
    }

    private void OnDeviceAdded(DiscoveredDevice device)
    {
        CancellationToken ct = _stop.Token;
        _ = Task.Run(async () =>
        {
            string? privateFingerprint = await _client.ProbePrivateAsync(device.Ip, ct).ConfigureAwait(false);
            if (!ct.IsCancellationRequested)
            {
                _registry.SetPrivate(device.Ip, privateFingerprint);
            }
        });
    }

    private void OnDeviceSeen(DiscoveredDevice device) =>
        _knownHosts?.Record(device.Ip, device.Port, device.Protocol, device.LastSeen);

    private void SaveKnownHosts()
    {
        try
        {
            _knownHosts?.Save(_clock());
        }
        catch (Exception)
        {
            // Tried again after the next refresh.
        }
    }
}
