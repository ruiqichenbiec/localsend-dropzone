using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dropzone.Core.Discovery;
using Dropzone.Core.Protocol;
using Dropzone.Core.Receive;
using Dropzone.Core.Security;

namespace Dropzone.Core.Send;

public enum SendOutcome
{
    Sent,

    /// <summary>The dedicated port refused the password token, and the normal route then succeeded.</summary>
    SentAfterPasswordMismatch,
    Rejected,
    Busy,
    Cancelled,
    Failed,
}

/// <param name="Detail">Optional Chinese explanation for the user, mainly for <see cref="SendOutcome.Failed"/>.</param>
public record SendResult(SendOutcome Outcome, string? Detail);

/// <summary>
/// Sends files to another device. Peers running this program are tried first on the dedicated port with a password
/// token (no prompt on their side); otherwise, or when that fails before any upload, the normal LocalSend v2 route is
/// used. Every request carries the official app's identity, never one of this program's own.
/// </summary>
/// <param name="originalIdentity">The official app's identity, sent as <c>info</c>; null when it cannot be read.</param>
/// <param name="password">Local password; null, empty or whitespace means "no password set".</param>
/// <param name="privatePort">The dedicated port, the same on every PC.</param>
/// <param name="clock">Current time for password tokens; defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
public sealed class TransferClient(
    Func<DeviceInfo?> originalIdentity,
    Func<string?> password,
    int privatePort,
    Func<DateTimeOffset>? clock = null)
{
    private const string HelloPath = "/api/lsdz/v1/hello";
    private const string PreparePath = "/api/localsend/v2/prepare-upload";
    private const string UploadPath = "/api/localsend/v2/upload";
    private const string CancelPath = "/api/localsend/v2/cancel";
    private const long MaxResponseBytes = 16 * 1024 * 1024;
    private const int UploadBufferSize = 1 << 16;

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(1.5);

    /// <summary>The dedicated port answers at once; anything slower is treated as unreachable.</summary>
    private static readonly TimeSpan PrivatePrepareTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The normal route's prepare-upload waits until the person on the receiving device taps accept.</summary>
    private static readonly TimeSpan NormalPrepareTimeout = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan CancelTimeout = TimeSpan.FromSeconds(5);

    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);

    /// <summary>
    /// Asks the dedicated port at <paramref name="ip"/> whether this program runs there. Returns the lowercase
    /// fingerprint of the certificate it presented when it did, else null (any failure, 1.5-second timeout).
    /// </summary>
    public async Task<string?> ProbePrivateAsync(IPAddress ip, CancellationToken ct)
    {
        string? fingerprint = null;
        using HttpClient client = CreateClient(certificate =>
        {
            // Trust on first use: the probe learns the certificate; later sends pin it.
            fingerprint = CertificateStore.Fingerprint(certificate);
            return true;
        });
        try
        {
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(ProbeTimeout);
            using HttpResponseMessage response = await client
                .GetAsync(BuildUri("https", ip, privatePort, HelloPath), deadline.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return null;
            }

            HelloResponse? hello = await response.Content
                .ReadFromJsonAsync<HelloResponse>(LocalSendJson.Options, deadline.Token).ConfigureAwait(false);
            return hello?.App == PrivateReceiveServer.AppName ? fingerprint : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Sends the files and folders in <paramref name="paths"/> to <paramref name="target"/>. Never throws for network
    /// or file problems; cancelling <paramref name="ct"/> yields <see cref="SendOutcome.Cancelled"/>.
    /// </summary>
    /// <param name="askPin">Asks the user for the receiver's PIN when it wants one; null means the user gave up.</param>
    public async Task<SendResult> SendAsync(
        DiscoveredDevice target, IReadOnlyList<string> paths, Func<Task<string?>> askPin, CancellationToken ct)
    {
        List<OutgoingFile>? files = DescribeFiles(FileEnumerator.Expand(paths), out string? unreadable);
        if (files is null)
        {
            return Failed($"读不到文件：{unreadable}");
        }

        if (files.Count == 0)
        {
            return Failed("没有可发送的文件");
        }

        DeviceInfo? identity = originalIdentity();
        try
        {
            bool passwordMismatch = false;
            string? localPassword = password();
            if (!string.IsNullOrEmpty(target.PrivateFingerprint) && !string.IsNullOrWhiteSpace(localPassword))
            {
                PrivateAttempt attempt = await SendPrivateAsync(target, target.PrivateFingerprint, localPassword,
                    identity ?? StandInIdentity(), files, ct).ConfigureAwait(false);
                if (attempt.Result is not null)
                {
                    return attempt.Result;
                }

                passwordMismatch = attempt.PasswordMismatch;
            }

            if (identity is null)
            {
                return Failed("读不到原版 LocalSend 的身份");
            }

            SendResult result = await SendNormalAsync(target, identity, files, askPin, ct).ConfigureAwait(false);
            return passwordMismatch && result.Outcome == SendOutcome.Sent
                ? result with { Outcome = SendOutcome.SentAfterPasswordMismatch }
                : result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new SendResult(SendOutcome.Cancelled, null);
        }
    }

    /// <summary>
    /// The dedicated port. Returns a final result once prepare-upload succeeded (uploads are not repeated on the
    /// normal route), and on 409 (the peer is busy). Any other failure before that returns no result so the caller falls
    /// back: 401 with the password-mismatch flag; unreachable, wrong certificate, timeout, 429, 5xx and the rest without.
    /// </summary>
    private async Task<PrivateAttempt> SendPrivateAsync(
        DiscoveredDevice target, string pinnedFingerprint, string localPassword, DeviceInfo identity,
        List<OutgoingFile> files, CancellationToken ct)
    {
        // Only a certificate with the probed fingerprint ever sees the token.
        using HttpClient client = CreateClient(certificate =>
            string.Equals(CertificateStore.Fingerprint(certificate), pinnedFingerprint, StringComparison.OrdinalIgnoreCase));

        DateTimeOffset now = _clock();
        // PBKDF2 is slow on first use; keep it off the caller's (UI) thread.
        string token = await Task.Run(() => PasswordToken.Create(localPassword, pinnedFingerprint, now), ct)
            .ConfigureAwait(false);

        HttpResponseMessage response;
        try
        {
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(PrivatePrepareTimeout);
            Uri uri = BuildUri("https", target.Ip, privatePort, PreparePath, ("pin", token));
            response = await client.PostAsync(uri, PrepareContent(identity, files), deadline.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException
                                      || (e is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return new PrivateAttempt(null, PasswordMismatch: false);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return new PrivateAttempt(null, PasswordMismatch: true);
            }

            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                // The peer is mid-transfer with another sender: report Busy rather than prompting on its official app.
                return new PrivateAttempt(new SendResult(SendOutcome.Busy, null), PasswordMismatch: false);
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                return new PrivateAttempt(null, PasswordMismatch: false);
            }

            PrepareUploadResponse? session = await ReadSessionAsync(response, ct).ConfigureAwait(false);
            if (session is null)
            {
                return new PrivateAttempt(null, PasswordMismatch: false);
            }

            SendResult result = await UploadAllAsync(client, "https", target.Ip, privatePort, session, files, ct)
                .ConfigureAwait(false);
            return new PrivateAttempt(result, PasswordMismatch: false);
        }
    }

    /// <summary>The normal LocalSend v2 route, as the official app would send.</summary>
    private static async Task<SendResult> SendNormalAsync(
        DiscoveredDevice target, DeviceInfo identity, List<OutgoingFile> files, Func<Task<string?>> askPin,
        CancellationToken ct)
    {
        string scheme = target.Protocol.ToLowerInvariant();
        if (scheme is not ("http" or "https"))
        {
            return Failed($"不支持的协议：{target.Protocol}");
        }

        bool certificateMismatch = false;
        using HttpClient client = CreateClient(certificate =>
        {
            // The official app announces the uppercase hex SHA-256 of its certificate (findings F1).
            bool matches = string.Equals(
                Convert.ToHexString(SHA256.HashData(certificate.RawData)), target.Fingerprint,
                StringComparison.OrdinalIgnoreCase);
            certificateMismatch = !matches;
            return matches;
        });

        string? pin = null;
        while (true)
        {
            HttpResponseMessage response;
            try
            {
                using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(NormalPrepareTimeout);
                Uri uri = pin is null
                    ? BuildUri(scheme, target.Ip, target.Port, PreparePath)
                    : BuildUri(scheme, target.Ip, target.Port, PreparePath, ("pin", pin));
                response = await client.PostAsync(uri, PrepareContent(identity, files), deadline.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return Failed("对方 5 分钟内没有回应");
            }
            catch (HttpRequestException)
            {
                return Failed(certificateMismatch ? "对方的证书与发现时不一致" : "连不上对方");
            }

            using (response)
            {
                switch (response.StatusCode)
                {
                    case HttpStatusCode.OK:
                        PrepareUploadResponse? session = await ReadSessionAsync(response, ct).ConfigureAwait(false);
                        return session is null
                            ? Failed("对方的回应无法识别")
                            : await UploadAllAsync(client, scheme, target.Ip, target.Port, session, files, ct)
                                .ConfigureAwait(false);
                    case HttpStatusCode.NoContent:
                        return new SendResult(SendOutcome.Sent, null);
                    case HttpStatusCode.Unauthorized when pin is null:
                        pin = await askPin().ConfigureAwait(false);
                        if (pin is null)
                        {
                            return new SendResult(SendOutcome.Cancelled, null);
                        }

                        continue;
                    case HttpStatusCode.Unauthorized:
                        return Failed("PIN 不正确");
                    case HttpStatusCode.Forbidden:
                        return new SendResult(SendOutcome.Rejected, null);
                    case HttpStatusCode.Conflict:
                    case HttpStatusCode.TooManyRequests:
                        return new SendResult(SendOutcome.Busy, null);
                    default:
                        return Failed($"对方返回 HTTP {(int)response.StatusCode}");
                }
            }
        }
    }

    /// <summary>
    /// Uploads every file the receiver accepted (files it left out of the reply are skipped). On a failure the
    /// session is cancelled on the receiver, best effort.
    /// </summary>
    private static async Task<SendResult> UploadAllAsync(
        HttpClient client, string scheme, IPAddress ip, int port, PrepareUploadResponse session,
        List<OutgoingFile> files, CancellationToken ct)
    {
        foreach (OutgoingFile file in files)
        {
            if (!session.Files.TryGetValue(file.Dto.Id, out string? token) || string.IsNullOrEmpty(token))
            {
                continue;
            }

            Uri uri = BuildUri(scheme, ip, port, UploadPath,
                ("sessionId", session.SessionId), ("fileId", file.Dto.Id), ("token", token));
            string? error;
            try
            {
                var stream = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    UploadBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var content = new StreamContent(stream, UploadBufferSize);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                using HttpResponseMessage response = await client.PostAsync(uri, content, ct).ConfigureAwait(false);
                error = response.IsSuccessStatusCode
                    ? null
                    : $"上传 {file.Dto.FileName} 失败（HTTP {(int)response.StatusCode}）";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await CancelSessionAsync(client, scheme, ip, port, session.SessionId).ConfigureAwait(false);
                throw;
            }
            catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException
                                          or OperationCanceledException)
            {
                error = $"上传 {file.Dto.FileName} 失败";
            }

            if (error is not null)
            {
                await CancelSessionAsync(client, scheme, ip, port, session.SessionId).ConfigureAwait(false);
                return Failed(error);
            }
        }

        return new SendResult(SendOutcome.Sent, null);
    }

    private static async Task CancelSessionAsync(HttpClient client, string scheme, IPAddress ip, int port, string sessionId)
    {
        try
        {
            using var deadline = new CancellationTokenSource(CancelTimeout);
            using HttpResponseMessage _ = await client
                .PostAsync(BuildUri(scheme, ip, port, CancelPath, ("sessionId", sessionId)), null, deadline.Token)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best effort: the receiver also expires idle sessions on its own.
        }
    }

    private static async Task<PrepareUploadResponse?> ReadSessionAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            PrepareUploadResponse? session = await response.Content
                .ReadFromJsonAsync<PrepareUploadResponse>(LocalSendJson.Options, ct).ConfigureAwait(false);
            return session is { SessionId: { Length: > 0 } }
                ? session with { Files = session.Files ?? [] }
                : null;
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>Reads size and modification time of every file; null (with the file's name) when one cannot be read.</summary>
    private static List<OutgoingFile>? DescribeFiles(
        IReadOnlyList<(string FullPath, string RelativeName)> expanded, out string? unreadable)
    {
        unreadable = null;
        var files = new List<OutgoingFile>(expanded.Count);
        foreach ((string fullPath, string relativeName) in expanded)
        {
            try
            {
                var info = new FileInfo(fullPath);
                var dto = new FileDto(
                    Guid.NewGuid().ToString("N"),
                    relativeName,
                    info.Length,
                    FileTypes.FromPath(fullPath),
                    Sha256: null,
                    Preview: null,
                    new FileMetadata(
                        info.LastWriteTimeUtc.ToString("o", CultureInfo.InvariantCulture), Accessed: null));
                files.Add(new OutgoingFile(fullPath, dto));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                unreadable = relativeName;
                return null;
            }
        }

        return files;
    }

    private static ByteArrayContent PrepareContent(DeviceInfo identity, List<OutgoingFile> files)
    {
        var request = new PrepareUploadRequest(identity, files.ToDictionary(f => f.Dto.Id, f => f.Dto, StringComparer.Ordinal));
        // Serialised up front so the request has a Content-Length rather than a chunked body.
        var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(request, LocalSendJson.Options));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return content;
    }

    private static HttpClient CreateClient(Func<X509Certificate2, bool> acceptCertificate)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = ConnectTimeout,
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    certificate is X509Certificate2 certificate2 && acceptCertificate(certificate2),
            },
        };
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            MaxResponseContentBufferSize = MaxResponseBytes,
        };
    }

    private static Uri BuildUri(string scheme, IPAddress ip, int port, string path, params (string Name, string Value)[] query)
    {
        var builder = new UriBuilder(scheme, ip.ToString(), port, path);
        if (query.Length > 0)
        {
            builder.Query = string.Join("&", Array.ConvertAll(query,
                q => Uri.EscapeDataString(q.Name) + "=" + Uri.EscapeDataString(q.Value)));
        }

        return builder.Uri;
    }

    /// <summary>
    /// Sender identity for the dedicated port when the official app's identity cannot be read. The private server reads
    /// only the alias (shown to the receiver); the fingerprint is a placeholder and never reaches an ordinary device.
    /// </summary>
    private DeviceInfo StandInIdentity() =>
        new(Environment.MachineName, "2.2", "Windows", "desktop", "localsend-dropzone", privatePort, "https", false);

    private static SendResult Failed(string detail) => new(SendOutcome.Failed, detail);

    private sealed record OutgoingFile(string FullPath, FileDto Dto);

    /// <param name="Result">Final result; null means "fall back to the normal route".</param>
    private sealed record PrivateAttempt(SendResult? Result, bool PasswordMismatch);
}
