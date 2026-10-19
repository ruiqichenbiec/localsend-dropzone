using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Dropzone.Core.Protocol;
using Dropzone.Core.Receive;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dropzone.Core.Tests.TestSupport;

/// <summary>How the fake answers prepare-upload.</summary>
public enum FakeMode
{
    Accept,
    RequirePin,
    Reject,
    Busy,
    TooManyRequests,
    NoContent,
    ServerError,
    FailUploads,
}

/// <summary>
/// A minimal LocalSend v2 receiver standing in for the official app, bound to the loopback address. It records every
/// request URL and body and the <c>info</c> of every prepare-upload, and saves uploads under <see cref="Directory"/>.
/// </summary>
internal sealed class FakeLocalSendServer : IAsyncDisposable
{
    public const string RequiredPin = "1234";

    private const string PreparePath = "/api/localsend/v2/prepare-upload";
    private const string UploadPath = "/api/localsend/v2/upload";
    private const string CancelPath = "/api/localsend/v2/cancel";
    private const string RegisterPath = "/api/localsend/v2/register";

    private readonly object _gate = new();
    private readonly List<string> _urls = [];
    private readonly List<string> _bodies = [];
    private readonly List<DeviceInfo> _infos = [];
    private readonly List<DeviceInfo> _registrations = [];
    private readonly Dictionary<string, (string FileName, string Token)> _files = [];
    private readonly X509Certificate2? _certificate;
    private WebApplication? _app;
    private string? _sessionId;

    private FakeLocalSendServer(bool https, int port, string root, IPAddress address)
    {
        Https = https;
        Port = port;
        Address = address;
        Directory = Path.Combine(root, "received");
        System.IO.Directory.CreateDirectory(Directory);
        if (https)
        {
            _certificate = CertificateStore.LoadOrCreate(Path.Combine(root, "cert.bin"));
            Fingerprint = Convert.ToHexString(SHA256.HashData(_certificate.RawData));
        }
        else
        {
            Fingerprint = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        }
    }

    public bool Https { get; }

    public int Port { get; }

    /// <summary>Loopback address the fake listens on (127.0.0.1 unless another 127.x address was asked for).</summary>
    public IPAddress Address { get; }

    /// <summary>Alias the fake returns from register.</summary>
    public string Alias { get; set; } = "FakePeer";

    /// <summary>What the official app would announce: uppercase hex SHA-256 of the certificate (F1).</summary>
    public string Fingerprint { get; }

    /// <summary>Lowercase fingerprint, the format of <see cref="CertificateStore.Fingerprint"/>.</summary>
    public string LowercaseFingerprint => Fingerprint.ToLowerInvariant();

    public string Directory { get; }

    public FakeMode Mode { get; set; } = FakeMode.Accept;

    public int CancelCount { get; private set; }

    public IReadOnlyList<string> Urls
    {
        get { lock (_gate) { return [.. _urls]; } }
    }

    public IReadOnlyList<string> Bodies
    {
        get { lock (_gate) { return [.. _bodies]; } }
    }

    public IReadOnlyList<DeviceInfo> Infos
    {
        get { lock (_gate) { return [.. _infos]; } }
    }

    /// <summary>Every body POSTed to register, in arrival order.</summary>
    public IReadOnlyList<DeviceInfo> Registrations
    {
        get { lock (_gate) { return [.. _registrations]; } }
    }

    /// <param name="address">A loopback address to bind; defaults to 127.0.0.1. Never a non-loopback address.</param>
    public static async Task<FakeLocalSendServer> StartAsync(
        string root, bool https = true, int? port = null, IPAddress? address = null)
    {
        address ??= IPAddress.Loopback;
        if (!IPAddress.IsLoopback(address))
        {
            throw new ArgumentException("The fake binds loopback addresses only.", nameof(address));
        }

        var server = new FakeLocalSendServer(https, port ?? FreePort.Get(), root, address);
        await server.StartCoreAsync();
        return server;
    }

    private async Task StartCoreAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(kestrel =>
        {
            kestrel.Limits.MaxRequestBodySize = null;
            kestrel.Listen(Address, Port, listen =>
            {
                if (_certificate is not null)
                {
                    listen.UseHttps(_certificate);
                }
            });
        });
        _app = builder.Build();
        ((IApplicationBuilder)_app).Run(HandleAsync);
        await _app.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        _certificate?.Dispose();
    }

    private async Task HandleAsync(HttpContext context)
    {
        HttpRequest request = context.Request;
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, context.RequestAborted);
        byte[] body = buffer.ToArray();
        lock (_gate)
        {
            _urls.Add(request.Path.Value + request.QueryString.Value);
            _bodies.Add(Encoding.UTF8.GetString(body));
        }

        string path = request.Path.Value ?? string.Empty;
        if (HttpMethods.IsPost(request.Method) && path == PreparePath)
        {
            await PrepareAsync(context, body);
        }
        else if (HttpMethods.IsPost(request.Method) && path == UploadPath)
        {
            await UploadAsync(context, body);
        }
        else if (HttpMethods.IsPost(request.Method) && path == RegisterPath)
        {
            await RegisterAsync(context, body);
        }
        else if (HttpMethods.IsPost(request.Method) && path == CancelPath)
        {
            lock (_gate)
            {
                CancelCount++;
                _sessionId = null;
            }

            context.Response.StatusCode = StatusCodes.Status200OK;
        }
        else
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
        }
    }

    /// <summary>
    /// Answers like the official app: its own info without <c>port</c> or <c>protocol</c> (the caller knows those).
    /// </summary>
    private async Task RegisterAsync(HttpContext context, byte[] body)
    {
        DeviceInfo? info;
        try
        {
            info = JsonSerializer.Deserialize<DeviceInfo>(body, LocalSendJson.Options);
        }
        catch (JsonException)
        {
            info = null;
        }

        if (info is null)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        lock (_gate)
        {
            _registrations.Add(info);
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.Body, new
        {
            alias = Alias,
            version = "2.2",
            deviceModel = "Pixel 8",
            deviceType = "mobile",
            fingerprint = Fingerprint,
            download = false,
        });
    }

    private async Task PrepareAsync(HttpContext context, byte[] body)
    {
        PrepareUploadRequest? prepare = JsonSerializer.Deserialize<PrepareUploadRequest>(body, LocalSendJson.Options);
        if (prepare?.Info is null || prepare.Files is null)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        lock (_gate)
        {
            _infos.Add(prepare.Info);
        }

        int? refusal = Mode switch
        {
            FakeMode.RequirePin when context.Request.Query["pin"] != RequiredPin => StatusCodes.Status401Unauthorized,
            FakeMode.Reject => StatusCodes.Status403Forbidden,
            FakeMode.Busy => StatusCodes.Status409Conflict,
            FakeMode.TooManyRequests => StatusCodes.Status429TooManyRequests,
            FakeMode.NoContent => StatusCodes.Status204NoContent,
            FakeMode.ServerError => StatusCodes.Status500InternalServerError,
            _ => null,
        };
        if (refusal is not null)
        {
            context.Response.StatusCode = refusal.Value;
            return;
        }

        string sessionId = Guid.NewGuid().ToString("N");
        var tokens = new Dictionary<string, string>();
        lock (_gate)
        {
            _sessionId = sessionId;
            _files.Clear();
            foreach ((string id, FileDto file) in prepare.Files)
            {
                string token = Guid.NewGuid().ToString("N");
                _files[id] = (file.FileName, token);
                tokens[id] = token;
            }
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(
            context.Response.Body, new PrepareUploadResponse(sessionId, tokens), LocalSendJson.Options);
    }

    private async Task UploadAsync(HttpContext context, byte[] body)
    {
        string? sessionId = context.Request.Query["sessionId"];
        string? fileId = context.Request.Query["fileId"];
        string? token = context.Request.Query["token"];
        (string FileName, string Token) file;
        lock (_gate)
        {
            if (Mode == FakeMode.FailUploads)
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                return;
            }

            if (_sessionId is null || sessionId != _sessionId || fileId is null
                || !_files.TryGetValue(fileId, out file) || file.Token != token)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
        }

        string target = Path.Combine(Directory, file.FileName.Replace('/', Path.DirectorySeparatorChar));
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllBytesAsync(target, body);
        context.Response.StatusCode = StatusCodes.Status200OK;
    }
}
