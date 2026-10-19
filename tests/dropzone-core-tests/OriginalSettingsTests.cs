using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Dropzone.Core.Original;
using Dropzone.Core.Protocol;
using Dropzone.Core.Tests.TestSupport;

namespace Dropzone.Core.Tests;

public class OriginalSettingsTests : IDisposable
{
    private static readonly HistoryEntry[] ExpectedFixtureHistory =
    [
        new HistoryEntry("fixture-0001", "fixture-photo.jpg", @"C:\Fixture\Downloads\fixture-photo.jpg", false, 2048, "Fixture Phone", new DateTimeOffset(2026, 10, 18, 9, 15, 30, 250, TimeSpan.Zero)),
        new HistoryEntry("fixture-0003", "Hello from the fixture", string.Empty, true, 23, "Fixture Phone", new DateTimeOffset(2026, 10, 17, 20, 1, 2, TimeSpan.Zero)),
        new HistoryEntry("fixture-0004", "fixture-notes.txt", @"C:\Fixture\Downloads\fixture-notes.txt", false, 512, "Fixture Laptop", new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero)),
    ];

    private readonly string _workDir = Path.Combine(Path.GetTempPath(), "dropzone-core-tests", Guid.NewGuid().ToString("N"));

    public OriginalSettingsTests()
    {
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        Directory.Delete(_workDir, recursive: true);
    }

    [Fact]
    public void TryRead_Fixture_ParsesAliasPortAndHistory()
    {
        OriginalSettings? settings = OriginalSettingsReader.TryRead(FixturePath);

        Assert.NotNull(settings);
        Assert.Equal("Fixture Laptop", settings.Alias);
        Assert.Equal(53399, settings.Port);
        Assert.Equal(ExpectedFixtureHistory, settings.ReceiveHistory);
        Assert.Equal(File.GetLastWriteTimeUtc(FixturePath), settings.FileLastWriteUtc);
    }

    [Fact]
    public void TryRead_MalformedHistoryEntry_IsSkipped()
    {
        OriginalSettings? settings = OriginalSettingsReader.TryRead(FixturePath);

        Assert.NotNull(settings);
        Assert.Equal(
            new[] { "fixture-0001", "fixture-0003", "fixture-0004" },
            settings.ReceiveHistory.Select(entry => entry.Id));
    }

    [Fact]
    public void TryRead_MissingFile_ReturnsNull()
    {
        string path = Path.Combine(_workDir, "does-not-exist.json");

        Assert.Null(OriginalSettingsReader.TryRead(path));
    }

    [Fact]
    public void TryRead_LockedFile_ReturnsNull()
    {
        string path = CopyFixture();
        using FileStream exclusive = new(path, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.Null(OriginalSettingsReader.TryRead(path));
    }

    [Fact]
    public void TryRead_TruncatedJson_ReturnsNull()
    {
        byte[] whole = File.ReadAllBytes(FixturePath);
        string path = Path.Combine(_workDir, "half-written.json");
        File.WriteAllBytes(path, whole[..(whole.Length / 2)]);

        Assert.Null(OriginalSettingsReader.TryRead(path));
    }

    [Fact]
    public void TryRead_MissingPort_DefaultsTo53317()
    {
        string path = WriteSettings(new Dictionary<string, object?>
        {
            ["flutter.ls_alias"] = "No Port",
        });

        OriginalSettings? settings = OriginalSettingsReader.TryRead(path);

        Assert.NotNull(settings);
        Assert.Equal(53317, settings.Port);
    }

    [Fact]
    public void TryRead_MissingHistory_ReturnsEmptyHistory()
    {
        string path = WriteSettings(new Dictionary<string, object?>
        {
            ["flutter.ls_alias"] = "No History",
            ["flutter.ls_port"] = 53317,
        });

        OriginalSettings? settings = OriginalSettingsReader.TryRead(path);

        Assert.NotNull(settings);
        Assert.Empty(settings.ReceiveHistory);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryRead_MissingOrBlankAlias_ReturnsNull(string? alias)
    {
        Dictionary<string, object?> values = new() { ["flutter.ls_port"] = 53317 };
        if (alias is not null)
        {
            values["flutter.ls_alias"] = alias;
        }

        Assert.Null(OriginalSettingsReader.TryRead(WriteSettings(values)));
    }

    [Theory]
    [InlineData("53317")]
    [InlineData(0)]
    [InlineData(65536)]
    public void TryRead_PortThatIsNotAPortNumber_ReturnsNull(object port)
    {
        string path = WriteSettings(new Dictionary<string, object?>
        {
            ["flutter.ls_alias"] = "Bad Port",
            ["flutter.ls_port"] = port,
        });

        Assert.Null(OriginalSettingsReader.TryRead(path));
    }

    [Fact]
    public void TryRead_HistoryItemsOfWrongShape_AreSkipped()
    {
        string valid = JsonSerializer.Serialize(new
        {
            id = "valid-1",
            fileName = "ok.txt",
            fileType = "text",
            path = @"C:\Fixture\ok.txt",
            savedToGallery = false,
            isMessage = false,
            fileSize = 3,
            senderAlias = "Fixture Phone",
            timestamp = "2026-10-18T00:00:00.000Z",
        });
        object[] history =
        [
            valid,
            "{\"id\":\"truncated\"",
            42,
            new { id = "object-not-string", fileName = "a", path = "", isMessage = false, fileSize = 1, senderAlias = "x", timestamp = "2026-10-18T00:00:00.000Z" },
            JsonSerializer.Serialize(new { id = "no-timestamp", fileName = "a", path = "", isMessage = false, fileSize = 1, senderAlias = "x" }),
            JsonSerializer.Serialize(new { id = "bad-timestamp", fileName = "a", path = "", isMessage = false, fileSize = 1, senderAlias = "x", timestamp = "yesterday" }),
            JsonSerializer.Serialize(new { id = "size-as-text", fileName = "a", path = "", isMessage = false, fileSize = "1", senderAlias = "x", timestamp = "2026-10-18T00:00:00.000Z" }),
        ];
        string path = WriteSettings(new Dictionary<string, object?>
        {
            ["flutter.ls_alias"] = "Fixture Laptop",
            ["flutter.ls_port"] = 53317,
            ["flutter.ls_receive_history"] = history,
        });

        OriginalSettings? settings = OriginalSettingsReader.TryRead(path);

        Assert.NotNull(settings);
        Assert.Equal("valid-1", Assert.Single(settings.ReceiveHistory).Id);
    }

    [Fact]
    public void TryRead_SaveToHistoryMissing_IsTrue()
    {
        OriginalSettings? settings = OriginalSettingsReader.TryRead(FixturePath);

        Assert.NotNull(settings);
        Assert.True(settings.SaveToHistory);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData("false", true)] // not a boolean: the official app's own default (true) applies
    public void TryRead_SaveToHistoryFlag(object flag, bool expected)
    {
        string path = WriteSettings(new Dictionary<string, object?>
        {
            ["flutter.ls_alias"] = "Fixture Laptop",
            ["flutter.ls_save_to_history"] = flag,
        });

        OriginalSettings? settings = OriginalSettingsReader.TryRead(path);

        Assert.NotNull(settings);
        Assert.Equal(expected, settings.SaveToHistory);
    }

    [Fact]
    public void ParseAndClear_ZeroesTheBufferAfterParsing()
    {
        byte[] content = File.ReadAllBytes(FixturePath);

        OriginalSettings? settings = OriginalSettingsReader.ParseAndClear(content, DateTime.UtcNow);

        Assert.NotNull(settings);
        Assert.Equal("Fixture Laptop", settings.Alias);
        Assert.All(content, b => Assert.Equal(0, b));
    }

    [Fact]
    public void ParseAndClear_InvalidJson_StillZeroesTheBuffer()
    {
        byte[] content = "{\"flutter.ls_alias\": \"x\", \"flutter.ls_security_context\": \"FAKE"u8.ToArray();

        Assert.Null(OriginalSettingsReader.ParseAndClear(content, DateTime.UtcNow));
        Assert.All(content, b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task ResolveAsync_AgainstTestTlsServer_ReturnsFingerprintOfCert()
    {
        using X509Certificate2 serverCertificate = CreateServerCertificate();
        string expectedFingerprint = Convert.ToHexString(SHA256.HashData(serverCertificate.RawData));
        int port = FreePort.Get();
        TcpListener listener = new(IPAddress.Loopback, port);
        listener.Start();
        try
        {
            Task serverHandshake = ServeOneTlsHandshakeAsync(listener, serverCertificate);
            OriginalSettings settings = new("Fixture Laptop", port, Array.Empty<HistoryEntry>(), DateTime.UtcNow);

            DeviceInfo? identity = await OriginalIdentity.ResolveAsync(settings, CancellationToken.None);

            await serverHandshake.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(
                new DeviceInfo("Fixture Laptop", "2.2", "Windows", "desktop", expectedFingerprint, port, "https", false),
                identity);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task ResolveAsync_NothingListening_ReturnsNull()
    {
        OriginalSettings settings = new("Fixture Laptop", FreePort.Get(), Array.Empty<HistoryEntry>(), DateTime.UtcNow);

        DeviceInfo? identity = await OriginalIdentity.ResolveAsync(settings, CancellationToken.None);

        Assert.Null(identity);
    }

    [Fact]
    public async Task ResolveAsync_ServerNeverAnswersTls_ReturnsNullWithinTimeout()
    {
        int port = FreePort.Get();
        TcpListener listener = new(IPAddress.Loopback, port);
        listener.Start();
        try
        {
            // The TCP connection completes into the listen backlog, but nothing ever answers the TLS ClientHello.
            OriginalSettings settings = new("Fixture Laptop", port, Array.Empty<HistoryEntry>(), DateTime.UtcNow);
            Stopwatch clock = Stopwatch.StartNew();

            DeviceInfo? identity = await OriginalIdentity.ResolveAsync(settings, CancellationToken.None);

            clock.Stop();
            Assert.Null(identity);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"Gave up after {clock.Elapsed}; expected about 2 s.");
        }
        finally
        {
            listener.Stop();
        }
    }

    private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "localsend-settings.json");

    private string CopyFixture()
    {
        string path = Path.Combine(_workDir, $"{Guid.NewGuid():N}.json");
        File.Copy(FixturePath, path);
        return path;
    }

    private string WriteSettings(Dictionary<string, object?> values)
    {
        string path = Path.Combine(_workDir, $"{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(values));
        return path;
    }

    private static async Task ServeOneTlsHandshakeAsync(TcpListener listener, X509Certificate2 certificate)
    {
        using TcpClient client = await listener.AcceptTcpClientAsync();
        using SslStream tls = new(client.GetStream(), leaveInnerStreamOpen: false);
        await tls.AuthenticateAsServerAsync(certificate, clientCertificateRequired: false, checkCertificateRevocation: false);
    }

    private static X509Certificate2 CreateServerCertificate()
    {
        using RSA key = RSA.Create(2048);
        CertificateRequest request = new("CN=dropzone-test-server", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 selfSigned = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        // Re-import so that SslStream on Windows can reach the private key.
        return X509CertificateLoader.LoadPkcs12(selfSigned.Export(X509ContentType.Pfx), null);
    }
}
