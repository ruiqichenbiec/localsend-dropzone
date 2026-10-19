using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Dropzone.Core.Protocol;
using Dropzone.Core.Receive;
using Dropzone.Core.Security;
using Dropzone.Core.Tests.TestSupport;

namespace Dropzone.Core.Tests;

public sealed class PrivateReceiveServerTests : IClassFixture<PrivateReceiveServerTests.CertificateFixture>, IAsyncLifetime
{
    private const string Password = "correct horse";
    private const string SenderAlias = "Sender PC";
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private readonly X509Certificate2 _certificate;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dropzone-core-tests", Guid.NewGuid().ToString("N"));
    private readonly string _downloads;
    private readonly int _port = FreePort.Get();
    private readonly List<ReceivedFile> _received = [];
    private readonly List<ReceivedSession> _sessions = [];
    private int _receivedCountAtSessionEnd;
    private DateTimeOffset _now = FixedNow;
    private string? _localPassword = Password;
    private TimeSpan _passwordLookupDelay = TimeSpan.Zero;
    private PrivateReceiveServer _server = null!;
    private HttpClient _client = null!;

    public PrivateReceiveServerTests(CertificateFixture fixture)
    {
        _certificate = fixture.Certificate;
        _downloads = Path.Combine(_root, "Downloads");
        Directory.CreateDirectory(_downloads);
    }

    public async Task InitializeAsync()
    {
        _server = new PrivateReceiveServer(new PrivateServerOptions(
            _port, _certificate, ReadLocalPassword, _downloads, () => _now, IPAddress.Loopback));
        _server.FileReceived += file =>
        {
            lock (_received)
            {
                _received.Add(file);
            }
        };
        _server.SessionCompleted += session =>
        {
            lock (_received)
            {
                _sessions.Add(session);
                _receivedCountAtSessionEnd = _received.Count;
            }
        };
        await _server.StartAsync(CancellationToken.None);
        _client = CreateClient(localAddress: null);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _server.DisposeAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Hello_Returns200WithAppName()
    {
        using HttpResponseMessage response = await _client.GetAsync("/api/lsdz/v1/hello");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        HelloResponse? hello = await response.Content.ReadFromJsonAsync<HelloResponse>(LocalSendJson.Options);
        Assert.NotNull(hello);
        Assert.Equal("localsend-dropzone", hello.App);
        Assert.False(string.IsNullOrWhiteSpace(hello.Version));
    }

    [Fact]
    public async Task Prepare_NoPin_401()
    {
        using HttpResponseMessage response = await PrepareAsync(Request(Entry("f1", "a.txt", 3)), pin: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Prepare_WrongToken_401()
    {
        string wrong = PasswordToken.Create("not the password", _server.Fingerprint, _now);

        using HttpResponseMessage response = await PrepareAsync(Request(Entry("f1", "a.txt", 3)), wrong);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Prepare_NoLocalPassword_401(string? localPassword)
    {
        _localPassword = localPassword;
        string pin = PasswordToken.Create(localPassword ?? string.Empty, _server.Fingerprint, _now);

        using HttpResponseMessage response = await PrepareAsync(Request(Entry("f1", "a.txt", 3)), pin);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Prepare_ValidToken_200WithFileTokens()
    {
        using HttpResponseMessage response = await PrepareAsync(
            Request(Entry("f1", "a.txt", 3), Entry("f2", "b.txt", 4)), ValidPin());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        PrepareUploadResponse? body = await response.Content.ReadFromJsonAsync<PrepareUploadResponse>(LocalSendJson.Options);
        Assert.NotNull(body);
        Assert.False(string.IsNullOrEmpty(body.SessionId));
        Assert.Equal(["f1", "f2"], body.Files.Keys.Order());
        Assert.All(body.Files.Values, token => Assert.False(string.IsNullOrEmpty(token)));
        Assert.NotEqual(body.Files["f1"], body.Files["f2"]);
    }

    [Theory]
    [InlineData(".0123456789abcdef0123456789abcdef.lsdz-part")]  // would be swept by the next StartAsync
    [InlineData("report.LSDZ-PART")]
    [InlineData("report.lsdz-part.")]                            // sanitizing drops the trailing dot
    [InlineData("dir.lsdz-part/a.txt")]
    [InlineData("dir/.0123456789abcdef0123456789abcdef.lsdz-part")]
    public async Task Prepare_TempSuffixInName_400_AndNothingWritten(string fileName)
    {
        using HttpResponseMessage response = await PrepareAsync(
            Request(Entry("f1", "ok.txt", 3), Entry("f2", fileName, 3)), ValidPin());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(AllEntries());
    }

    [Fact]
    public async Task Prepare_UnsafeFileName_400_AndNothingWritten()
    {
        using HttpResponseMessage response = await PrepareAsync(
            Request(Entry("f1", "ok.txt", 3), Entry("f2", "../evil.txt", 3)), ValidPin());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(AllEntries());
        Assert.False(File.Exists(Path.Combine(_root, "evil.txt")));

        // No session was opened: a valid request goes through.
        using HttpResponseMessage next = await PrepareAsync(Request(Entry("f1", "ok.txt", 3)), ValidPin());
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
    }

    public static TheoryData<string> InvalidBodies => new()
    {
        "not json",
        """{"files":{"f1":{"id":"f1","fileName":"a.txt","size":1,"fileType":"text/plain"}}}""",
        """{"info":{"alias":"x","version":"2.1","fingerprint":"f","port":1,"protocol":"https","download":false}}""",
        """{"info":{"alias":"x","version":"2.1","fingerprint":"f","port":1,"protocol":"https","download":false},"files":{}}""",
        """{"info":{"alias":"x","version":"2.1","fingerprint":"f","port":1,"protocol":"https","download":false},"files":{"f1":{"fileName":"a.txt","size":1,"fileType":"text/plain"}}}""",
        """{"info":{"alias":"x","version":"2.1","fingerprint":"f","port":1,"protocol":"https","download":false},"files":{"f1":{"id":"","fileName":"a.txt","size":1,"fileType":"text/plain"}}}""",
        """{"info":{"alias":"x","version":"2.1","fingerprint":"f","port":1,"protocol":"https","download":false},"files":{"f1":{"id":"f1","size":1,"fileType":"text/plain"}}}""",
        """{"info":{"alias":"x","version":"2.1","fingerprint":"f","port":1,"protocol":"https","download":false},"files":{"f1":{"id":"f1","fileName":"a.txt","size":-1,"fileType":"text/plain"}}}""",
        """{"info":{"alias":"x","version":"2.1","fingerprint":"f","port":1,"protocol":"https","download":false},"files":{"f1":null}}""",
    };

    [Theory]
    [MemberData(nameof(InvalidBodies))]
    public async Task Prepare_InvalidBody_400(string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await _client.PostAsync(PreparePath(ValidPin()), content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(AllEntries());
    }

    [Fact]
    public async Task Prepare_SizeAboveInt32_Accepted()
    {
        using HttpResponseMessage response = await PrepareAsync(Request(Entry("big", "big.iso", 5_000_000_000)), ValidPin());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(AllEntries());
    }

    [Fact]
    public async Task Prepare_SixthFailureFromSameIp_429()
    {
        string wrong = PasswordToken.Create("guess", _server.Fingerprint, _now);
        for (int i = 0; i < 5; i++)
        {
            using HttpResponseMessage failed = await PrepareAsync(Request(Entry("f1", "a.txt", 3)), wrong);
            Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        }

        // Blocked even with the right token.
        using HttpResponseMessage sixth = await PrepareAsync(Request(Entry("f1", "a.txt", 3)), ValidPin());
        Assert.Equal((HttpStatusCode)429, sixth.StatusCode);

        _now = FixedNow.AddMinutes(4).AddSeconds(59);
        using HttpResponseMessage stillBlocked = await PrepareAsync(Request(Entry("f1", "a.txt", 3)), ValidPin());
        Assert.Equal((HttpStatusCode)429, stillBlocked.StatusCode);

        _now = FixedNow.AddMinutes(5).AddSeconds(1);
        using HttpResponseMessage afterBlock = await PrepareAsync(Request(Entry("f1", "a.txt", 3)), ValidPin());
        Assert.Equal(HttpStatusCode.OK, afterBlock.StatusCode);
    }

    [Fact]
    public async Task Prepare_FailuresSpreadOverMoreThanAMinute_NotBlocked()
    {
        string wrong = PasswordToken.Create("guess", _server.Fingerprint, _now);
        for (int i = 0; i < 8; i++)
        {
            _now = FixedNow.AddSeconds(20 * i);
            using HttpResponseMessage failed = await PrepareAsync(Request(Entry("f1", "a.txt", 3)), wrong);
            Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        }
    }

    [Fact]
    public async Task Prepare_WhileSessionActive_409()
    {
        await PrepareOkAsync(Entry("f1", "a.txt", 3));

        using HttpResponseMessage second = await PrepareAsync(Request(Entry("f1", "b.txt", 3)), ValidPin());

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Prepare_AfterSessionIdleTwoMinutes_NewSessionAccepted()
    {
        await PrepareOkAsync(Entry("f1", "a.txt", 3));

        _now = FixedNow.AddMinutes(1).AddSeconds(59);
        using HttpResponseMessage busy = await PrepareAsync(Request(Entry("f1", "b.txt", 3)), ValidPin());
        Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);

        _now = FixedNow.AddMinutes(2).AddSeconds(1);
        using HttpResponseMessage fresh = await PrepareAsync(Request(Entry("f1", "b.txt", 3)), ValidPin());
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
    }

    [Fact]
    public async Task Upload_WritesFile_WithClockMtime()
    {
        byte[] content = Encoding.UTF8.GetBytes("hello dropzone");
        PrepareUploadResponse session = await PrepareOkAsync(Entry("f1", "hello.txt", content.Length));

        using HttpResponseMessage response = await UploadAsync(session, "f1", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string path = Path.Combine(_downloads, "hello.txt");
        Assert.Equal(content, await File.ReadAllBytesAsync(path));
        Assert.Equal(FixedNow.UtcDateTime, File.GetLastWriteTimeUtc(path));
        Assert.Equal([path], AllEntries());
    }

    [Fact]
    public async Task Upload_AllFilesReceived_SessionEnds()
    {
        PrepareUploadResponse session = await PrepareOkAsync(Entry("f1", "a.txt", 1), Entry("f2", "b.txt", 1));
        using HttpResponseMessage first = await UploadAsync(session, "f1", "a"u8.ToArray());
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using HttpResponseMessage busy = await PrepareAsync(Request(Entry("f1", "c.txt", 1)), ValidPin());
        Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);

        using HttpResponseMessage second = await UploadAsync(session, "f2", "b"u8.ToArray());
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        using HttpResponseMessage next = await PrepareAsync(Request(Entry("f1", "c.txt", 1)), ValidPin());
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
    }

    [Fact]
    public async Task Upload_NameConflict_SavedAsOne()
    {
        string existing = Path.Combine(_downloads, "报告.pdf");
        await File.WriteAllTextAsync(existing, "old");
        byte[] content = Encoding.UTF8.GetBytes("new report");
        PrepareUploadResponse session = await PrepareOkAsync(Entry("f1", "报告.pdf", content.Length));

        using HttpResponseMessage response = await UploadAsync(session, "f1", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("old", await File.ReadAllTextAsync(existing));
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(_downloads, "报告 (1).pdf")));
    }

    [Fact]
    public async Task Upload_NestedName_CreatesSubdirectory()
    {
        byte[] content = "nested"u8.ToArray();
        PrepareUploadResponse session = await PrepareOkAsync(Entry("f1", "dir/a.txt", content.Length));

        using HttpResponseMessage response = await UploadAsync(session, "f1", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(_downloads, "dir", "a.txt")));
    }

    [Fact]
    public async Task Upload_NestedNameWhoseFolderIsAFile_500_ServerKeepsRunning()
    {
        string blocker = Path.Combine(_downloads, "dir");
        await File.WriteAllTextAsync(blocker, "i am a file");
        PrepareUploadResponse session = await PrepareOkAsync(Entry("f1", "dir/a.txt", 3));

        using HttpResponseMessage response = await UploadAsync(session, "f1", "abc"u8.ToArray());

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal([blocker], AllEntries());
        Assert.Equal("i am a file", await File.ReadAllTextAsync(blocker));
        using HttpResponseMessage hello = await _client.GetAsync("/api/lsdz/v1/hello");
        Assert.Equal(HttpStatusCode.OK, hello.StatusCode);
    }

    [Fact]
    public async Task Upload_WrongToken_403()
    {
        PrepareUploadResponse session = await PrepareOkAsync(Entry("f1", "a.txt", 3));
        string query = $"sessionId={session.SessionId}&fileId=f1&token=not-the-token";

        using HttpResponseMessage response = await _client.PostAsync(UploadPath(query), new ByteArrayContent("abc"u8.ToArray()));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(AllEntries());
    }

    [Fact]
    public async Task Upload_WrongSessionOrFile_403()
    {
        PrepareUploadResponse session = await PrepareOkAsync(Entry("f1", "a.txt", 3));
        string token = session.Files["f1"];

        using HttpResponseMessage wrongSession = await _client.PostAsync(
            UploadPath($"sessionId=other&fileId=f1&token={token}"), new ByteArrayContent("abc"u8.ToArray()));
        using HttpResponseMessage wrongFile = await _client.PostAsync(
            UploadPath($"sessionId={session.SessionId}&fileId=f2&token={token}"), new ByteArrayContent("abc"u8.ToArray()));

        Assert.Equal(HttpStatusCode.Forbidden, wrongSession.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, wrongFile.StatusCode);
        Assert.Empty(AllEntries());
    }

    [Fact]
    public async Task Upload_FromOtherIp_403()
    {
        PrepareUploadResponse session = await PrepareOkAsync(Entry("f1", "a.txt", 3));
        using HttpClient other = CreateClient(localAddress: IPAddress.Parse("127.0.0.2"));

        using HttpResponseMessage response = await other.PostAsync(UploadUri(session, "f1"), new ByteArrayContent("abc"u8.ToArray()));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(AllEntries());
    }

    [Theory]
    [InlineData("fileId=f1&token=t")]
    [InlineData("sessionId=s&token=t")]
    [InlineData("sessionId=s&fileId=f1")]
    [InlineData("")]
    public async Task Upload_MissingParameter_400(string query)
    {
        using HttpResponseMessage response = await _client.PostAsync(UploadPath(query), new ByteArrayContent("abc"u8.ToArray()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(15)]
    public async Task Upload_LengthMismatch_400_NoFile(int sentLength)
    {
        PrepareUploadResponse session = await PrepareOkAsync(Entry("f1", "a.txt", 10));

        using HttpResponseMessage response = await UploadAsync(session, "f1", new byte[sentLength]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(AllEntries());
    }

    [Fact]
    public async Task Upload_AbortedMidway_LeavesNoFile()
    {
        const int size = 2 * 1024 * 1024;
        PrepareUploadResponse session = await PrepareOkAsync(Entry("f1", "big.bin", size));
        (TcpClient tcp, SslStream ssl) = await StartRawUploadAsync(session, "f1", size);

        await ssl.WriteAsync(new byte[size / 2]);
        await ssl.FlushAsync();
        await WaitUntilAsync(() => PartFiles().Length == 1);

        tcp.Client.LingerState = new LingerOption(enable: true, seconds: 0);
        ssl.Dispose();
        tcp.Dispose();

        await WaitUntilAsync(() => AllEntries().Length == 0);
        Assert.False(File.Exists(Path.Combine(_downloads, "big.bin")));
    }

    [Fact]
    public async Task Cancel_RemovesPartFiles()
    {
        const int size = 2 * 1024 * 1024;
        PrepareUploadResponse session = await PrepareOkAsync(Entry("f1", "big.bin", size));
        (TcpClient tcp, SslStream ssl) = await StartRawUploadAsync(session, "f1", size);
        using (tcp)
        using (ssl)
        {
            await ssl.WriteAsync(new byte[size / 2]);
            await ssl.FlushAsync();
            await WaitUntilAsync(() => PartFiles().Length == 1);

            using HttpResponseMessage cancel = await _client.PostAsync(
                $"/api/localsend/v2/cancel?sessionId={session.SessionId}", content: null);

            Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
            await WaitUntilAsync(() => AllEntries().Length == 0);
        }

        using HttpResponseMessage next = await PrepareAsync(Request(Entry("f1", "a.txt", 3)), ValidPin());
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
    }

    [Fact]
    public async Task Cancel_FromOtherIp_403_SessionKept()
    {
        PrepareUploadResponse session = await PrepareOkAsync(Entry("f1", "a.txt", 3));
        using HttpClient other = CreateClient(localAddress: IPAddress.Parse("127.0.0.2"));

        using HttpResponseMessage cancel = await other.PostAsync(
            $"/api/localsend/v2/cancel?sessionId={session.SessionId}", content: null);

        Assert.Equal(HttpStatusCode.Forbidden, cancel.StatusCode);
        using HttpResponseMessage upload = await UploadAsync(session, "f1", "abc"u8.ToArray());
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
    }

    [Fact]
    public async Task FileReceived_RaisedWithFinalPath()
    {
        await File.WriteAllTextAsync(Path.Combine(_downloads, "a.txt"), "old");
        PrepareUploadResponse session = await PrepareOkAsync(Entry("f1", "a.txt", 3));

        using HttpResponseMessage response = await UploadAsync(session, "f1", "abc"u8.ToArray());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ReceivedFile received = Assert.Single(_received);
        Assert.Equal(Path.Combine(_downloads, "a (1).txt"), received.FullPath);
        Assert.Equal(SenderAlias, received.SenderAlias);
    }

    // ---------------------------------------------------------------- one signal per session

    [Fact]
    public async Task SessionCompleted_AllFilesReceived_RaisedOnceWithEveryFileAfterTheirFileReceived()
    {
        PrepareUploadResponse session = await PrepareOkAsync(Entry("f1", "a.txt", 1), Entry("f2", "dir/b.txt", 1));

        using HttpResponseMessage first = await UploadAsync(session, "f1", "a"u8.ToArray());
        Assert.Empty(Sessions());
        using HttpResponseMessage second = await UploadAsync(session, "f2", "b"u8.ToArray());

        ReceivedSession done = Assert.Single(Sessions());
        Assert.Equal(SenderAlias, done.SenderAlias);
        Assert.Equal([Path.Combine(_downloads, "a.txt"), Path.Combine(_downloads, "dir", "b.txt")], done.Files);
        Assert.Equal(0, done.FailedCount);
        Assert.Equal(2, _receivedCountAtSessionEnd);
    }

    [Fact]
    public async Task SessionCompleted_LastFileFails_RaisedAtOnceWithTheFailedCount()
    {
        PrepareUploadResponse session = await PrepareOkAsync(Entry("f1", "a.txt", 1), Entry("f2", "b.txt", 3));
        using HttpResponseMessage first = await UploadAsync(session, "f1", "a"u8.ToArray());

        using HttpResponseMessage second = await UploadAsync(session, "f2", "b"u8.ToArray()); // 1 byte of 3

        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        ReceivedSession done = Assert.Single(Sessions());
        Assert.Equal([Path.Combine(_downloads, "a.txt")], done.Files);
        Assert.Equal(1, done.FailedCount);

        // The session is over: no 2-minute wait before the next sender gets in.
        using HttpResponseMessage next = await PrepareAsync(Request(Entry("f1", "c.txt", 1)), ValidPin());
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
    }

    [Fact]
    public async Task SessionCompleted_FirstFileFailsThenLastArrives_RaisedWithTheFailedCount()
    {
        PrepareUploadResponse session = await PrepareOkAsync(Entry("f1", "a.txt", 3), Entry("f2", "b.txt", 1));
        using HttpResponseMessage first = await UploadAsync(session, "f1", "a"u8.ToArray()); // 1 byte of 3
        Assert.Equal(HttpStatusCode.BadRequest, first.StatusCode);
        Assert.Empty(Sessions());

        using HttpResponseMessage second = await UploadAsync(session, "f2", "b"u8.ToArray());

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        ReceivedSession done = Assert.Single(Sessions());
        Assert.Equal([Path.Combine(_downloads, "b.txt")], done.Files);
        Assert.Equal(1, done.FailedCount);
    }

    [Fact]
    public async Task Upload_OnlyFileFails_SessionEndsAtOnceWithoutSignal()
    {
        PrepareUploadResponse session = await PrepareOkAsync(Entry("f1", "a.txt", 3));

        using HttpResponseMessage failed = await UploadAsync(session, "f1", "a"u8.ToArray()); // 1 byte of 3

        Assert.Equal(HttpStatusCode.BadRequest, failed.StatusCode);
        using HttpResponseMessage next = await PrepareAsync(Request(Entry("f1", "c.txt", 1)), ValidPin());
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        Assert.Empty(Sessions());
    }

    [Fact]
    public async Task Upload_FailedFileRetriedWhileAnotherIsPending_Accepted()
    {
        PrepareUploadResponse session = await PrepareOkAsync(Entry("f1", "a.txt", 1), Entry("f2", "b.txt", 1));
        using HttpResponseMessage failed = await UploadAsync(session, "f1", "aa"u8.ToArray()); // 2 bytes of 1
        Assert.Equal(HttpStatusCode.BadRequest, failed.StatusCode);

        using HttpResponseMessage retry = await UploadAsync(session, "f1", "a"u8.ToArray());
        using HttpResponseMessage second = await UploadAsync(session, "f2", "b"u8.ToArray());

        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        ReceivedSession done = Assert.Single(Sessions());
        Assert.Equal([Path.Combine(_downloads, "a.txt"), Path.Combine(_downloads, "b.txt")], done.Files);
        Assert.Equal(0, done.FailedCount);
    }

    [Fact]
    public async Task SessionCompleted_CancelledAfterOneFile_RaisedWithTheFileThatArrived()
    {
        PrepareUploadResponse session = await PrepareOkAsync(Entry("f1", "a.txt", 1), Entry("f2", "b.txt", 1));
        using HttpResponseMessage first = await UploadAsync(session, "f1", "a"u8.ToArray());

        using HttpResponseMessage cancel = await _client.PostAsync(
            $"/api/localsend/v2/cancel?sessionId={session.SessionId}", content: null);

        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        ReceivedSession done = Assert.Single(Sessions());
        Assert.Equal([Path.Combine(_downloads, "a.txt")], done.Files);
        Assert.Equal(1, done.FailedCount);
    }

    [Fact]
    public async Task SessionCompleted_IdleSessionExpires_RaisedWithTheFileThatArrived()
    {
        PrepareUploadResponse session = await PrepareOkAsync(Entry("f1", "a.txt", 1), Entry("f2", "b.txt", 1));
        using HttpResponseMessage first = await UploadAsync(session, "f1", "a"u8.ToArray());

        _now = FixedNow.AddMinutes(2).AddSeconds(1);
        using HttpResponseMessage fresh = await PrepareAsync(Request(Entry("f1", "c.txt", 1)), ValidPin());

        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        ReceivedSession done = Assert.Single(Sessions());
        Assert.Equal([Path.Combine(_downloads, "a.txt")], done.Files);
        Assert.Equal(1, done.FailedCount);
    }

    [Fact]
    public async Task SessionCompleted_ServerStoppedAfterOneFile_RaisedWithTheFileThatArrived()
    {
        PrepareUploadResponse session = await PrepareOkAsync(Entry("f1", "a.txt", 1), Entry("f2", "b.txt", 1));
        using HttpResponseMessage first = await UploadAsync(session, "f1", "a"u8.ToArray());

        await _server.StopAsync();

        ReceivedSession done = Assert.Single(Sessions());
        Assert.Equal([Path.Combine(_downloads, "a.txt")], done.Files);
        Assert.Equal(1, done.FailedCount);
    }

    [Fact]
    public async Task SessionCompleted_NothingReceived_NotRaised()
    {
        PrepareUploadResponse session = await PrepareOkAsync(Entry("f1", "a.txt", 1));

        using HttpResponseMessage cancel = await _client.PostAsync(
            $"/api/localsend/v2/cancel?sessionId={session.SessionId}", content: null);
        await _server.StopAsync();

        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        Assert.Empty(Sessions());
    }

    [Theory]
    [InlineData(@"C:\D", @"C:\D\a.txt")]
    [InlineData(@"C:\D\x", @"C:\D\x\a.txt", @"C:\D\x\y\b.txt")]
    [InlineData(@"C:\D", @"C:\D\x\a.txt", @"C:\D\y\b.txt")]
    [InlineData(@"C:\D", @"C:\D\ab\1.txt", @"C:\D\abc\2.txt")]
    [InlineData(@"C:\D\X", @"C:\D\X\1.txt", @"c:\d\x\2.txt")]
    public void ReceivedSession_CommonDirectory(string expected, params string[] files)
    {
        Assert.Equal(expected, new ReceivedSession("Sender", files).CommonDirectory);
    }

    [Fact]
    public async Task StopAsync_ThenStartAsync_ServesAgain()
    {
        await _server.StopAsync();
        await _server.StartAsync(CancellationToken.None);

        using HttpResponseMessage hello = await _client.GetAsync("/api/lsdz/v1/hello");
        Assert.Equal(HttpStatusCode.OK, hello.StatusCode);
    }

    [Fact]
    public async Task Prepare_ConcurrentWrongTokens_ExactlyFiveUnauthorized()
    {
        // HTTP/2: all guesses arrive as parallel streams on one connection. A slow password lookup widens any gap
        // between the block check and the failure count.
        _passwordLookupDelay = TimeSpan.FromMilliseconds(30);
        using HttpClient h2 = CreateClient(localAddress: null, http2: true);
        string wrong = PasswordToken.Create("guess", _server.Fingerprint, _now);

        HttpResponseMessage[] responses = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => PrepareAsync(h2, Request(Entry("f1", "a.txt", 3)), wrong)));
        try
        {
            Assert.All(responses, r => Assert.Equal(HttpVersion.Version20, r.Version));
            Assert.Equal(5, responses.Count(r => r.StatusCode == HttpStatusCode.Unauthorized));
            Assert.Equal(15, responses.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests));
        }
        finally
        {
            foreach (HttpResponseMessage response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task Prepare_TwentyFailuresAcrossIps_BlocksEveryoneForFiveMinutes()
    {
        string wrong = PasswordToken.Create("guess", _server.Fingerprint, _now);
        HttpClient[] clients = Enumerable.Range(1, 5)
            .Select(i => CreateClient(IPAddress.Parse($"127.0.0.{i}")))
            .ToArray();
        try
        {
            for (int c = 0; c < 4; c++)
            {
                for (int i = 0; i < 5; i++)
                {
                    if (c == 3 && i == 4)
                    {
                        // 19 failures so far: below the global ceiling, a fifth IP still gets in.
                        using HttpResponseMessage allowed = await PrepareAsync(clients[4], Request(Entry("f1", "a.txt", 3)), ValidPin());
                        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
                    }

                    using HttpResponseMessage failed = await PrepareAsync(clients[c], Request(Entry("f1", "a.txt", 3)), wrong);
                    Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
                }
            }

            using HttpResponseMessage blocked = await PrepareAsync(clients[4], Request(Entry("f1", "b.txt", 3)), ValidPin());
            Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);

            _now = FixedNow.AddMinutes(4).AddSeconds(59);
            using HttpResponseMessage stillBlocked = await PrepareAsync(clients[4], Request(Entry("f1", "b.txt", 3)), ValidPin());
            Assert.Equal(HttpStatusCode.TooManyRequests, stillBlocked.StatusCode);

            // The block is over, and the first session has been idle for more than 2 minutes.
            _now = FixedNow.AddMinutes(5).AddSeconds(1);
            using HttpResponseMessage after = await PrepareAsync(clients[4], Request(Entry("f1", "b.txt", 3)), ValidPin());
            Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        }
        finally
        {
            foreach (HttpClient client in clients)
            {
                client.Dispose();
            }
        }
    }

    [Theory]
    [InlineData(255, 1, HttpStatusCode.OK)]
    [InlineData(256, 1, HttpStatusCode.BadRequest)]
    [InlineData(255, 4, HttpStatusCode.OK)]          // 4 * 255 + 3 separators = 1023
    [InlineData(250, 5, HttpStatusCode.BadRequest)]  // 5 * 250 + 4 separators = 1254
    public async Task Prepare_PathLengthLimits(int segmentLength, int segments, HttpStatusCode expected)
    {
        string name = string.Join('/', Enumerable.Range(0, segments).Select(i => new string((char)('a' + i), segmentLength)));

        using HttpResponseMessage response = await PrepareAsync(Request(Entry("f1", name, 3)), ValidPin());

        Assert.Equal(expected, response.StatusCode);
        Assert.Empty(AllEntries());
    }

    [Fact]
    public async Task Prepare_BodyOver16MB_400()
    {
        const long tooLarge = 16L * 1024 * 1024 + 1;
        string head =
            $"POST {PreparePath(ValidPin())} HTTP/1.1\r\n" +
            $"Host: 127.0.0.1:{_port}\r\n" +
            "Content-Type: application/json\r\n" +
            $"Content-Length: {tooLarge}\r\n\r\n";
        (TcpClient tcp, SslStream ssl) = await OpenRawAsync(head);
        using (tcp)
        using (ssl)
        {
            Assert.Equal(400, await ReadStatusAsync(ssl));
        }

        using HttpResponseMessage next = await PrepareAsync(Request(Entry("f1", "a.txt", 3)), ValidPin());
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
    }

    [Fact]
    public async Task Upload_ZeroByteFile_Written()
    {
        PrepareUploadResponse session = await PrepareOkAsync(Entry("f1", "empty.txt", 0));

        using HttpResponseMessage response = await UploadAsync(session, "f1", []);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string path = Path.Combine(_downloads, "empty.txt");
        Assert.Equal(0, new FileInfo(path).Length);
        Assert.Equal([path], AllEntries());
    }

    [Fact]
    public async Task FileReceived_LongAlias_CappedAt64Characters()
    {
        string alias = new string('x', 63) + "\U0001F600" + new string('y', 40);
        using HttpResponseMessage prepare = await PrepareAsync(RequestFrom(alias, Entry("f1", "a.txt", 3)), ValidPin());
        PrepareUploadResponse? session = await prepare.Content.ReadFromJsonAsync<PrepareUploadResponse>(LocalSendJson.Options);
        Assert.NotNull(session);

        using HttpResponseMessage response = await UploadAsync(session, "f1", "abc"u8.ToArray());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ReceivedFile received = Assert.Single(_received);
        // Cut before the emoji rather than through its surrogate pair.
        Assert.Equal(new string('x', 63), received.SenderAlias);
    }

    [Fact]
    public async Task StartAsync_DeletesOnlyOwnOrphanedPartFiles()
    {
        await _server.StopAsync();
        string guid = Guid.NewGuid().ToString("N");
        string own = Path.Combine(_downloads, "." + guid + ".lsdz-part");
        string[] kept =
        [
            Path.Combine(_downloads, ".x.lsdz-part"),
            // A different GUID: names are case-insensitive on Windows, so guid.ToUpper would be the same file as own.
            Path.Combine(_downloads, "." + Guid.NewGuid().ToString("N").ToUpperInvariant() + ".lsdz-part"),
            Path.Combine(_downloads, guid + ".lsdz-part"),
            Path.Combine(_downloads, "." + guid + ".lsdz-part.txt"),
            Path.Combine(_downloads, "." + guid[..31] + ".lsdz-part"),
            Path.Combine(_downloads, "sub", "." + guid + ".lsdz-part"),
        ];
        Directory.CreateDirectory(Path.Combine(_downloads, "sub"));
        foreach (string path in kept.Append(own))
        {
            await File.WriteAllTextAsync(path, "x");
        }

        await _server.StartAsync(CancellationToken.None);

        Assert.False(File.Exists(own));
        Assert.All(kept, path => Assert.True(File.Exists(path), path));
    }

    private HttpClient CreateClient(IPAddress? localAddress, bool http2 = false)
    {
        var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            },
            PooledConnectionLifetime = TimeSpan.Zero,
        };
        if (localAddress is not null)
        {
            handler.ConnectCallback = async (context, ct) =>
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    socket.Bind(new IPEndPoint(localAddress, 0));
                    await socket.ConnectAsync(context.DnsEndPoint, ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            };
        }

        return new HttpClient(handler)
        {
            BaseAddress = new Uri($"https://127.0.0.1:{_port}"),
            Timeout = TimeSpan.FromSeconds(30),
            DefaultRequestVersion = http2 ? HttpVersion.Version20 : HttpVersion.Version11,
            DefaultVersionPolicy = http2 ? HttpVersionPolicy.RequestVersionExact : HttpVersionPolicy.RequestVersionOrLower,
        };
    }

    private Task<(TcpClient Tcp, SslStream Ssl)> StartRawUploadAsync(PrepareUploadResponse session, string fileId, long contentLength) =>
        OpenRawAsync(
            $"POST {UploadUri(session, fileId)} HTTP/1.1\r\n" +
            $"Host: 127.0.0.1:{_port}\r\n" +
            "Content-Type: application/octet-stream\r\n" +
            $"Content-Length: {contentLength}\r\n\r\n");

    /// <summary>Opens a TLS connection and sends only the request head, so the test controls the body.</summary>
    private async Task<(TcpClient Tcp, SslStream Ssl)> OpenRawAsync(string head)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, _port);
        var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false, (_, _, _, _) => true);
        await ssl.AuthenticateAsClientAsync("localhost");
        await ssl.WriteAsync(Encoding.ASCII.GetBytes(head));
        await ssl.FlushAsync();
        return (tcp, ssl);
    }

    /// <summary>Reads the status line of an HTTP/1.1 response and returns its status code.</summary>
    private static async Task<int> ReadStatusAsync(SslStream ssl)
    {
        var line = new StringBuilder();
        byte[] one = new byte[1];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!line.ToString().EndsWith("\r\n", StringComparison.Ordinal))
        {
            int read = await ssl.ReadAsync(one, timeout.Token);
            Assert.True(read == 1, "Connection closed before the status line.");
            line.Append((char)one[0]);
        }

        return int.Parse(line.ToString().Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);
    }

    private static PrepareUploadRequest Request(params FileDto[] files) => RequestFrom(SenderAlias, files);

    private static PrepareUploadRequest RequestFrom(string alias, params FileDto[] files) =>
        new(new DeviceInfo(alias, "2.1", null, "desktop", "sender-fingerprint", 53319, "https", false),
            files.ToDictionary(f => f.Id));

    private static FileDto Entry(string id, string name, long size) =>
        new(id, name, size, "application/octet-stream", null, null, null);

    private string? ReadLocalPassword()
    {
        if (_passwordLookupDelay > TimeSpan.Zero)
        {
            Thread.Sleep(_passwordLookupDelay);
        }

        return _localPassword;
    }

    private string ValidPin() => PasswordToken.Create(Password, _server.Fingerprint, _now);

    private static string PreparePath(string? pin) =>
        "/api/localsend/v2/prepare-upload" + (pin is null ? string.Empty : "?pin=" + Uri.EscapeDataString(pin));

    private static string UploadPath(string query) => "/api/localsend/v2/upload?" + query;

    private static string UploadUri(PrepareUploadResponse session, string fileId) =>
        UploadPath($"sessionId={Uri.EscapeDataString(session.SessionId)}&fileId={Uri.EscapeDataString(fileId)}" +
                   $"&token={Uri.EscapeDataString(session.Files[fileId])}");

    private Task<HttpResponseMessage> PrepareAsync(PrepareUploadRequest request, string? pin) =>
        PrepareAsync(_client, request, pin);

    private static Task<HttpResponseMessage> PrepareAsync(HttpClient client, PrepareUploadRequest request, string? pin) =>
        client.PostAsJsonAsync(PreparePath(pin), request, LocalSendJson.Options);

    private async Task<PrepareUploadResponse> PrepareOkAsync(params FileDto[] files)
    {
        using HttpResponseMessage response = await PrepareAsync(Request(files), ValidPin());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        PrepareUploadResponse? body = await response.Content.ReadFromJsonAsync<PrepareUploadResponse>(LocalSendJson.Options);
        Assert.NotNull(body);
        return body;
    }

    private Task<HttpResponseMessage> UploadAsync(PrepareUploadResponse session, string fileId, byte[] content)
    {
        var body = new ByteArrayContent(content);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return _client.PostAsync(UploadUri(session, fileId), body);
    }

    private ReceivedSession[] Sessions()
    {
        lock (_received)
        {
            return [.. _sessions];
        }
    }

    private string[] AllEntries() =>
        Directory.GetFileSystemEntries(_downloads, "*", SearchOption.AllDirectories);

    private string[] PartFiles() =>
        Directory.GetFiles(_downloads, "*.lsdz-part", SearchOption.AllDirectories);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition not met within 10 seconds.");
            await Task.Delay(50);
        }
    }

    /// <summary>One certificate for the whole class: RSA key generation is slow.</summary>
    public sealed class CertificateFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "dropzone-core-tests", Guid.NewGuid().ToString("N"));

        public CertificateFixture()
        {
            Certificate = CertificateStore.LoadOrCreate(Path.Combine(_directory, "cert.bin"));
        }

        public X509Certificate2 Certificate { get; }

        public void Dispose()
        {
            Certificate.Dispose();
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
