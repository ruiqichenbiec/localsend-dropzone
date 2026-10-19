using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dropzone.Core.Files;
using Dropzone.Core.Protocol;
using Dropzone.Core.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Dropzone.Core.Receive;

/// <param name="Port">TCP port of the dedicated HTTPS server.</param>
/// <param name="Certificate">TLS certificate; its fingerprint is what senders bind the password token to.</param>
/// <param name="Password">Current local password; null, empty or whitespace means "no password set".</param>
/// <param name="DownloadsDirectory">Folder that receives files. Nothing is ever written outside it.</param>
/// <param name="Clock">Current time: token checks, rate limiting, session idle time, and the mtime of received files.</param>
/// <param name="BindAddress">Address to listen on: <see cref="IPAddress.Any"/> in the app, <see cref="IPAddress.Loopback"/> in tests.</param>
public record PrivateServerOptions(
    int Port,
    X509Certificate2 Certificate,
    Func<string?> Password,
    string DownloadsDirectory,
    Func<DateTimeOffset> Clock,
    IPAddress BindAddress);

/// <summary>A file that was received completely and moved to its final name.</summary>
public record ReceivedFile(string FullPath, string SenderAlias);

/// <summary>The files one session received (at least one), in the order they arrived.</summary>
/// <param name="FailedCount">
/// How many of the session's files did not arrive: their upload failed, or the session ended before they were sent.
/// </param>
public record ReceivedSession(string SenderAlias, IReadOnlyList<string> Files, int FailedCount = 0)
{
    /// <summary>The deepest folder that holds every file (compared case-insensitively, as Windows does).</summary>
    public string CommonDirectory
    {
        get
        {
            string common = Path.GetDirectoryName(Files[0]) ?? Files[0];
            foreach (string file in Files.Skip(1))
            {
                string directory = Path.GetDirectoryName(file) ?? file;
                while (!IsSameOrUnder(directory, common))
                {
                    string? parent = Path.GetDirectoryName(common);
                    if (parent is null)
                    {
                        return common;
                    }

                    common = parent;
                }
            }

            return common;
        }
    }

    private static bool IsSameOrUnder(string directory, string ancestor) =>
        string.Equals(directory, ancestor, StringComparison.OrdinalIgnoreCase)
        || directory.StartsWith(
            Path.EndsInDirectorySeparator(ancestor) ? ancestor : ancestor + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// HTTPS receive server on the dedicated port. Peers running this program upload with a password-derived token;
/// accepted files go straight into the downloads folder without a prompt.
/// </summary>
public sealed class PrivateReceiveServer(PrivateServerOptions options) : IAsyncDisposable
{
    public const string AppName = "localsend-dropzone";

    private const string HelloPath = "/api/lsdz/v1/hello";
    private const string PreparePath = "/api/localsend/v2/prepare-upload";
    private const string UploadPath = "/api/localsend/v2/upload";
    private const string CancelPath = "/api/localsend/v2/cancel";
    private const string PartSuffix = ".lsdz-part";

    private const int MaxFailures = 5;
    private const int MaxGlobalFailures = 20;
    private const long MaxPrepareBodyBytes = 16 * 1024 * 1024;
    private const int MaxSegmentLength = 255;
    private const int MaxRelativePathLength = 1024;
    private const int MaxAliasLength = 64;
    private const int MoveAttempts = 5;
    private const int MaxTrackedIps = 1024;
    private static readonly TimeSpan FailureWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan BlockDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SessionIdleLimit = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan IdleCheckInterval = TimeSpan.FromSeconds(15);
    private static readonly string AppVersion = ReadVersion();

    /// <summary>Exactly the temp names this server creates; anything else in Downloads is never swept.</summary>
    private static readonly Regex OwnPartFileName = new(
        @"^\.[0-9a-f]{32}\.lsdz-part$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private readonly object _gate = new();
    private readonly Dictionary<IPAddress, FailureLog> _failures = [];
    private readonly FailureLog _globalFailures = new();
    private WebApplication? _app;
    private Timer? _idleTimer;
    private Session? _session;

    /// <summary>Fingerprint of this server's certificate (lowercase hex SHA-256 of the DER bytes).</summary>
    public string Fingerprint { get; } = CertificateStore.Fingerprint(options.Certificate);

    /// <summary>Raised on a request thread after a file is in its final place. Handler exceptions are swallowed.</summary>
    public event Action<ReceivedFile>? FileReceived;

    /// <summary>
    /// Raised once per session that received at least one file, when it ends: every file was uploaded (a failed upload
    /// counts; see <see cref="ReceivedSession.FailedCount"/>), the sender cancelled, it sat idle for 2 minutes, or the
    /// server stopped. Raised after the last file's <see cref="FileReceived"/>, on a request or timer thread. Handler
    /// exceptions are swallowed.
    /// </summary>
    public event Action<ReceivedSession>? SessionCompleted;

    /// <summary>
    /// Deletes temp files a previous run left behind, then starts listening on <c>BindAddress:Port</c>.
    /// Throws (IOException) when the port is taken.
    /// </summary>
    public async Task StartAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_app is not null)
            {
                throw new InvalidOperationException("The server is already running.");
            }
        }

        DeleteOrphanedPartFiles();
        WarmPasswordKey(options.Clock());

        WebApplicationBuilder builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.Logging.ClearProviders();
        builder.Services.Configure<HostOptions>(host => host.ShutdownTimeout = TimeSpan.FromSeconds(5));
        builder.WebHost.UseKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = null;
            kestrel.Listen(options.BindAddress, options.Port, listen => listen.UseHttps(options.Certificate));
        });

        WebApplication app = builder.Build();
        ((IApplicationBuilder)app).Run(HandleAsync);

        bool alreadyRunning;
        lock (_gate)
        {
            // Re-check: another StartAsync may have won while this one was building. The timer is created in the
            // same section that publishes _app, so a concurrent StopAsync always sees and disposes both.
            alreadyRunning = _app is not null;
            if (!alreadyRunning)
            {
                _app = app;
                _idleTimer = new Timer(_ => ExpireIdleSession(), null, IdleCheckInterval, IdleCheckInterval);
            }
        }

        if (alreadyRunning)
        {
            await app.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException("The server is already running.");
        }

        try
        {
            await app.StartAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            Timer? timer = null;
            lock (_gate)
            {
                if (ReferenceEquals(_app, app))
                {
                    _app = null;
                    timer = _idleTimer;
                    _idleTimer = null;
                }
            }

            if (timer is not null)
            {
                await timer.DisposeAsync().ConfigureAwait(false);
            }

            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Removes temp files left by a crash or power loss: only top-level Downloads files whose whole name matches
    /// this server's temp-name pattern. Names a peer could choose (they always get a different shape) are left alone.
    /// </summary>
    private void DeleteOrphanedPartFiles()
    {
        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            MatchType = MatchType.Simple,
            AttributesToSkip = 0,
            IgnoreInaccessible = true,
        };
        try
        {
            foreach (string path in Directory.EnumerateFiles(options.DownloadsDirectory, "*" + PartSuffix, enumeration))
            {
                if (OwnPartFileName.IsMatch(Path.GetFileName(path)))
                {
                    TryDelete(path);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Missing or unreadable Downloads folder: nothing to clean.
        }
    }

    /// <summary>Stops listening, ends any session and deletes its partial files. Safe to call when not running.</summary>
    public async Task StopAsync()
    {
        WebApplication? app;
        Timer? timer;
        Session? session;
        ReceivedSession? completed = null;
        lock (_gate)
        {
            app = _app;
            timer = _idleTimer;
            session = _session;
            _app = null;
            _idleTimer = null;
            _session = null;
            if (session is not null)
            {
                completed = EndSessionLocked(session);
            }
        }

        // Outside the lock: cancelling runs the aborted uploads' callbacks, which take the lock themselves.
        session?.Cancellation.Cancel();
        RaiseSessionCompleted(completed);
        if (timer is not null)
        {
            await timer.DisposeAsync().ConfigureAwait(false);
        }

        if (app is not null)
        {
            await app.StopAsync().ConfigureAwait(false);
            await app.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task HandleAsync(HttpContext context)
    {
        HttpRequest request = context.Request;
        IPAddress? remote = context.Connection.RemoteIpAddress;
        if (remote is null)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (remote.IsIPv4MappedToIPv6)
        {
            remote = remote.MapToIPv4();
        }

        string path = request.Path.Value ?? string.Empty;
        try
        {
            if (HttpMethods.IsGet(request.Method) && path == HelloPath)
            {
                await WriteJsonAsync(context, new HelloResponse(AppName, AppVersion)).ConfigureAwait(false);
            }
            else if (HttpMethods.IsPost(request.Method) && path == PreparePath)
            {
                await HandlePrepareAsync(context, remote).ConfigureAwait(false);
            }
            else if (HttpMethods.IsPost(request.Method) && path == UploadPath)
            {
                await HandleUploadAsync(context, remote).ConfigureAwait(false);
            }
            else if (HttpMethods.IsPost(request.Method) && path == CancelPath)
            {
                HandleCancel(context, remote);
            }
            else
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
            }
        }
        catch (Exception) when (!context.Response.HasStarted)
        {
            // Never let one bad request take the server down; Kestrel keeps serving others either way.
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        }
    }

    private async Task HandlePrepareAsync(HttpContext context, IPAddress remote)
    {
        DateTimeOffset now = options.Clock();
        int? refusal = Authorize(remote, SingleQueryValue(context.Request, "pin"), now);
        if (refusal is not null)
        {
            context.Response.StatusCode = refusal.Value;
            return;
        }

        if (IsSessionActive(now))
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            return;
        }

        PrepareUploadRequest? body = await ReadPrepareBodyAsync(context).ConfigureAwait(false);
        List<SessionFile>? files = body is null ? null : ValidateFiles(body);
        if (body is null || files is null)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var session = new Session(
            Guid.NewGuid().ToString("N"),
            remote,
            CapAlias(body.Info.Alias),
            files.ToDictionary(f => f.Id, StringComparer.Ordinal),
            now);
        ReceivedSession? expired;
        bool busy;
        lock (_gate)
        {
            expired = ExpireIdleSessionLocked(now);
            busy = _session is not null;
            if (!busy)
            {
                _session = session;
            }
        }

        RaiseSessionCompleted(expired);
        if (busy)
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            return;
        }

        var response = new PrepareUploadResponse(
            session.Id,
            files.ToDictionary(f => f.Id, f => f.Token, StringComparer.Ordinal));
        await WriteJsonAsync(context, response).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns null when the token is valid, else the status to answer (429 blocked, 401 refused). The block check,
    /// the token check and the failure count form one critical section, so parallel guesses (for example HTTP/2
    /// streams on one connection) cannot slip past the limit.
    /// </summary>
    private int? Authorize(IPAddress remote, string? pin, DateTimeOffset now)
    {
        // Outside the lock: the first use of a password runs the slow key derivation; afterwards it is cached.
        WarmPasswordKey(now);

        lock (_gate)
        {
            if (IsBlocked(remote, now))
            {
                return StatusCodes.Status429TooManyRequests;
            }

            string? password = options.Password();
            if (string.IsNullOrWhiteSpace(password))
            {
                // No password set: no token check took place, so nothing is counted.
                return StatusCodes.Status401Unauthorized;
            }

            if (!PasswordToken.Verify(pin, password, Fingerprint, now))
            {
                RecordFailure(remote, now);
                return StatusCodes.Status401Unauthorized;
            }

            return null;
        }
    }

    /// <summary>Runs the PBKDF2 key derivation for the current password (if any) so it lands in PasswordToken's cache.</summary>
    private void WarmPasswordKey(DateTimeOffset now)
    {
        string? password = options.Password();
        if (!string.IsNullOrWhiteSpace(password))
        {
            _ = PasswordToken.Create(password, Fingerprint, now);
        }
    }

    private static string CapAlias(string? alias)
    {
        if (alias is null || alias.Length <= MaxAliasLength)
        {
            return alias ?? string.Empty;
        }

        // Do not split a surrogate pair.
        int length = char.IsHighSurrogate(alias[MaxAliasLength - 1]) ? MaxAliasLength - 1 : MaxAliasLength;
        return alias[..length];
    }

    private static async Task<PrepareUploadRequest?> ReadPrepareBodyAsync(HttpContext context)
    {
        IHttpMaxRequestBodySizeFeature? limit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false })
        {
            limit.MaxRequestBodySize = MaxPrepareBodyBytes;
        }

        try
        {
            return await JsonSerializer.DeserializeAsync<PrepareUploadRequest>(
                context.Request.Body, LocalSendJson.Options, context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception e) when (e is JsonException or BadHttpRequestException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Checks peer input (System.Text.Json leaves missing members null) and maps every file name to a safe path
    /// under the downloads folder. Returns null when anything is invalid.
    /// </summary>
    private List<SessionFile>? ValidateFiles(PrepareUploadRequest body)
    {
        if (body.Info is null || body.Files is null || body.Files.Count == 0)
        {
            return null;
        }

        string root = Path.GetFullPath(options.DownloadsDirectory);
        var files = new List<SessionFile>(body.Files.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (FileDto? dto in body.Files.Values)
        {
            if (dto is null || string.IsNullOrEmpty(dto.Id) || dto.FileName is null || dto.Size < 0 || !ids.Add(dto.Id))
            {
                return null;
            }

            string? relative = SafePaths.ToSafeRelativePath(dto.FileName);
            if (relative is null
                || relative.Length > MaxRelativePathLength
                || relative.Split('\\').Any(segment => segment.Length > MaxSegmentLength
                    // Temp-file suffix is reserved: the startup sweep must never be able to hit a received file.
                    || segment.EndsWith(PartSuffix, StringComparison.OrdinalIgnoreCase))
                || !IsUnder(root, Path.GetFullPath(Path.Combine(root, relative))))
            {
                return null;
            }

            files.Add(new SessionFile(dto.Id, relative, dto.Size, NewToken()));
        }

        return files;
    }

    private async Task HandleUploadAsync(HttpContext context, IPAddress remote)
    {
        string? sessionId = SingleQueryValue(context.Request, "sessionId");
        string? fileId = SingleQueryValue(context.Request, "fileId");
        string? token = SingleQueryValue(context.Request, "token");
        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(fileId) || string.IsNullOrEmpty(token))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        Session? session;
        SessionFile? file;
        lock (_gate)
        {
            session = _session;
            if (session is null
                || !FixedTimeEquals(session.Id, sessionId)
                || !session.Ip.Equals(remote)
                || !session.Files.TryGetValue(fileId, out file)
                || !FixedTimeEquals(file.Token, token))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            // A failed file may be sent again while the session lasts.
            if (file.State is FileState.Uploading or FileState.Done)
            {
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                return;
            }

            file.State = FileState.Uploading;
            session.ActiveUploads++;
        }

        string root = Path.GetFullPath(options.DownloadsDirectory);
        string tempPath = Path.Combine(root, "." + Guid.NewGuid().ToString("N") + PartSuffix);
        string? finalPath = null;
        ReceivedSession? completed = null;
        try
        {
            using var aborted = CancellationTokenSource.CreateLinkedTokenSource(
                context.RequestAborted, session.Cancellation.Token);
            Directory.CreateDirectory(root);
            long received;
            var stream = new FileStream(
                tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous);
            await using (stream.ConfigureAwait(false))
            {
                received = await CopyAtMostAsync(context.Request.Body, stream, file.Size, aborted.Token).ConfigureAwait(false);
            }

            if (received != file.Size)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            aborted.Token.ThrowIfCancellationRequested();
            finalPath = MoveIntoPlace(tempPath, root, file.RelativePath);
            try
            {
                File.SetLastWriteTimeUtc(finalPath, options.Clock().UtcDateTime);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The file itself arrived; a failed mtime change must not make the sender retry and duplicate it.
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or UnauthorizedAccessException
                                      or BadHttpRequestException)
        {
            // Cancelled session, dropped connection, or a write/move failure (disk full, folder name taken by a file).
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = e is OperationCanceledException or BadHttpRequestException
                    ? StatusCodes.Status400BadRequest
                    : StatusCodes.Status500InternalServerError;
            }
        }
        finally
        {
            if (finalPath is null)
            {
                TryDelete(tempPath);
            }

            completed = FinishUpload(session, file, finalPath);
            if (finalPath is null)
            {
                // A failed upload can end the session itself (every other file is done or failed), or be the last one
                // of a session that ended meanwhile; either way the files that did arrive are reported now.
                RaiseSessionCompleted(completed);
            }
        }

        if (finalPath is not null)
        {
            RaiseFileReceived(new ReceivedFile(finalPath, session.SenderAlias));
            RaiseSessionCompleted(completed);
            context.Response.StatusCode = StatusCodes.Status200OK;
        }
    }

    /// <summary>Copies the body, reading at most <paramref name="expected"/> + 1 bytes so an oversized body is caught early.</summary>
    private static async Task<long> CopyAtMostAsync(Stream source, Stream destination, long expected, CancellationToken ct)
    {
        byte[] buffer = new byte[1 << 16];
        long total = 0;
        while (true)
        {
            int want = (int)Math.Min(buffer.Length, expected - total + 1);
            int read = await source.ReadAsync(buffer.AsMemory(0, want), ct).ConfigureAwait(false);
            if (read == 0)
            {
                return total;
            }

            total += read;
            if (total > expected)
            {
                return total;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Moves the finished temp file to a free name under <paramref name="root"/>, creating subfolders as needed.
    /// If another writer takes the chosen name in between, a new name is picked.
    /// </summary>
    private static string MoveIntoPlace(string tempPath, string root, string relativePath)
    {
        for (int attempt = 1; ; attempt++)
        {
            string finalPath = Path.GetFullPath(SafePaths.GetUniquePath(root, relativePath));
            if (!IsUnder(root, finalPath))
            {
                throw new IOException("Target path leaves the downloads folder.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
            try
            {
                File.Move(tempPath, finalPath, overwrite: false);
                return finalPath;
            }
            catch (IOException) when (attempt < MoveAttempts && (File.Exists(finalPath) || Directory.Exists(finalPath)))
            {
                // Name taken meanwhile: compute the next free one.
            }
        }
    }

    /// <summary>
    /// Books one finished upload (<paramref name="finalPath"/> is null when it failed). Returns the session's files when
    /// this ended it (every file has now arrived or failed) or it was the last upload of a session that had already
    /// ended; else null.
    /// </summary>
    private ReceivedSession? FinishUpload(Session session, SessionFile file, string? finalPath)
    {
        DateTimeOffset now = options.Clock();
        lock (_gate)
        {
            session.ActiveUploads--;
            session.LastActivity = now;
            file.State = finalPath is not null ? FileState.Done : FileState.Failed;
            if (finalPath is not null)
            {
                session.Received.Add(finalPath);
            }

            // Nothing is left to wait for. The sender does not retry a failed file, so it must not keep the session (and
            // its notification) waiting until the session goes idle.
            if (ReferenceEquals(_session, session)
                && session.Files.Values.All(f => f.State is FileState.Done or FileState.Failed))
            {
                _session = null;
                return EndSessionLocked(session);
            }

            return TakeCompletionLocked(session);
        }
    }

    /// <summary>
    /// Marks <paramref name="session"/> as ended (the caller has already removed it from <see cref="_session"/>) and
    /// returns its files if nothing is still uploading; otherwise the last running upload returns them.
    /// </summary>
    private static ReceivedSession? EndSessionLocked(Session session)
    {
        session.Ended = true;
        return TakeCompletionLocked(session);
    }

    /// <summary>
    /// The session's files, once: when it has ended, no upload runs and at least one file arrived. Every other file of
    /// the session counts as failed.
    /// </summary>
    private static ReceivedSession? TakeCompletionLocked(Session session)
    {
        if (!session.Ended || session.Reported || session.ActiveUploads > 0 || session.Received.Count == 0)
        {
            return null;
        }

        session.Reported = true;
        return new ReceivedSession(
            session.SenderAlias, [.. session.Received], session.Files.Count - session.Received.Count);
    }

    private void HandleCancel(HttpContext context, IPAddress remote)
    {
        string? sessionId = SingleQueryValue(context.Request, "sessionId");
        if (string.IsNullOrEmpty(sessionId))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        Session? session;
        ReceivedSession? completed;
        lock (_gate)
        {
            session = _session;
            if (session is null || !FixedTimeEquals(session.Id, sessionId) || !session.Ip.Equals(remote))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            _session = null;
            completed = EndSessionLocked(session);
        }

        // Running uploads see the cancellation, stop, and delete their temp files.
        session.Cancellation.Cancel();
        RaiseSessionCompleted(completed);
        context.Response.StatusCode = StatusCodes.Status200OK;
    }

    private bool IsSessionActive(DateTimeOffset now)
    {
        ReceivedSession? expired;
        bool active;
        lock (_gate)
        {
            expired = ExpireIdleSessionLocked(now);
            active = _session is not null;
        }

        RaiseSessionCompleted(expired);
        return active;
    }

    private void ExpireIdleSession()
    {
        ReceivedSession? expired;
        lock (_gate)
        {
            expired = ExpireIdleSessionLocked(options.Clock());
        }

        RaiseSessionCompleted(expired);
    }

    /// <summary>
    /// Ends the session when no upload is running and nothing happened for 2 minutes. Temp files only exist while
    /// an upload runs, so an idle session has none left to delete. Returns its files when it had received any; the
    /// caller raises <see cref="SessionCompleted"/> after leaving the lock.
    /// </summary>
    private ReceivedSession? ExpireIdleSessionLocked(DateTimeOffset now)
    {
        if (_session is { ActiveUploads: 0 } session && now - session.LastActivity >= SessionIdleLimit)
        {
            _session = null;
            return EndSessionLocked(session);
        }

        return null;
    }

    private bool IsBlocked(IPAddress ip, DateTimeOffset now) =>
        now < _globalFailures.BlockedUntil
        || (_failures.TryGetValue(ip, out FailureLog? log) && now < log.BlockedUntil);

    /// <summary>
    /// Counts a failed token check: 5 from one IP within 60 s block that IP for 5 minutes; 20 from all IPs together
    /// within 60 s block every prepare-upload for 5 minutes (a guesser spread over many addresses).
    /// </summary>
    private void RecordFailure(IPAddress ip, DateTimeOffset now)
    {
        _globalFailures.Recent.RemoveAll(t => now - t >= FailureWindow);
        _globalFailures.Recent.Add(now);
        if (_globalFailures.Recent.Count >= MaxGlobalFailures)
        {
            _globalFailures.BlockedUntil = now + BlockDuration;
            _globalFailures.Recent.Clear();
        }

        if (!_failures.TryGetValue(ip, out FailureLog? log))
        {
            if (_failures.Count >= MaxTrackedIps)
            {
                PruneFailures(now);
            }

            log = new FailureLog();
            _failures[ip] = log;
        }

        log.Recent.RemoveAll(t => now - t >= FailureWindow);
        log.Recent.Add(now);
        if (log.Recent.Count >= MaxFailures)
        {
            log.BlockedUntil = now + BlockDuration;
            log.Recent.Clear();
        }
    }

    private void PruneFailures(DateTimeOffset now)
    {
        foreach (IPAddress ip in _failures.Keys.ToList())
        {
            FailureLog log = _failures[ip];
            log.Recent.RemoveAll(t => now - t >= FailureWindow);
            if (log.Recent.Count == 0 && now >= log.BlockedUntil)
            {
                _failures.Remove(ip);
            }
        }
    }

    private void RaiseFileReceived(ReceivedFile file)
    {
        if (FileReceived is not { } handlers)
        {
            return;
        }

        foreach (Action<ReceivedFile> handler in handlers.GetInvocationList().Cast<Action<ReceivedFile>>())
        {
            try
            {
                handler(file);
            }
            catch (Exception)
            {
                // A subscriber's failure must not turn a completed upload into an error for the sender.
            }
        }
    }

    private void RaiseSessionCompleted(ReceivedSession? session)
    {
        if (session is null || SessionCompleted is not { } handlers)
        {
            return;
        }

        foreach (Action<ReceivedSession> handler in handlers.GetInvocationList().Cast<Action<ReceivedSession>>())
        {
            try
            {
                handler(session);
            }
            catch (Exception)
            {
                // Same as FileReceived: a subscriber's failure must not reach the sender or the timer thread.
            }
        }
    }

    private static async Task WriteJsonAsync<T>(HttpContext context, T value)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(context.Response.Body, value, LocalSendJson.Options, context.RequestAborted)
            .ConfigureAwait(false);
    }

    private static string? SingleQueryValue(HttpRequest request, string name)
    {
        StringValues values = request.Query[name];
        return values.Count == 1 ? values[0] : null;
    }

    private static bool IsUnder(string root, string fullPath)
    {
        string prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool FixedTimeEquals(string expected, string presented) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(presented));

    private static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string ReadVersion()
    {
        string? version = typeof(PrivateReceiveServer).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(version))
        {
            return "0.1.0";
        }

        int metadata = version.IndexOf('+');
        return metadata > 0 ? version[..metadata] : version;
    }

    private enum FileState
    {
        Pending,
        Uploading,
        Done,
        Failed,
    }

    private sealed class SessionFile(string id, string relativePath, long size, string token)
    {
        public string Id { get; } = id;

        public string RelativePath { get; } = relativePath;

        public long Size { get; } = size;

        public string Token { get; } = token;

        public FileState State { get; set; } = FileState.Pending;
    }

    private sealed class Session(
        string id, IPAddress ip, string senderAlias, Dictionary<string, SessionFile> files, DateTimeOffset now)
    {
        public string Id { get; } = id;

        public IPAddress Ip { get; } = ip;

        public string SenderAlias { get; } = senderAlias;

        public Dictionary<string, SessionFile> Files { get; } = files;

        /// <summary>Cancelled when the session ends while uploads may still run (cancel, server stop).</summary>
        public CancellationTokenSource Cancellation { get; } = new();

        public int ActiveUploads { get; set; }

        public DateTimeOffset LastActivity { get; set; } = now;

        /// <summary>Final paths of the files that arrived, in order.</summary>
        public List<string> Received { get; } = [];

        /// <summary>No longer the current session (all files arrived, cancelled, idle, or the server stopped).</summary>
        public bool Ended { get; set; }

        /// <summary><see cref="SessionCompleted"/> has been raised for it.</summary>
        public bool Reported { get; set; }
    }

    private sealed class FailureLog
    {
        public List<DateTimeOffset> Recent { get; } = [];

        public DateTimeOffset BlockedUntil { get; set; } = DateTimeOffset.MinValue;
    }
}
