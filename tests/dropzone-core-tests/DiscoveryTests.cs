using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Dropzone.Core.Discovery;
using Dropzone.Core.Protocol;
using Dropzone.Core.Receive;
using Dropzone.Core.Send;
using Dropzone.Core.Tests.TestSupport;

namespace Dropzone.Core.Tests;

/// <summary>
/// Device discovery. Safety: nothing here sends to 224.0.0.167 or any non-loopback address, and nothing binds UDP
/// 53317 (the official app owns it on the developer's machine). Announcements are captured through the sender seam,
/// the listener is fed through its receive path, and every server binds a 127.x address.
/// </summary>
public sealed class DiscoveryTests : IAsyncLifetime
{
    private const string OwnPrivateFingerprint = "aa11bb22cc33dd44ee55ff6600778899aa11bb22cc33dd44ee55ff6600778899";

    private static readonly DeviceInfo Identity = new(
        "MyPC", "2.2", "Windows", "desktop",
        "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF", 53317, "https", false);

    private static readonly DateTimeOffset T0 = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "dropzone-core-tests", Guid.NewGuid().ToString("N"));
    private readonly List<IAsyncDisposable> _servers = [];
    private readonly List<IDisposable> _disposables = [];

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (IDisposable disposable in _disposables)
        {
            disposable.Dispose();
        }

        foreach (IAsyncDisposable server in _servers)
        {
            await server.DisposeAsync();
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ---------------------------------------------------------------- registry

    [Fact]
    public void Registry_Upsert_ThenSnapshot_ContainsDevice()
    {
        DeviceRegistry registry = NewRegistry();

        registry.Upsert(Peer("Pixel", "ABCD"), IPAddress.Parse("10.0.0.5"), T0);

        DiscoveredDevice device = Assert.Single(registry.Snapshot(T0));
        Assert.Equal("Pixel", device.Alias);
        Assert.Equal("mobile", device.DeviceType);
        Assert.Equal("ABCD", device.Fingerprint);
        Assert.Equal(IPAddress.Parse("10.0.0.5"), device.Ip);
        Assert.Equal(53317, device.Port);
        Assert.Equal("http", device.Protocol);
        Assert.Null(device.PrivateFingerprint);
        Assert.Equal(T0, device.LastSeen);
    }

    [Fact]
    public void Registry_ExcludesOwnFingerprints()
    {
        DeviceRegistry registry = NewRegistry();

        registry.Upsert(Peer("Original", Identity.Fingerprint.ToLowerInvariant()), IPAddress.Parse("10.0.0.2"), T0);
        registry.Upsert(Peer("Private", OwnPrivateFingerprint.ToUpperInvariant()), IPAddress.Parse("10.0.0.3"), T0);
        registry.Upsert(Peer("Other", "ABCD"), IPAddress.Parse("10.0.0.4"), T0);

        Assert.Equal(["Other"], registry.Snapshot(T0).Select(d => d.Alias));
    }

    [Fact]
    public void Registry_OwnFingerprintLearnedLater_IsExcludedFromSnapshot()
    {
        var own = new HashSet<string>();
        var registry = new DeviceRegistry(() => own);
        registry.Upsert(Peer("Me", Identity.Fingerprint), IPAddress.Parse("10.0.0.2"), T0);

        own.Add(Identity.Fingerprint);

        Assert.Empty(registry.Snapshot(T0));
    }

    [Fact]
    public void Registry_ExpiresAfterThreeMinutes()
    {
        DeviceRegistry registry = NewRegistry();
        registry.Upsert(Peer("Pixel", "ABCD"), IPAddress.Parse("10.0.0.5"), T0);

        Assert.Single(registry.Snapshot(T0 + TimeSpan.FromMinutes(3)));
        Assert.Empty(registry.Snapshot(T0 + TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Registry_SameFingerprintNewIp_Updates()
    {
        DeviceRegistry registry = NewRegistry();
        registry.Upsert(Peer("Pixel", "abcd"), IPAddress.Parse("10.0.0.5"), T0);

        registry.Upsert(Peer("Pixel 2", "ABCD"), IPAddress.Parse("10.0.0.9"), T0 + TimeSpan.FromSeconds(5));

        DiscoveredDevice device = Assert.Single(registry.Snapshot(T0 + TimeSpan.FromSeconds(5)));
        Assert.Equal(IPAddress.Parse("10.0.0.9"), device.Ip);
        Assert.Equal("Pixel 2", device.Alias);
        Assert.Equal(T0 + TimeSpan.FromSeconds(5), device.LastSeen);
    }

    [Fact]
    public void Registry_SetPrivate_AttachesToDeviceAtIp()
    {
        DeviceRegistry registry = NewRegistry();
        registry.Upsert(Peer("PC-A", "AAAA"), IPAddress.Parse("10.0.0.5"), T0);
        registry.Upsert(Peer("PC-B", "BBBB"), IPAddress.Parse("10.0.0.6"), T0);

        registry.SetPrivate(IPAddress.Parse("10.0.0.6"), "beef");

        IReadOnlyList<DiscoveredDevice> devices = registry.Snapshot(T0);
        Assert.Null(devices.Single(d => d.Alias == "PC-A").PrivateFingerprint);
        Assert.Equal("beef", devices.Single(d => d.Alias == "PC-B").PrivateFingerprint);
    }

    [Fact]
    public void Registry_SetPrivateNull_ClearsIt()
    {
        DeviceRegistry registry = NewRegistry();
        registry.Upsert(Peer("PC-A", "AAAA"), IPAddress.Parse("10.0.0.5"), T0);
        registry.SetPrivate(IPAddress.Parse("10.0.0.5"), "beef");

        registry.SetPrivate(IPAddress.Parse("10.0.0.5"), null);

        Assert.Null(Assert.Single(registry.Snapshot(T0)).PrivateFingerprint);
    }

    [Fact]
    public void Registry_SnapshotSortedByAlias()
    {
        DeviceRegistry registry = NewRegistry();
        registry.Upsert(Peer("zeta", "01"), IPAddress.Parse("10.0.0.5"), T0);
        registry.Upsert(Peer("Alpha", "02"), IPAddress.Parse("10.0.0.6"), T0);
        registry.Upsert(Peer("beta", "03"), IPAddress.Parse("10.0.0.7"), T0);

        Assert.Equal(["Alpha", "beta", "zeta"], registry.Snapshot(T0).Select(d => d.Alias));
    }

    [Fact]
    public void Registry_Events_AddedOnceSeenEveryTime()
    {
        DeviceRegistry registry = NewRegistry();
        var added = new List<DiscoveredDevice>();
        var seen = new List<DiscoveredDevice>();
        int changed = 0;
        registry.DeviceAdded += added.Add;
        registry.DeviceSeen += seen.Add;
        registry.Changed += () => changed++;

        registry.Upsert(Peer("Pixel", "ABCD"), IPAddress.Parse("10.0.0.5"), T0);
        registry.Upsert(Peer("Pixel", "ABCD"), IPAddress.Parse("10.0.0.5"), T0 + TimeSpan.FromSeconds(1));
        registry.Upsert(Peer("Me", Identity.Fingerprint), IPAddress.Parse("10.0.0.2"), T0);

        Assert.Single(added);
        Assert.Equal(2, seen.Count);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void Registry_DeviceBackAfterExpiry_IsAddedAgain()
    {
        DeviceRegistry registry = NewRegistry();
        int added = 0;
        registry.DeviceAdded += _ => added++;

        registry.Upsert(Peer("Pixel", "ABCD"), IPAddress.Parse("10.0.0.5"), T0);
        registry.Upsert(Peer("Pixel", "ABCD"), IPAddress.Parse("10.0.0.5"), T0 + TimeSpan.FromMinutes(10));

        Assert.Equal(2, added);
    }

    // ---------------------------------------------------------------- parser

    [Fact]
    public void Parser_ValidAnnouncement_Parses()
    {
        byte[] datagram = Encoding.UTF8.GetBytes(
            """{"alias":"Pixel","version":"2.1","deviceModel":"Pixel 8","deviceType":"mobile","fingerprint":"abc123","port":53317,"protocol":"http","download":false,"announce":true}""");

        Announcement? announcement = AnnouncementParser.TryParse(datagram);

        Assert.Equal(
            new Announcement("Pixel", "2.1", "Pixel 8", "mobile", "abc123", 53317, "http", false, true),
            announcement);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("[1,2,3]")]
    [InlineData("null")]
    [InlineData("""{"alias":"NoFingerprint","version":"2.1","port":53317,"protocol":"http"}""")]
    [InlineData("""{"fingerprint":"abc","version":"2.1","port":53317,"protocol":"http"}""")]
    [InlineData("""{"alias":"A","fingerprint":"abc","port":"lots","protocol":"http"}""")]
    [InlineData("""{"alias":"A","fingerprint":"abc","port":70000,"protocol":"http"}""")]
    [InlineData("""{"alias":"A","fingerprint":"abc","port":53317,"protocol":"ftp"}""")]
    public void Parser_Garbage_ReturnsNull(string text)
    {
        Assert.Null(AnnouncementParser.TryParse(Encoding.UTF8.GetBytes(text)));
        Assert.Null(AnnouncementParser.TryParse([0xFF, 0xFE, 0x00, 0x7B]));
    }

    [Fact]
    public void Parser_MissingPortAndProtocol_UsesLocalSendDefaults()
    {
        Announcement? announcement = AnnouncementParser.TryParse(
            Encoding.UTF8.GetBytes("""{"alias":"Old","fingerprint":"abc","announcement":true}"""));

        Assert.NotNull(announcement);
        Assert.Equal(53317, announcement.Port);
        Assert.Equal("https", announcement.Protocol);
        Assert.Equal("1.0", announcement.Version);
        Assert.True(announcement.Announce);
    }

    [Theory]
    [InlineData("""{"alias":"A","fingerprint":"abc","announce":true}""", true)]
    [InlineData("""{"alias":"A","fingerprint":"abc","announcement":true}""", true)]
    [InlineData("""{"alias":"A","fingerprint":"abc","announce":false,"announcement":false}""", false)]
    [InlineData("""{"alias":"A","fingerprint":"abc"}""", false)]
    public void Parser_EitherAnnounceFlag_Counts(string text, bool expected)
    {
        Assert.Equal(expected, AnnouncementParser.TryParse(Encoding.UTF8.GetBytes(text))!.Announce);
    }

    // ---------------------------------------------------------------- subnet math and interfaces

    [Fact]
    public void SubnetMath_Slash24_Has253HostsExcludingSelf()
    {
        List<IPAddress> hosts = SubnetMath.Hosts(IPAddress.Parse("192.168.1.42"), 24).ToList();

        Assert.Equal(253, hosts.Count);
        Assert.DoesNotContain(IPAddress.Parse("192.168.1.42"), hosts);
        Assert.DoesNotContain(IPAddress.Parse("192.168.1.0"), hosts);
        Assert.DoesNotContain(IPAddress.Parse("192.168.1.255"), hosts);
        Assert.Contains(IPAddress.Parse("192.168.1.1"), hosts);
        Assert.Contains(IPAddress.Parse("192.168.1.254"), hosts);
    }

    [Fact]
    public void SubnetMath_Slash16_ReturnsEmpty()
    {
        Assert.Empty(SubnetMath.Hosts(IPAddress.Parse("198.51.100.170"), 20));
        Assert.Empty(SubnetMath.Hosts(IPAddress.Parse("10.1.2.3"), 16));
    }

    [Fact]
    public void SubnetMath_Slash32AndIpv6_ReturnEmpty()
    {
        Assert.Empty(SubnetMath.Hosts(IPAddress.Parse("192.0.2.82"), 32));
        Assert.Empty(SubnetMath.Hosts(IPAddress.Parse("fe80::1"), 64));
    }

    [Fact]
    public void SubnetMath_Slash28_UsesTheRightBlock()
    {
        List<IPAddress> hosts = SubnetMath.Hosts(IPAddress.Parse("203.0.113.20"), 28).ToList();

        // 203.0.113.16/28: .17 to .30 minus self.
        Assert.Equal(13, hosts.Count);
        Assert.Equal(IPAddress.Parse("203.0.113.17"), hosts[0]);
        Assert.Equal(IPAddress.Parse("203.0.113.30"), hosts[^1]);
    }

    [Theory]
    [InlineData("198.51.100.170", true)]
    [InlineData("203.0.113.1", true)]
    [InlineData("169.254.10.20", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("fe80::1", false)]
    public void LocalInterfaces_UsableAddress(string address, bool usable)
    {
        Assert.Equal(usable, LocalInterfaces.IsUsableAddress(IPAddress.Parse(address)));
    }

    // ---------------------------------------------------------------- multicast listener (no real sockets)

    [Fact]
    public async Task Announcer_SendsOriginalIdentityOnEveryInterface()
    {
        var sent = new CapturingSender();
        using MulticastListener listener = NewListener(NewRegistry(), () => Identity, sent,
            Addresses("198.51.100.170/20", "203.0.113.1/24"));

        await listener.AnnounceNowAsync(CancellationToken.None);

        Assert.Equal([IPAddress.Parse("198.51.100.170"), IPAddress.Parse("203.0.113.1")],
            sent.Packets.Select(p => p.Local));
        foreach ((IPAddress _, byte[] payload) in sent.Packets)
        {
            Announcement? announcement = JsonSerializer.Deserialize<Announcement>(payload, LocalSendJson.Options);
            Assert.NotNull(announcement);
            Assert.True(announcement.Announce);
            Assert.Equal(Identity, announcement.ToDeviceInfo());
        }
    }

    [Fact]
    public async Task Announcer_NoOriginalIdentity_SendsNothing()
    {
        var sent = new CapturingSender();
        using MulticastListener listener = NewListener(NewRegistry(), () => null, sent, Addresses("10.0.0.2/24"));

        await listener.AnnounceNowAsync(CancellationToken.None);

        Assert.Empty(sent.Packets);
    }

    [Fact]
    public async Task Announcer_OneInterfaceFails_OthersStillSent()
    {
        var sent = new CapturingSender { FailFor = IPAddress.Parse("10.0.0.2") };
        using MulticastListener listener = NewListener(NewRegistry(), () => Identity, sent,
            Addresses("10.0.0.2/24", "10.0.1.2/24"));

        await listener.AnnounceNowAsync(CancellationToken.None);

        Assert.Equal([IPAddress.Parse("10.0.1.2")], sent.Packets.Select(p => p.Local));
    }

    [Fact]
    public async Task Announcer_RepeatsOnItsInterval()
    {
        var sent = new CapturingSender();
        using MulticastListener listener = NewListener(NewRegistry(), () => Identity, sent, Addresses("10.0.0.2/24"),
            TimeSpan.FromMilliseconds(50));

        listener.StartAnnouncing();

        await WaitUntilAsync(() => sent.Packets.Count >= 3, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Listener_ReceivedAnnouncement_UpsertsDeviceAtSourceAddress()
    {
        DeviceRegistry registry = NewRegistry();
        using MulticastListener listener = NewListener(registry, () => Identity, new CapturingSender(), Addresses());
        byte[] datagram = Encoding.UTF8.GetBytes(
            """{"alias":"Pixel","version":"2.1","deviceModel":"Pixel 8","deviceType":"mobile","fingerprint":"abc123","port":53317,"protocol":"http","download":false,"announce":true}""");

        listener.HandleDatagram(datagram, IPAddress.Parse("198.51.100.203"));
        listener.HandleDatagram("garbage"u8, IPAddress.Parse("198.51.100.204"));

        DiscoveredDevice device = Assert.Single(registry.Snapshot(DateTimeOffset.UtcNow));
        Assert.Equal("Pixel", device.Alias);
        Assert.Equal(IPAddress.Parse("198.51.100.203"), device.Ip);
        Assert.Equal("http", device.Protocol);
    }

    [Fact]
    public void Listener_OwnAnnouncementEcho_Ignored()
    {
        DeviceRegistry registry = NewRegistry();
        using MulticastListener listener = NewListener(registry, () => Identity, new CapturingSender(), Addresses());
        byte[] echo = JsonSerializer.SerializeToUtf8Bytes(
            new Announcement(Identity.Alias, Identity.Version, Identity.DeviceModel, Identity.DeviceType,
                Identity.Fingerprint, Identity.Port, Identity.Protocol, Identity.Download, true),
            LocalSendJson.Options);

        listener.HandleDatagram(echo, IPAddress.Parse("198.51.100.170"));

        Assert.Empty(registry.Snapshot(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Listener_ThrowingEventHandler_DoesNotEscape()
    {
        DeviceRegistry registry = NewRegistry();
        registry.DeviceAdded += _ => throw new InvalidOperationException("handler bug");
        using MulticastListener listener = NewListener(registry, () => Identity, new CapturingSender(), Addresses());

        listener.HandleDatagram(AnnouncementBytes("PEER1", 53317, "http", announce: true), IPAddress.Parse("10.0.0.9"));
    }

    [Fact]
    public async Task Listener_AnnounceFromPeer_RegistersOriginalIdentityToIt()
    {
        FakeLocalSendServer fake = await StartFakeAsync(https: false, "127.0.0.8");
        DeviceRegistry registry = NewRegistry();
        using SubnetScanner scanner = NewScanner(registry);
        using var listener = new MulticastListener(registry, () => Identity, Addresses(), new CapturingSender().SendAsync,
            register: scanner.RegisterAsync);

        listener.HandleDatagram(AnnouncementBytes("PEER1", fake.Port, "http", announce: true), fake.Address);

        await WaitUntilAsync(() => fake.Registrations.Count > 0, TimeSpan.FromSeconds(5));
        await Task.Delay(300);
        Assert.Equal([Identity], fake.Registrations);
    }

    [Theory]
    [InlineData("PEER1", false, true, false)] // announce flag off
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", true, true, false)] // original, lowercase
    [InlineData("AA11BB22CC33DD44EE55FF6600778899AA11BB22CC33DD44EE55FF6600778899", true, true, false)] // private, uppercase
    [InlineData("PEER1", true, false, false)] // no original identity
    [InlineData("PEER1", true, true, true)]
    public async Task Listener_AnswersOnlyAnnouncingPeers(string fingerprint, bool announce, bool haveIdentity, bool answers)
    {
        var recorder = new RegisterRecorder();
        using var listener = new MulticastListener(NewRegistry(), () => haveIdentity ? Identity : null, Addresses(),
            new CapturingSender().SendAsync, register: recorder.RegisterAsync);

        listener.HandleDatagram(AnnouncementBytes(fingerprint, 53317, "http", announce), IPAddress.Parse("127.0.0.9"));
        await Task.Delay(300);

        ScanTarget[] expected = answers ? [new ScanTarget(IPAddress.Parse("127.0.0.9"), 53317, "http")] : [];
        Assert.Equal(expected, recorder.Targets);
    }

    [Fact]
    public async Task Listener_AnnounceFromOwnAddress_NotAnswered()
    {
        var recorder = new RegisterRecorder();
        using var listener = new MulticastListener(NewRegistry(), () => Identity, Addresses("127.0.0.9/24"),
            new CapturingSender().SendAsync, register: recorder.RegisterAsync);

        listener.HandleDatagram(AnnouncementBytes("PEER1", 53317, "http", announce: true), IPAddress.Parse("127.0.0.9"));
        await Task.Delay(300);

        Assert.Empty(recorder.Targets);
    }

    [Fact]
    public void Listener_DatagramFromOwnAddress_NotListed()
    {
        // While the official app's identity is unknown (or it runs in http mode) its announcements cannot be told
        // apart by fingerprint; their source address still gives them away.
        DeviceRegistry registry = NewRegistry();
        using MulticastListener listener = NewListener(registry, () => null, new CapturingSender(),
            Addresses("10.0.0.5/24", "192.168.1.5/24"));

        listener.HandleDatagram(AnnouncementBytes("UNKNOWN-ORIGINAL", 53317, "http", announce: true), IPAddress.Parse("10.0.0.5"));
        listener.HandleDatagram(AnnouncementBytes("UNKNOWN-ORIGINAL", 53317, "http", announce: true), IPAddress.Parse("192.168.1.5"));
        listener.HandleDatagram(AnnouncementBytes("PEER1", 53317, "http", announce: true), IPAddress.Parse("10.0.0.6"));

        DiscoveredDevice device = Assert.Single(registry.Snapshot(DateTimeOffset.UtcNow));
        Assert.Equal(IPAddress.Parse("10.0.0.6"), device.Ip);
    }

    [Fact]
    public async Task Announcer_ThrowingIteration_IsReportedAndTheLoopGoesOn()
    {
        var sent = new CapturingSender();
        var errors = new List<Exception>();
        int calls = 0;
        DeviceInfo? Identity_()
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new InvalidOperationException("identity lookup bug");
            }

            return Identity;
        }

        using var listener = new MulticastListener(NewRegistry(), Identity_, Addresses("10.0.0.2/24"), sent.SendAsync,
            announceInterval: TimeSpan.FromMilliseconds(50), onError: e => { lock (errors) { errors.Add(e); } });

        listener.StartAnnouncing();

        await WaitUntilAsync(() => sent.Packets.Count >= 2, TimeSpan.FromSeconds(5));
        lock (errors)
        {
            Assert.IsType<InvalidOperationException>(Assert.Single(errors));
        }
    }

    [Fact]
    public async Task Listener_AnswersEachIpAtMostOncePerTenSeconds()
    {
        var recorder = new RegisterRecorder();
        DateTimeOffset now = T0;
        using var listener = new MulticastListener(NewRegistry(), () => Identity, Addresses(),
            new CapturingSender().SendAsync, clock: () => now, register: recorder.RegisterAsync);
        byte[] datagram = AnnouncementBytes("PEER1", 53317, "http", announce: true);

        listener.HandleDatagram(datagram, IPAddress.Parse("127.0.0.9"));
        now += TimeSpan.FromSeconds(9);
        listener.HandleDatagram(datagram, IPAddress.Parse("127.0.0.9"));
        listener.HandleDatagram(datagram, IPAddress.Parse("127.0.0.10"));
        await WaitUntilAsync(() => recorder.Targets.Count >= 2, TimeSpan.FromSeconds(5));
        await Task.Delay(300);
        Assert.Equal(2, recorder.Targets.Count);

        now += TimeSpan.FromSeconds(2);
        listener.HandleDatagram(datagram, IPAddress.Parse("127.0.0.9"));
        await WaitUntilAsync(() => recorder.Targets.Count >= 3, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Memberships_LeaveVanishedAndRejoinReturning()
    {
        var tracker = new MembershipTracker();
        var joins = new List<IPAddress>();
        var leaves = new List<IPAddress>();
        IPAddress wifi = IPAddress.Parse("198.51.100.170");
        IPAddress usb = IPAddress.Parse("203.0.113.1");
        bool usbJoinWorks = false;
        bool Join(IPAddress a)
        {
            joins.Add(a);
            return !a.Equals(usb) || usbJoinWorks;
        }

        tracker.Update([wifi, usb], Join, leaves.Add);
        Assert.Equal([wifi], tracker.Joined);

        usbJoinWorks = true;
        tracker.Update([wifi, usb], Join, leaves.Add); // failed join retried
        tracker.Update([usb], Join, leaves.Add); // Wi-Fi drops
        tracker.Update([wifi, usb], Join, leaves.Add); // Wi-Fi back with the same IP

        Assert.Equal([wifi, usb, usb, wifi], joins);
        Assert.Equal([wifi], leaves);
        Assert.Equal(new HashSet<IPAddress> { wifi, usb }, tracker.Joined.ToHashSet());
    }

    // ---------------------------------------------------------------- scanner (loopback fakes only)

    [Fact]
    public async Task Scanner_RegistersUsingOriginalIdentity()
    {
        FakeLocalSendServer fake = await StartFakeAsync(https: true, "127.0.0.1");
        using SubnetScanner scanner = NewScanner(NewRegistry());

        await scanner.ScanAsync([IPAddress.Loopback], fake.Port, CancellationToken.None);

        Assert.Equal([Identity], fake.Registrations);
    }

    [Fact]
    public async Task Scanner_RespondingHost_AddedToRegistry()
    {
        FakeLocalSendServer https = await StartFakeAsync(https: true, "127.0.0.2");
        FakeLocalSendServer http = await StartFakeAsync(https: false, "127.0.0.3", https.Port);
        http.Alias = "Pixel";
        https.Alias = "Laptop";
        DeviceRegistry registry = NewRegistry();
        using SubnetScanner scanner = NewScanner(registry);

        await scanner.ScanAsync([IPAddress.Parse("127.0.0.2"), IPAddress.Parse("127.0.0.3")], https.Port,
            CancellationToken.None);

        IReadOnlyList<DiscoveredDevice> devices = registry.Snapshot(DateTimeOffset.UtcNow);
        Assert.Equal(["Laptop", "Pixel"], devices.Select(d => d.Alias));
        DiscoveredDevice laptop = devices[0];
        Assert.Equal(https.Fingerprint, laptop.Fingerprint);
        Assert.Equal(IPAddress.Parse("127.0.0.2"), laptop.Ip);
        Assert.Equal(https.Port, laptop.Port);
        Assert.Equal("https", laptop.Protocol);
        DiscoveredDevice pixel = devices[1];
        Assert.Equal(IPAddress.Parse("127.0.0.3"), pixel.Ip);
        Assert.Equal("http", pixel.Protocol);
        Assert.Equal("mobile", pixel.DeviceType);
    }

    [Fact]
    public async Task Scanner_KnownProtocol_TriesOnlyThatProtocol()
    {
        FakeLocalSendServer http = await StartFakeAsync(https: false, "127.0.0.4");
        DeviceRegistry registry = NewRegistry();
        using SubnetScanner scanner = NewScanner(registry);

        await scanner.ScanAsync([new ScanTarget(IPAddress.Parse("127.0.0.4"), http.Port, "https")],
            CancellationToken.None);

        Assert.Empty(registry.Snapshot(DateTimeOffset.UtcNow));
        Assert.Empty(http.Registrations);
    }

    [Fact]
    public async Task Scanner_NoOriginalIdentity_RegistersNowhere()
    {
        FakeLocalSendServer fake = await StartFakeAsync(https: false, "127.0.0.1");
        DeviceRegistry registry = NewRegistry();
        using var scanner = new SubnetScanner(registry, NewClient(), () => null);

        await scanner.ScanAsync([IPAddress.Loopback], fake.Port, CancellationToken.None);

        Assert.Empty(fake.Urls);
        Assert.Empty(registry.Snapshot(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Scanner_ResponderRunningThisProgram_GetsPrivateFingerprint()
    {
        (PrivateReceiveServer server, int privatePort) = await StartPrivateAsync();
        FakeLocalSendServer fake = await StartFakeAsync(https: false, "127.0.0.1");
        DeviceRegistry registry = NewRegistry();
        using var scanner = new SubnetScanner(registry, NewClient(privatePort), () => Identity);

        await scanner.ScanAsync([IPAddress.Loopback], fake.Port, CancellationToken.None);

        Assert.Equal(server.Fingerprint, Assert.Single(registry.Snapshot(DateTimeOffset.UtcNow)).PrivateFingerprint);
    }

    [Fact]
    public async Task Scanner_SilentHosts_DoNotBlockBeyondTimeout()
    {
        // 20 loopback addresses that accept TCP connections and never answer, so every request runs into the timeout.
        int port = FreePort.Get();
        var hosts = new List<IPAddress>();
        for (int i = 10; i < 30; i++)
        {
            var address = IPAddress.Parse($"127.0.0.{i}");
            var silent = new TcpListener(address, port);
            silent.Start();
            _disposables.Add(silent.Server);
            hosts.Add(address);
        }

        DeviceRegistry registry = NewRegistry();
        using SubnetScanner scanner = NewScanner(registry);

        var watch = Stopwatch.StartNew();
        await scanner.ScanAsync(hosts, port, CancellationToken.None);
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"scan took {watch.Elapsed}");
        Assert.Empty(registry.Snapshot(DateTimeOffset.UtcNow));
    }

    // ---------------------------------------------------------------- known hosts

    [Fact]
    public void KnownHosts_SaveAndReload_RoundTrips()
    {
        string path = Path.Combine(_root, "state", "known-hosts.json");
        var store = new KnownHostsStore(path);
        store.Record(IPAddress.Parse("198.51.100.203"), 53317, "http", T0);
        store.Save(T0);

        var reloaded = new KnownHostsStore(path);

        Assert.Equal([new KnownHost(IPAddress.Parse("198.51.100.203"), 53317, "http", T0)], reloaded.Hosts(T0));
    }

    [Fact]
    public void KnownHosts_RecordSameIp_Updates()
    {
        var store = new KnownHostsStore(Path.Combine(_root, "known-hosts.json"));
        store.Record(IPAddress.Parse("10.0.0.5"), 53317, "http", T0);
        store.Record(IPAddress.Parse("10.0.0.5"), 53318, "https", T0 + TimeSpan.FromHours(1));

        KnownHost host = Assert.Single(store.Hosts(T0 + TimeSpan.FromHours(1)));
        Assert.Equal(53318, host.Port);
        Assert.Equal("https", host.Protocol);
        Assert.Equal(T0 + TimeSpan.FromHours(1), host.LastSeen);
    }

    [Fact]
    public void KnownHosts_KeepsAtMostFiftyNewest()
    {
        string path = Path.Combine(_root, "known-hosts.json");
        var store = new KnownHostsStore(path);
        for (int i = 1; i <= 60; i++)
        {
            store.Record(IPAddress.Parse($"10.0.0.{i}"), 53317, "http", T0 + TimeSpan.FromMinutes(i));
        }

        store.Save(T0 + TimeSpan.FromHours(2));
        IReadOnlyList<KnownHost> hosts = new KnownHostsStore(path).Hosts(T0 + TimeSpan.FromHours(2));

        Assert.Equal(50, hosts.Count);
        Assert.DoesNotContain(hosts, h => h.Ip.Equals(IPAddress.Parse("10.0.0.10")));
        Assert.Contains(hosts, h => h.Ip.Equals(IPAddress.Parse("10.0.0.11")));
    }

    [Fact]
    public void KnownHosts_DropsEntriesOlderThanSevenDays()
    {
        string path = Path.Combine(_root, "known-hosts.json");
        var store = new KnownHostsStore(path);
        store.Record(IPAddress.Parse("10.0.0.1"), 53317, "http", T0);
        store.Record(IPAddress.Parse("10.0.0.2"), 53317, "http", T0 + TimeSpan.FromDays(1));
        DateTimeOffset later = T0 + TimeSpan.FromDays(7) + TimeSpan.FromMinutes(1);

        Assert.Equal([IPAddress.Parse("10.0.0.2")], store.Hosts(later).Select(h => h.Ip));
        store.Save(later);
        Assert.Equal([IPAddress.Parse("10.0.0.2")], new KnownHostsStore(path).Hosts(later).Select(h => h.Ip));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""[{"ip":"not an ip","port":53317,"protocol":"http","lastSeen":"2026-10-19T12:00:00+00:00"}]""")]
    public void KnownHosts_CorruptFileOrEntry_IsIgnored(string content)
    {
        string path = Path.Combine(_root, "known-hosts.json");
        File.WriteAllText(path, content);

        Assert.Empty(new KnownHostsStore(path).Hosts(T0));
    }

    [Fact]
    public void KnownHosts_DefaultPath_IsUnderLocalAppData()
    {
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "localsend-dropzone", "known-hosts.json"),
            KnownHostsStore.DefaultPath);
    }

    // ---------------------------------------------------------------- service

    [Fact]
    public async Task Service_Refresh_AnnouncesOriginalIdentity()
    {
        var sent = new CapturingSender();
        DeviceRegistry registry = NewRegistry();
        using MulticastListener listener = NewListener(registry, () => Identity, sent, Addresses("10.0.0.2/24"));
        using SubnetScanner scanner = NewScanner(registry);
        using var service = new DiscoveryService(registry, listener, scanner, NewClient(),
            localAddresses: Addresses());

        await service.RefreshNowAsync();

        (IPAddress local, byte[] payload) = Assert.Single(sent.Packets);
        Assert.Equal(IPAddress.Parse("10.0.0.2"), local);
        Assert.Equal(Identity,
            JsonSerializer.Deserialize<Announcement>(payload, LocalSendJson.Options)!.ToDeviceInfo());
    }

    [Fact]
    public async Task Service_Refresh_RegistersToKnownHostsAndRecordsThem()
    {
        FakeLocalSendServer fake = await StartFakeAsync(https: false, "127.0.0.5");
        string path = Path.Combine(_root, "known-hosts.json");
        var store = new KnownHostsStore(path);
        store.Record(IPAddress.Parse("127.0.0.5"), fake.Port, "http", DateTimeOffset.UtcNow - TimeSpan.FromDays(1));
        DeviceRegistry registry = NewRegistry();
        using SubnetScanner scanner = NewScanner(registry);
        using var service = new DiscoveryService(registry, null, scanner, NewClient(), store, Addresses());

        await service.RefreshNowAsync();

        Assert.Equal([Identity], fake.Registrations);
        DiscoveredDevice device = Assert.Single(registry.Snapshot(DateTimeOffset.UtcNow));
        Assert.Equal(IPAddress.Parse("127.0.0.5"), device.Ip);
        KnownHost saved = Assert.Single(new KnownHostsStore(path).Hosts(DateTimeOffset.UtcNow));
        Assert.True(DateTimeOffset.UtcNow - saved.LastSeen < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Service_Refresh_ScansLocalSubnets()
    {
        FakeLocalSendServer fake = await StartFakeAsync(https: false, "127.0.0.7");
        DeviceRegistry registry = NewRegistry();
        using SubnetScanner scanner = NewScanner(registry);
        using var service = new DiscoveryService(registry, null, scanner, NewClient(),
            localAddresses: Addresses("127.0.0.1/24"), subnetPort: fake.Port);

        await service.RefreshNowAsync();

        Assert.Equal(IPAddress.Parse("127.0.0.7"), Assert.Single(registry.Snapshot(DateTimeOffset.UtcNow)).Ip);
    }

    [Fact]
    public async Task Service_Refresh_SkipsOwnAddresses()
    {
        FakeLocalSendServer fake = await StartFakeAsync(https: false, "127.0.0.6");
        var store = new KnownHostsStore(Path.Combine(_root, "known-hosts.json"));
        store.Record(IPAddress.Parse("127.0.0.6"), fake.Port, "http", DateTimeOffset.UtcNow);
        DeviceRegistry registry = NewRegistry();
        using SubnetScanner scanner = NewScanner(registry);
        using var service = new DiscoveryService(registry, null, scanner, NewClient(), store,
            Addresses("127.0.0.6/32"));

        await service.RefreshNowAsync();

        Assert.Empty(fake.Urls);
    }

    [Fact]
    public async Task Service_ConcurrentRefresh_SharesTheRunningTask()
    {
        var gate = new TaskCompletionSource();
        var sent = new CapturingSender { Gate = gate.Task };
        DeviceRegistry registry = NewRegistry();
        using MulticastListener listener = NewListener(registry, () => Identity, sent, Addresses("10.0.0.2/24"));
        using SubnetScanner scanner = NewScanner(registry);
        using var service = new DiscoveryService(registry, listener, scanner, NewClient(), localAddresses: Addresses());

        Task first = service.RefreshNowAsync();
        Task second = service.RefreshNowAsync();
        gate.SetResult();
        await Task.WhenAll(first, second);

        Assert.Same(first, second);
        Assert.Single(sent.Packets);
        Task third = service.RefreshNowAsync();
        Assert.NotSame(first, third);
        await third;
    }

    [Fact]
    public void Service_Dispose_DisposesItsListener()
    {
        DeviceRegistry registry = NewRegistry();
        MulticastListener listener = NewListener(registry, () => Identity, new CapturingSender(), Addresses());
        using SubnetScanner scanner = NewScanner(registry);
        var service = new DiscoveryService(registry, listener, scanner, NewClient(), localAddresses: Addresses());

        service.Dispose();

        Assert.Throws<ObjectDisposedException>(listener.StartAnnouncing);
    }

    [Fact]
    public async Task Service_DeviceAdded_ProbesPrivatePort()
    {
        (PrivateReceiveServer server, int privatePort) = await StartPrivateAsync();
        DeviceRegistry registry = NewRegistry();
        using SubnetScanner scanner = NewScanner(registry);
        using var service = new DiscoveryService(registry, null, scanner, NewClient(privatePort),
            localAddresses: Addresses());
        service.Start();

        // As if the device had only been heard through multicast.
        registry.Upsert(Peer("Other PC", "ABCD"), IPAddress.Loopback, DateTimeOffset.UtcNow);

        await WaitUntilAsync(
            () => registry.Snapshot(DateTimeOffset.UtcNow).Single().PrivateFingerprint == server.Fingerprint,
            TimeSpan.FromSeconds(5));
    }

    // ---------------------------------------------------------------- helpers

    private static DeviceRegistry NewRegistry() =>
        new(() => new HashSet<string> { Identity.Fingerprint, OwnPrivateFingerprint });

    private static DeviceInfo Peer(string alias, string fingerprint) =>
        new(alias, "2.1", "Pixel 8", "mobile", fingerprint, 53317, "http", false);

    private static Func<IReadOnlyList<LocalAddress>> Addresses(params string[] cidrs) =>
        () => cidrs.Select(c =>
        {
            string[] parts = c.Split('/');
            return new LocalAddress(IPAddress.Parse(parts[0]), int.Parse(parts[1]));
        }).ToList();

    private static MulticastListener NewListener(
        DeviceRegistry registry, Func<DeviceInfo?> identity, CapturingSender sender,
        Func<IReadOnlyList<LocalAddress>> addresses, TimeSpan? interval = null) =>
        new(registry, identity, addresses, sender.SendAsync, announceInterval: interval);

    /// <summary>The private port points at a port nobody listens on unless a test passes one.</summary>
    private static TransferClient NewClient(int? privatePort = null) =>
        new(() => Identity, () => null, privatePort ?? FreePort.Get());

    private static SubnetScanner NewScanner(DeviceRegistry registry) => new(registry, NewClient(), () => Identity);

    private async Task<FakeLocalSendServer> StartFakeAsync(bool https, string address, int? port = null)
    {
        FakeLocalSendServer fake = await FakeLocalSendServer.StartAsync(
            Path.Combine(_root, "fake-" + Guid.NewGuid().ToString("N")), https, port, IPAddress.Parse(address));
        _servers.Add(fake);
        return fake;
    }

    private async Task<(PrivateReceiveServer Server, int Port)> StartPrivateAsync()
    {
        X509Certificate2 certificate = CertificateStore.LoadOrCreate(
            Path.Combine(_root, "private-" + Guid.NewGuid().ToString("N"), "cert.bin"));
        _disposables.Add(certificate);
        int port = FreePort.Get();
        string downloads = Path.Combine(_root, "downloads");
        Directory.CreateDirectory(downloads);
        var server = new PrivateReceiveServer(new PrivateServerOptions(
            port, certificate, () => "pw", downloads, () => DateTimeOffset.UtcNow, IPAddress.Loopback));
        await server.StartAsync(CancellationToken.None);
        _servers.Add(server);
        return (server, port);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < timeout, "condition not met in time");
            await Task.Delay(20);
        }
    }

    private static byte[] AnnouncementBytes(string fingerprint, int port, string protocol, bool announce) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new Announcement("Peer", "2.1", "Pixel 8", "mobile", fingerprint, port, protocol, false, announce),
            LocalSendJson.Options);

    /// <summary>Stands in for the register answer: records each target it was asked to register to.</summary>
    private sealed class RegisterRecorder
    {
        private readonly object _gate = new();
        private readonly List<ScanTarget> _targets = [];

        public IReadOnlyList<ScanTarget> Targets
        {
            get { lock (_gate) { return [.. _targets]; } }
        }

        public Task RegisterAsync(ScanTarget target, CancellationToken ct)
        {
            lock (_gate)
            {
                _targets.Add(target);
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>Stands in for the UDP multicast send: records what would have gone out on which interface.</summary>
    private sealed class CapturingSender
    {
        private readonly object _gate = new();
        private readonly List<(IPAddress Local, byte[] Payload)> _packets = [];

        public IPAddress? FailFor { get; init; }

        public Task? Gate { get; init; }

        public IReadOnlyList<(IPAddress Local, byte[] Payload)> Packets
        {
            get { lock (_gate) { return [.. _packets]; } }
        }

        public async Task SendAsync(IPAddress local, byte[] payload, CancellationToken ct)
        {
            if (Gate is not null)
            {
                await Gate;
            }

            if (local.Equals(FailFor))
            {
                throw new SocketException((int)SocketError.NetworkUnreachable);
            }

            lock (_gate)
            {
                _packets.Add((local, payload));
            }
        }
    }
}
