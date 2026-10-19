using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dropzone.Core.Protocol;

namespace Dropzone.Core.Discovery;

/// <summary>
/// LocalSend multicast on 224.0.0.167:53317. Listens through a shared binding of UDP 53317 (the official app keeps
/// its own binding), and re-announces the official app's identity on every interface, because on this PC the official
/// app's own announcements do not leave through every interface (findings F7). A peer's announcement that asks for
/// replies is answered like a LocalSend peer would: a register to it with the official app's identity. It never
/// announces or registers any other identity, so peers still list this PC once.
/// </summary>
/// <param name="registry">Receives every valid announcement heard.</param>
/// <param name="originalIdentity">The official app's identity; nothing is announced or answered while it is null.</param>
/// <param name="localAddresses">Interfaces to join and announce on; defaults to <see cref="LocalInterfaces.Ipv4"/>.</param>
/// <param name="sender">
/// Sends one datagram to the multicast group from the given local address. Defaults to a UDP socket bound to that
/// address with multicast TTL 1. Tests pass a capturing sender.
/// </param>
/// <param name="clock">Time stamped on received announcements and used by the reply throttle.</param>
/// <param name="announceInterval">Time between announcements; defaults to 30 seconds.</param>
/// <param name="register">
/// Answers an announcement: registers to the target with the official app's identity and records the reply
/// (normally <see cref="SubnetScanner.RegisterAsync"/>). Null: announcements are not answered.
/// </param>
/// <param name="onError">
/// Told about unexpected failures in the announce and receive loops, which carry on regardless (for logging).
/// </param>
public sealed class MulticastListener(
    DeviceRegistry registry,
    Func<DeviceInfo?> originalIdentity,
    Func<IReadOnlyList<LocalAddress>>? localAddresses = null,
    Func<IPAddress, byte[], CancellationToken, Task>? sender = null,
    Func<DateTimeOffset>? clock = null,
    TimeSpan? announceInterval = null,
    Func<ScanTarget, CancellationToken, Task>? register = null,
    Action<Exception>? onError = null) : IDisposable
{
    public const int Port = 53317;

    public static readonly IPAddress Group = IPAddress.Parse("224.0.0.167");

    /// <summary>At most one register answer per IP within this time.</summary>
    public static readonly TimeSpan ReplyThrottle = TimeSpan.FromSeconds(10);

    private const int MaxDatagramBytes = 64 * 1024;
    private static readonly TimeSpan ReceiveErrorPause = TimeSpan.FromSeconds(1);

    private readonly Func<IReadOnlyList<LocalAddress>> _localAddresses = localAddresses ?? LocalInterfaces.Ipv4;
    private readonly Func<IPAddress, byte[], CancellationToken, Task> _sender = sender ?? SendMulticastAsync;
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly TimeSpan _announceInterval = announceInterval ?? TimeSpan.FromSeconds(30);

    // Cancelled on dispose but never disposed: background work may still read its token.
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly MembershipTracker _memberships = new();
    private readonly Dictionary<IPAddress, DateTimeOffset> _lastReply = [];
    private Socket? _socket;
    private bool _announcing;
    private bool _disposed;

    /// <summary>True once the shared binding of UDP 53317 succeeded.</summary>
    public bool IsListening => Volatile.Read(ref _socket) is not null;

    /// <summary>Starts listening and announcing. Safe to call more than once.</summary>
    public void Start()
    {
        StartListening();
        StartAnnouncing();
    }

    /// <summary>
    /// Binds UDP 53317 shared (never exclusively) and joins the multicast group on every interface. If the binding
    /// fails, nothing is heard but announcing still works; <see cref="IsListening"/> stays false.
    /// </summary>
    public void StartListening()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_socket is not null)
            {
                return;
            }

            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                socket.ExclusiveAddressUse = false;
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                socket.Bind(new IPEndPoint(IPAddress.Any, Port));
            }
            catch (SocketException)
            {
                socket.Dispose();
                return;
            }

            _socket = socket;
            UpdateMemberships();
            CancellationToken ct = _stop.Token;
            _ = Task.Run(() => ReceiveLoopAsync(socket, ct));
        }
    }

    /// <summary>Announces now and then every interval until disposed. Safe to call more than once.</summary>
    public void StartAnnouncing()
    {
        CancellationToken ct;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_announcing)
            {
                return;
            }

            _announcing = true;
            ct = _stop.Token;
        }

        _ = Task.Run(() => AnnounceLoopAsync(ct));
    }

    /// <summary>
    /// Sends the official app's identity with <c>announce: true</c> from every interface, once. Does nothing while the
    /// identity is unknown. A failure on one interface does not stop the others; never throws for network errors.
    /// </summary>
    public async Task AnnounceNowAsync(CancellationToken ct)
    {
        DeviceInfo? identity = originalIdentity();
        if (identity is null)
        {
            return;
        }

        var announcement = new Announcement(identity.Alias, identity.Version, identity.DeviceModel, identity.DeviceType,
            identity.Fingerprint, identity.Port, identity.Protocol, identity.Download, Announce: true);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(announcement, LocalSendJson.Options);

        foreach (LocalAddress local in SafeLocalAddresses())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await _sender(local.Address, payload, ct).ConfigureAwait(false);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // That interface may have just gone away; the next round tries again.
            }
        }
    }

    /// <summary>
    /// The receive path: hands one datagram from <paramref name="source"/> to the registry when it parses, and answers
    /// it with a register (in the background) when it asks for replies. Datagrams from one of this PC's own addresses
    /// (the official app's announcements and this program's re-announcements) are dropped, so this PC never lists
    /// itself, even while the official app's fingerprint is unknown. Never throws.
    /// </summary>
    public void HandleDatagram(ReadOnlySpan<byte> datagram, IPAddress source)
    {
        try
        {
            Announcement? announcement = AnnouncementParser.TryParse(datagram);
            if (announcement is null || IsOwnAddress(source))
            {
                return;
            }

            DateTimeOffset now = _clock();
            registry.Upsert(announcement.ToDeviceInfo(), source, now);
            if (announcement.Announce)
            {
                AnswerAnnouncement(announcement, source, now);
            }
        }
        catch (Exception)
        {
            // One bad datagram or event handler must not stop the receive loop.
        }
    }

    public void Dispose()
    {
        Socket? socket;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            socket = _socket;
            _socket = null;
        }

        _stop.Cancel();
        socket?.Dispose();
    }

    private void AnswerAnnouncement(Announcement announcement, IPAddress source, DateTimeOffset now)
    {
        if (register is null || originalIdentity() is null || registry.IsOwnFingerprint(announcement.Fingerprint))
        {
            return;
        }

        CancellationToken ct;
        lock (_gate)
        {
            if (_disposed
                || (_lastReply.TryGetValue(source, out DateTimeOffset last) && now - last < ReplyThrottle))
            {
                return;
            }

            if (_lastReply.Count > 256)
            {
                foreach (IPAddress stale in _lastReply.Where(p => now - p.Value >= ReplyThrottle).Select(p => p.Key).ToList())
                {
                    _lastReply.Remove(stale);
                }
            }

            _lastReply[source] = now;
            ct = _stop.Token;
        }

        var target = new ScanTarget(source, announcement.Port, announcement.Protocol);
        _ = Task.Run(async () =>
        {
            try
            {
                await register(target, ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best effort: the peer announces again, and the periodic refresh registers too.
            }
        });
    }

    private bool IsOwnAddress(IPAddress source) => SafeLocalAddresses().Any(l => l.Address.Equals(source));

    /// <summary>
    /// Announces every interval until disposed. A failing round is reported to <c>onError</c> and the next round runs
    /// as usual; only cancellation ends the loop.
    /// </summary>
    private async Task AnnounceLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(_announceInterval);
            do
            {
                try
                {
                    lock (_gate)
                    {
                        // Interfaces come and go (Wi-Fi reconnects); keep the group memberships in step.
                        UpdateMemberships();
                    }

                    await AnnounceNowAsync(ct).ConfigureAwait(false);
                }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    ReportError(e);
                }
            }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ReportError(Exception error)
    {
        try
        {
            onError?.Invoke(error);
        }
        catch (Exception)
        {
            // A failing error handler must not end a loop either.
        }
    }

    /// <summary>Joins the group on new interfaces and leaves vanished ones. Caller holds <see cref="_gate"/>.</summary>
    private void UpdateMemberships()
    {
        Socket? socket = _socket;
        if (socket is null)
        {
            return;
        }

        _memberships.Update(
            SafeLocalAddresses().Select(l => l.Address),
            join: address =>
            {
                try
                {
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                        new MulticastOption(Group, address));
                    return true;
                }
                catch (SocketException e) when (e.SocketErrorCode == SocketError.AddressAlreadyInUse)
                {
                    // Windows' WSAEADDRINUSE here means "already a member".
                    return true;
                }
                catch (SocketException)
                {
                    // Not multicast-capable or already gone; tried again next round.
                    return false;
                }
            },
            leave: address =>
            {
                try
                {
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.DropMembership,
                        new MulticastOption(Group, address));
                }
                catch (SocketException)
                {
                    // The interface is gone, and its membership with it.
                }
            });
    }

    private IReadOnlyList<LocalAddress> SafeLocalAddresses()
    {
        try
        {
            return _localAddresses();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private async Task ReceiveLoopAsync(Socket socket, CancellationToken ct)
    {
        byte[] buffer = new byte[MaxDatagramBytes];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (Exception e)
            {
                // Windows reports ICMP errors from earlier sends as receive failures (SocketException): normal, not
                // reported. Anything else is. Either way pause, then keep listening.
                if (e is not SocketException)
                {
                    ReportError(e);
                }

                try
                {
                    await Task.Delay(ReceiveErrorPause, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            if (result.RemoteEndPoint is IPEndPoint source)
            {
                HandleDatagram(buffer.AsSpan(0, result.ReceivedBytes), source.Address);
            }
        }
    }

    /// <summary>One multicast datagram from <paramref name="local"/>, TTL 1 so it never leaves the local link.</summary>
    private static async Task SendMulticastAsync(IPAddress local, byte[] payload, CancellationToken ct)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
        socket.Bind(new IPEndPoint(local, 0));
        await socket.SendToAsync(payload, SocketFlags.None, new IPEndPoint(Group, Port), ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Which interface addresses hold a multicast group membership. Not thread-safe; <see cref="MulticastListener"/>
/// calls it under its lock.
/// </summary>
public sealed class MembershipTracker
{
    private readonly HashSet<IPAddress> _joined = [];

    /// <summary>Addresses currently joined.</summary>
    public IReadOnlyCollection<IPAddress> Joined => _joined;

    /// <summary>
    /// Leaves addresses no longer in <paramref name="current"/> (so one that comes back is joined again) and joins the
    /// new ones. A join that returns false is retried on the next update.
    /// </summary>
    public void Update(IEnumerable<IPAddress> current, Func<IPAddress, bool> join, Action<IPAddress> leave)
    {
        var now = new HashSet<IPAddress>(current);
        foreach (IPAddress gone in _joined.Where(a => !now.Contains(a)).ToList())
        {
            leave(gone);
            _joined.Remove(gone);
        }

        foreach (IPAddress address in now.Where(a => !_joined.Contains(a)))
        {
            if (join(address))
            {
                _joined.Add(address);
            }
        }
    }
}
