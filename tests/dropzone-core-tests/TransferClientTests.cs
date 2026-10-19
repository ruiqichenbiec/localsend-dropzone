using System.Diagnostics;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using Dropzone.Core.Discovery;
using Dropzone.Core.Protocol;
using Dropzone.Core.Receive;
using Dropzone.Core.Send;
using Dropzone.Core.Tests.TestSupport;

namespace Dropzone.Core.Tests;

public sealed class TransferClientTests : IAsyncLifetime
{
    private const string Password = "shared secret 7";

    private static readonly DeviceInfo Identity = new(
        "MyPC", "2.2", "Windows", "desktop",
        "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF", 53317, "https", false);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "dropzone-core-tests", Guid.NewGuid().ToString("N"));
    private readonly string _source;
    private readonly string _privateDownloads;
    private readonly List<IAsyncDisposable> _servers = [];
    private readonly List<X509Certificate2> _certificates = [];

    public TransferClientTests()
    {
        _source = Path.Combine(_root, "source");
        _privateDownloads = Path.Combine(_root, "private-downloads");
        Directory.CreateDirectory(_source);
        Directory.CreateDirectory(_privateDownloads);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (IAsyncDisposable server in _servers)
        {
            await server.DisposeAsync();
        }

        foreach (X509Certificate2 certificate in _certificates)
        {
            certificate.Dispose();
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Probe_PrivateServer_ReturnsItsFingerprint()
    {
        PrivateReceiveServer server = await StartPrivateAsync(Password);
        var client = new TransferClient(() => Identity, () => Password, PrivatePortOf(server));

        string? fingerprint = await client.ProbePrivateAsync(IPAddress.Loopback, CancellationToken.None);

        Assert.Equal(server.Fingerprint, fingerprint);
    }

    [Fact]
    public async Task Probe_FakeOriginal_ReturnsNull()
    {
        FakeLocalSendServer fake = await StartFakeAsync();
        var client = new TransferClient(() => Identity, () => Password, fake.Port);

        string? fingerprint = await client.ProbePrivateAsync(IPAddress.Loopback, CancellationToken.None);

        Assert.Null(fingerprint);
    }

    [Fact]
    public async Task Probe_NobodyListening_ReturnsNullWithinTimeout()
    {
        var client = new TransferClient(() => Identity, () => Password, FreePort.Get());
        var watch = Stopwatch.StartNew();

        string? fingerprint = await client.ProbePrivateAsync(IPAddress.Loopback, CancellationToken.None);

        Assert.Null(fingerprint);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"Took {watch.Elapsed}.");
    }

    [Fact]
    public async Task Send_SamePassword_UsesPrivateChannel_Sent()
    {
        PrivateReceiveServer server = await StartPrivateAsync(Password);
        FakeLocalSendServer fake = await StartFakeAsync();
        string file = WriteSource("photo.jpg", "private bytes");
        var client = new TransferClient(() => Identity, () => Password, PrivatePortOf(server));

        SendResult result = await client.SendAsync(Target(fake, server.Fingerprint), [file], NoPin, CancellationToken.None);

        Assert.Equal(SendOutcome.Sent, result.Outcome);
        Assert.Equal("private bytes", File.ReadAllText(Path.Combine(_privateDownloads, "photo.jpg")));
        Assert.Empty(fake.Urls);
    }

    [Fact]
    public async Task Send_WrongPassword_FallsBackToOriginal()
    {
        PrivateReceiveServer server = await StartPrivateAsync("a different password");
        FakeLocalSendServer fake = await StartFakeAsync();
        string file = WriteSource("note.txt", "fallback bytes");
        var client = new TransferClient(() => Identity, () => Password, PrivatePortOf(server));

        SendResult result = await client.SendAsync(Target(fake, server.Fingerprint), [file], NoPin, CancellationToken.None);

        Assert.Equal(SendOutcome.SentAfterPasswordMismatch, result.Outcome);
        Assert.Equal("fallback bytes", File.ReadAllText(Path.Combine(fake.Directory, "note.txt")));
        Assert.Empty(Directory.GetFiles(_privateDownloads, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Send_ClockSkewThreeMinutes_FallsBackToOriginal()
    {
        PrivateReceiveServer server = await StartPrivateAsync(Password);
        FakeLocalSendServer fake = await StartFakeAsync();
        string file = WriteSource("skew.txt", "skewed");
        var client = new TransferClient(
            () => Identity, () => Password, PrivatePortOf(server), () => DateTimeOffset.UtcNow.AddMinutes(3));

        SendResult result = await client.SendAsync(Target(fake, server.Fingerprint), [file], NoPin, CancellationToken.None);

        Assert.Equal(SendOutcome.SentAfterPasswordMismatch, result.Outcome);
        Assert.True(File.Exists(Path.Combine(fake.Directory, "skew.txt")));
    }

    [Fact]
    public async Task Send_NoLocalPassword_SkipsPrivateChannel()
    {
        PrivateReceiveServer server = await StartPrivateAsync(Password);
        FakeLocalSendServer fake = await StartFakeAsync();
        string file = WriteSource("plain.txt", "plain");
        var client = new TransferClient(() => Identity, () => "   ", PrivatePortOf(server));

        SendResult result = await client.SendAsync(Target(fake, server.Fingerprint), [file], NoPin, CancellationToken.None);

        Assert.Equal(SendOutcome.Sent, result.Outcome);
        Assert.True(File.Exists(Path.Combine(fake.Directory, "plain.txt")));
        Assert.Empty(Directory.GetFiles(_privateDownloads, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Send_PinRequired_AsksAndRetries_Sent()
    {
        FakeLocalSendServer fake = await StartFakeAsync();
        fake.Mode = FakeMode.RequirePin;
        string file = WriteSource("pin.txt", "pinned");
        int asked = 0;
        var client = NormalOnlyClient();

        SendResult result = await client.SendAsync(Target(fake), [file], () =>
        {
            asked++;
            return Task.FromResult<string?>(FakeLocalSendServer.RequiredPin);
        }, CancellationToken.None);

        Assert.Equal(SendOutcome.Sent, result.Outcome);
        Assert.Equal(1, asked);
        Assert.Equal("pinned", File.ReadAllText(Path.Combine(fake.Directory, "pin.txt")));
        Assert.Contains(fake.Urls, url => url.Contains("pin=1234", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Send_PinRequired_UserCancels_Cancelled()
    {
        FakeLocalSendServer fake = await StartFakeAsync();
        fake.Mode = FakeMode.RequirePin;
        string file = WriteSource("pin.txt", "pinned");

        SendResult result = await NormalOnlyClient().SendAsync(Target(fake), [file], NoPin, CancellationToken.None);

        Assert.Equal(SendOutcome.Cancelled, result.Outcome);
        Assert.False(File.Exists(Path.Combine(fake.Directory, "pin.txt")));
    }

    [Fact]
    public async Task Send_PinWrongTwice_Failed()
    {
        FakeLocalSendServer fake = await StartFakeAsync();
        fake.Mode = FakeMode.RequirePin;
        string file = WriteSource("pin.txt", "pinned");

        SendResult result = await NormalOnlyClient().SendAsync(
            Target(fake), [file], () => Task.FromResult<string?>("9999"), CancellationToken.None);

        Assert.Equal(SendOutcome.Failed, result.Outcome);
        Assert.Equal("PIN 不正确", result.Detail);
    }

    [Fact]
    public async Task Send_Rejected_403()
    {
        FakeLocalSendServer fake = await StartFakeAsync();
        fake.Mode = FakeMode.Reject;

        SendResult result = await NormalOnlyClient().SendAsync(
            Target(fake), [WriteSource("a.txt", "a")], NoPin, CancellationToken.None);

        Assert.Equal(SendOutcome.Rejected, result.Outcome);
    }

    [Theory]
    [InlineData(FakeMode.Busy)]
    [InlineData(FakeMode.TooManyRequests)]
    public async Task Send_Busy_409(FakeMode mode)
    {
        FakeLocalSendServer fake = await StartFakeAsync();
        fake.Mode = mode;

        SendResult result = await NormalOnlyClient().SendAsync(
            Target(fake), [WriteSource("a.txt", "a")], NoPin, CancellationToken.None);

        Assert.Equal(SendOutcome.Busy, result.Outcome);
    }

    [Fact]
    public async Task Send_NoContent_204_SentWithoutUpload()
    {
        FakeLocalSendServer fake = await StartFakeAsync();
        fake.Mode = FakeMode.NoContent;

        SendResult result = await NormalOnlyClient().SendAsync(
            Target(fake), [WriteSource("a.txt", "a")], NoPin, CancellationToken.None);

        Assert.Equal(SendOutcome.Sent, result.Outcome);
        Assert.DoesNotContain(fake.Urls, url => url.StartsWith("/api/localsend/v2/upload", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Send_UploadFails_CancelsSession_Failed()
    {
        FakeLocalSendServer fake = await StartFakeAsync();
        fake.Mode = FakeMode.FailUploads;

        SendResult result = await NormalOnlyClient().SendAsync(
            Target(fake), [WriteSource("a.txt", "a")], NoPin, CancellationToken.None);

        Assert.Equal(SendOutcome.Failed, result.Outcome);
        Assert.Equal(1, fake.CancelCount);
    }

    [Fact]
    public async Task Send_UsesOriginalIdentityAsInfo()
    {
        FakeLocalSendServer fake = await StartFakeAsync();
        string file = WriteSource("id.txt", "identity");

        SendResult result = await NormalOnlyClient().SendAsync(Target(fake), [file], NoPin, CancellationToken.None);

        Assert.Equal(SendOutcome.Sent, result.Outcome);
        DeviceInfo info = Assert.Single(fake.Infos);
        Assert.Equal(Identity.Alias, info.Alias);
        Assert.Equal(Identity.Fingerprint, info.Fingerprint);
        Assert.Equal(Identity.Port, info.Port);
        Assert.Equal(Identity.Protocol, info.Protocol);
    }

    [Fact]
    public async Task Send_NoOriginalIdentity_Failed()
    {
        FakeLocalSendServer fake = await StartFakeAsync();
        var client = new TransferClient(() => null, () => null, FreePort.Get());

        SendResult result = await client.SendAsync(
            Target(fake), [WriteSource("a.txt", "a")], NoPin, CancellationToken.None);

        Assert.Equal(SendOutcome.Failed, result.Outcome);
        Assert.Empty(fake.Urls);
    }

    [Fact]
    public async Task Send_PrepareBody_DescribesFiles()
    {
        FakeLocalSendServer fake = await StartFakeAsync();
        string file = WriteSource("pic.png", "12345");
        var modified = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, modified);

        await NormalOnlyClient().SendAsync(Target(fake), [file], NoPin, CancellationToken.None);

        PrepareUploadRequest request = System.Text.Json.JsonSerializer.Deserialize<PrepareUploadRequest>(
            fake.Bodies[0], LocalSendJson.Options)!;
        FileDto dto = Assert.Single(request.Files.Values);
        Assert.Equal("pic.png", dto.FileName);
        Assert.Equal(5, dto.Size);
        Assert.Equal("image/png", dto.FileType);
        Assert.Equal(32, dto.Id.Length);
        Assert.Equal(modified, DateTime.Parse(dto.Metadata!.Modified!, null, System.Globalization.DateTimeStyles.RoundtripKind));
    }

    [Fact]
    public async Task Send_HttpTarget_Sent()
    {
        FakeLocalSendServer fake = await StartFakeAsync(https: false);
        string file = WriteSource("phone.txt", "to the phone");

        SendResult result = await NormalOnlyClient().SendAsync(Target(fake), [file], NoPin, CancellationToken.None);

        Assert.Equal(SendOutcome.Sent, result.Outcome);
        Assert.Equal("to the phone", File.ReadAllText(Path.Combine(fake.Directory, "phone.txt")));
    }

    [Fact]
    public async Task Send_HttpsFingerprintMismatch_Failed()
    {
        FakeLocalSendServer fake = await StartFakeAsync();
        DiscoveredDevice target = Target(fake) with { Fingerprint = new string('A', 64) };

        SendResult result = await NormalOnlyClient().SendAsync(
            target, [WriteSource("a.txt", "a")], NoPin, CancellationToken.None);

        Assert.Equal(SendOutcome.Failed, result.Outcome);
        Assert.Empty(fake.Urls);
    }

    [Fact]
    public async Task Send_TargetOffline_FailsFast()
    {
        var target = new DiscoveredDevice(
            "Gone", "desktop", new string('B', 64), IPAddress.Loopback, FreePort.Get(), "https", null, DateTimeOffset.UtcNow);
        var watch = Stopwatch.StartNew();

        SendResult result = await NormalOnlyClient().SendAsync(
            target, [WriteSource("a.txt", "a")], NoPin, CancellationToken.None);

        Assert.Equal(SendOutcome.Failed, result.Outcome);
        Assert.True(watch.Elapsed <= TimeSpan.FromSeconds(6), $"Took {watch.Elapsed}.");
    }

    [Fact]
    public async Task Send_PrivateFingerprintMismatch_DoesNotSendToken()
    {
        // Something else now answers on the dedicated port with another certificate.
        FakeLocalSendServer impostor = await StartFakeAsync();
        FakeLocalSendServer fake = await StartFakeAsync();
        string file = WriteSource("moved.txt", "moved");
        var client = new TransferClient(() => Identity, () => Password, impostor.Port);

        SendResult result = await client.SendAsync(
            Target(fake, new string('c', 64)), [file], NoPin, CancellationToken.None);

        Assert.Equal(SendOutcome.Sent, result.Outcome);
        Assert.Empty(impostor.Urls);
        Assert.True(File.Exists(Path.Combine(fake.Directory, "moved.txt")));
        Assert.DoesNotContain(fake.Urls, url => url.Contains("pin=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Send_Folder_SendsRelativeNames()
    {
        FakeLocalSendServer fake = await StartFakeAsync();
        string folder = Path.Combine(_source, "Album");
        Directory.CreateDirectory(Path.Combine(folder, "day1"));
        File.WriteAllText(Path.Combine(folder, "cover.jpg"), "cover");
        File.WriteAllText(Path.Combine(folder, "day1", "a.jpg"), "a");

        SendResult result = await NormalOnlyClient().SendAsync(Target(fake), [folder], NoPin, CancellationToken.None);

        Assert.Equal(SendOutcome.Sent, result.Outcome);
        Assert.Equal("cover", File.ReadAllText(Path.Combine(fake.Directory, "Album", "cover.jpg")));
        Assert.Equal("a", File.ReadAllText(Path.Combine(fake.Directory, "Album", "day1", "a.jpg")));
    }

    [Fact]
    public void Expand_Folder_RelativeNamesIncludeFolder()
    {
        string folder = Path.Combine(_source, "Trip");
        Directory.CreateDirectory(Path.Combine(folder, "b", "deep"));
        Directory.CreateDirectory(Path.Combine(folder, "a"));
        File.WriteAllText(Path.Combine(folder, "z.txt"), "z");
        File.WriteAllText(Path.Combine(folder, "a", "1.txt"), "1");
        File.WriteAllText(Path.Combine(folder, "b", "deep", "2.txt"), "2");
        string single = WriteSource("single.txt", "s");

        IReadOnlyList<(string FullPath, string RelativeName)> expanded = FileEnumerator.Expand([single, folder + "\\"]);

        Assert.Equal(
            ["single.txt", "Trip/a/1.txt", "Trip/b/deep/2.txt", "Trip/z.txt"],
            expanded.Select(e => e.RelativeName));
        Assert.Equal(Path.Combine(folder, "b", "deep", "2.txt"), expanded[2].FullPath);
    }

    [Fact]
    public void Expand_MissingPath_Skipped()
    {
        IReadOnlyList<(string FullPath, string RelativeName)> expanded =
            FileEnumerator.Expand([Path.Combine(_source, "does-not-exist")]);

        Assert.Empty(expanded);
    }

    [Fact]
    public async Task Send_PrivateBusy409_ReturnsBusyWithoutFallback()
    {
        FakeLocalSendServer privatePeer = await StartFakeAsync();
        privatePeer.Mode = FakeMode.Busy;
        FakeLocalSendServer fake = await StartFakeAsync();
        var client = new TransferClient(() => Identity, () => Password, privatePeer.Port);

        SendResult result = await client.SendAsync(
            Target(fake, privatePeer.LowercaseFingerprint), [WriteSource("a.txt", "a")], NoPin, CancellationToken.None);

        Assert.Equal(SendOutcome.Busy, result.Outcome);
        Assert.Contains(privatePeer.Urls, url => url.Contains("pin=", StringComparison.Ordinal));
        Assert.Empty(fake.Urls);
    }

    [Theory]
    [InlineData(FakeMode.TooManyRequests)]
    [InlineData(FakeMode.ServerError)]
    public async Task Send_PrivateRefused_FallsBackToOriginal(FakeMode mode)
    {
        FakeLocalSendServer privatePeer = await StartFakeAsync();
        privatePeer.Mode = mode;
        FakeLocalSendServer fake = await StartFakeAsync();
        var client = new TransferClient(() => Identity, () => Password, privatePeer.Port);

        SendResult result = await client.SendAsync(
            Target(fake, privatePeer.LowercaseFingerprint), [WriteSource("r.txt", "refused")], NoPin,
            CancellationToken.None);

        Assert.Equal(SendOutcome.Sent, result.Outcome);
        Assert.NotEmpty(privatePeer.Urls);
        Assert.Equal("refused", File.ReadAllText(Path.Combine(fake.Directory, "r.txt")));
    }

    [Fact]
    public async Task Send_NoOriginalIdentity_PrivateRouteStillSends()
    {
        PrivateReceiveServer server = await StartPrivateAsync(Password);
        var received = new List<ReceivedFile>();
        server.FileReceived += file =>
        {
            lock (received)
            {
                received.Add(file);
            }
        };
        FakeLocalSendServer fake = await StartFakeAsync();
        var client = new TransferClient(() => null, () => Password, PrivatePortOf(server));

        SendResult result = await client.SendAsync(
            Target(fake, server.Fingerprint), [WriteSource("solo.txt", "solo")], NoPin, CancellationToken.None);

        Assert.Equal(SendOutcome.Sent, result.Outcome);
        Assert.Equal("solo", File.ReadAllText(Path.Combine(_privateDownloads, "solo.txt")));
        Assert.Equal(Environment.MachineName, Assert.Single(received).SenderAlias);
        Assert.Empty(fake.Urls);
    }

    [Fact]
    public void Expand_SameFileTwice_ListedOnce()
    {
        string folder = Path.Combine(_source, "Box");
        Directory.CreateDirectory(folder);
        string inside = Path.Combine(folder, "in.txt");
        File.WriteAllText(inside, "i");
        File.WriteAllText(Path.Combine(folder, "other.txt"), "o");

        IReadOnlyList<(string FullPath, string RelativeName)> expanded =
            FileEnumerator.Expand([inside, inside.ToUpperInvariant(), folder, folder]);

        Assert.Equal(["in.txt", "Box/other.txt"], expanded.Select(e => e.RelativeName));
    }

    [Fact]
    public async Task Token_NeverAppearsWithPassword()
    {
        PrivateReceiveServer server = await StartPrivateAsync("receiver password");
        FakeLocalSendServer fake = await StartFakeAsync();
        fake.Mode = FakeMode.RequirePin;
        string file = WriteSource("secret.txt", "contents");
        var client = new TransferClient(() => Identity, () => Password, PrivatePortOf(server));

        SendResult result = await client.SendAsync(
            Target(fake, server.Fingerprint), [file], () => Task.FromResult<string?>(FakeLocalSendServer.RequiredPin),
            CancellationToken.None);

        Assert.Equal(SendOutcome.SentAfterPasswordMismatch, result.Outcome);
        Assert.NotEmpty(fake.Urls);
        Assert.All(fake.Urls.Concat(fake.Bodies), text => Assert.DoesNotContain(Password, text, StringComparison.Ordinal));
        Assert.All(fake.Urls, url => Assert.True(
            !url.Contains("pin=", StringComparison.Ordinal) || url.Contains("pin=1234", StringComparison.Ordinal), url));
    }

    private static Task<string?> NoPin() => Task.FromResult<string?>(null);

    private TransferClient NormalOnlyClient() => new(() => Identity, () => null, FreePort.Get());

    private static DiscoveredDevice Target(FakeLocalSendServer fake, string? privateFingerprint = null) =>
        new("Phone", "mobile", fake.Fingerprint, IPAddress.Loopback, fake.Port, fake.Https ? "https" : "http",
            privateFingerprint, DateTimeOffset.UtcNow);

    private string WriteSource(string name, string contents)
    {
        string path = Path.Combine(_source, name);
        File.WriteAllText(path, contents);
        return path;
    }

    private async Task<FakeLocalSendServer> StartFakeAsync(bool https = true)
    {
        FakeLocalSendServer fake = await FakeLocalSendServer.StartAsync(
            Path.Combine(_root, "fake-" + Guid.NewGuid().ToString("N")), https);
        _servers.Add(fake);
        return fake;
    }

    private readonly Dictionary<PrivateReceiveServer, int> _privatePorts = [];

    private int PrivatePortOf(PrivateReceiveServer server) => _privatePorts[server];

    private async Task<PrivateReceiveServer> StartPrivateAsync(string password)
    {
        X509Certificate2 certificate = CertificateStore.LoadOrCreate(
            Path.Combine(_root, "private-cert-" + Guid.NewGuid().ToString("N"), "cert.bin"));
        _certificates.Add(certificate);
        int port = FreePort.Get();
        var server = new PrivateReceiveServer(new PrivateServerOptions(
            port, certificate, () => password, _privateDownloads, () => DateTimeOffset.UtcNow, IPAddress.Loopback));
        await server.StartAsync(CancellationToken.None);
        _servers.Add(server);
        _privatePorts[server] = port;
        return server;
    }
}
