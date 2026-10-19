using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dropzone.Core.Protocol;
using Dropzone.Core.Send;

namespace Dropzone.Core.Discovery;

/// <summary>A host to register to. A null <paramref name="Protocol"/> means "unknown": https and http are both tried.</summary>
public sealed record ScanTarget(IPAddress Ip, int Port, string? Protocol);

/// <summary>
/// Finds LocalSend devices by <c>POST /api/localsend/v2/register</c>, sent with the official app's identity so the
/// peer lists this PC as the one entry it already knows. Responders go into the registry and are probed for this
/// program's dedicated port. Skipping this PC's own addresses is the caller's job.
/// </summary>
/// <param name="originalIdentity">The register body; nothing is sent while it is null.</param>
/// <param name="handler">HTTP handler; defaults to one that accepts any certificate (peers use self-signed ones).</param>
/// <param name="clock">Time stamped on responders; defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
public sealed class SubnetScanner(
    DeviceRegistry registry,
    TransferClient client,
    Func<DeviceInfo?> originalIdentity,
    HttpMessageHandler? handler = null,
    Func<DateTimeOffset>? clock = null) : IDisposable
{
    public const int MaxInFlight = 64;

    public static readonly TimeSpan RequestTimeout = TimeSpan.FromMilliseconds(300);

    private const string RegisterPath = "/api/localsend/v2/register";
    private const int MaxResponseBytes = 64 * 1024;

    private readonly HttpClient _http = new(handler ?? CreateHandler(), disposeHandler: handler is null)
    {
        Timeout = Timeout.InfiniteTimeSpan,
        MaxResponseContentBufferSize = MaxResponseBytes,
    };

    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);

    /// <summary>Registers to every host on <paramref name="port"/>, trying https and http on each.</summary>
    public Task ScanAsync(IEnumerable<IPAddress> hosts, int port, CancellationToken ct) =>
        ScanAsync(hosts.Select(ip => new ScanTarget(ip, port, null)), ct);

    /// <summary>
    /// Registers to every target, 64 at a time, each attempt limited to 300 ms. Silent or failing hosts are skipped;
    /// only cancelling <paramref name="ct"/> ends the scan early (with <see cref="OperationCanceledException"/>).
    /// </summary>
    public async Task ScanAsync(IEnumerable<ScanTarget> targets, CancellationToken ct)
    {
        DeviceInfo? identity = originalIdentity();
        if (identity is null)
        {
            return;
        }

        byte[] body = JsonSerializer.SerializeToUtf8Bytes(identity, LocalSendJson.Options);
        await Parallel.ForEachAsync(
            targets,
            new ParallelOptions { MaxDegreeOfParallelism = MaxInFlight, CancellationToken = ct },
            async (target, token) => await ScanOneAsync(target, body, token).ConfigureAwait(false))
            .ConfigureAwait(false);
    }

    /// <summary>Registers to one target, as <see cref="ScanAsync(IEnumerable{ScanTarget}, CancellationToken)"/> does.</summary>
    public Task RegisterAsync(ScanTarget target, CancellationToken ct) => ScanAsync([target], ct);

    public void Dispose() => _http.Dispose();

    private async Task ScanOneAsync(ScanTarget target, byte[] body, CancellationToken ct)
    {
        string[] protocols = target.Protocol?.ToLowerInvariant() switch
        {
            null => ["https", "http"],
            "https" or "http" => [target.Protocol.ToLowerInvariant()],
            _ => [],
        };

        RegisterReply?[] replies = await Task.WhenAll(
            protocols.Select(p => RegisterAsync(p, target.Ip, target.Port, body, ct))).ConfigureAwait(false);
        int answered = Array.FindIndex(replies, r => r is not null);
        if (answered < 0)
        {
            return;
        }

        RegisterReply reply = replies[answered]!;
        // The reply may omit port and protocol; the ones that answered are the ones that work.
        var info = new DeviceInfo(reply.Alias!, reply.Version ?? "2.0", reply.DeviceModel, reply.DeviceType,
            reply.Fingerprint!, target.Port, protocols[answered], reply.Download ?? false);
        registry.Upsert(info, target.Ip, _clock());

        string? privateFingerprint = await client.ProbePrivateAsync(target.Ip, ct).ConfigureAwait(false);
        registry.SetPrivate(target.Ip, privateFingerprint);
    }

    /// <summary>The peer's info, or null for no answer within 300 ms, an error status or an unusable body.</summary>
    private async Task<RegisterReply?> RegisterAsync(
        string scheme, IPAddress ip, int port, byte[] body, CancellationToken ct)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(RequestTimeout);
        try
        {
            using var content = new ByteArrayContent(body);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            Uri uri = new UriBuilder(scheme, ip.ToString(), port, RegisterPath).Uri;
            using HttpResponseMessage response = await _http.PostAsync(uri, content, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            RegisterReply? reply = await response.Content
                .ReadFromJsonAsync<RegisterReply>(LocalSendJson.Options, deadline.Token).ConfigureAwait(false);
            return reply is { Alias: { Length: > 0 }, Fingerprint: { Length: > 0 } } ? reply : null;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private static SocketsHttpHandler CreateHandler() => new()
    {
        ConnectTimeout = RequestTimeout,
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(1),
        SslOptions = new SslClientAuthenticationOptions
        {
            // Discovery only: nothing secret is sent, and peers present self-signed certificates.
            RemoteCertificateValidationCallback = static (_, _, _, _) => true,
        },
    };

    /// <summary>Register reply: the peer's info, every field optional.</summary>
    private sealed record RegisterReply(
        string? Alias,
        string? Version,
        string? DeviceModel,
        string? DeviceType,
        string? Fingerprint,
        bool? Download);
}
