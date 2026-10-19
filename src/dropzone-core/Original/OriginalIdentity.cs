using System;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Dropzone.Core.Protocol;

namespace Dropzone.Core.Original;

/// <summary>
/// Works out the official app's identity from the certificate its HTTPS server presents on the loopback address.
/// The fingerprint is the SHA-256 of the certificate's DER bytes, in uppercase hex with no separators.
/// </summary>
public static class OriginalIdentity
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Connects to 127.0.0.1 on the official app's port, completes a TLS handshake that accepts any certificate,
    /// and returns the identity that the official app announces. Returns null on any failure, including the
    /// 2-second overall timeout.
    /// </summary>
    public static async Task<DeviceInfo?> ResolveAsync(OriginalSettings settings, CancellationToken ct)
    {
        try
        {
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(HandshakeTimeout);

            using TcpClient tcp = new();
            // Closing the socket is what stops a stuck connect or handshake when the deadline passes.
            using CancellationTokenRegistration closeOnDeadline = deadline.Token.Register(() => tcp.Dispose());
            await tcp.ConnectAsync(IPAddress.Loopback, settings.Port, deadline.Token);

            using SslStream tls = new(tcp.GetStream());
            await tls.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = "127.0.0.1",
                    RemoteCertificateValidationCallback = static (_, _, _, _) => true,
                },
                deadline.Token);

            X509Certificate? certificate = tls.RemoteCertificate;
            if (certificate is null)
            {
                return null;
            }

            string fingerprint = Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));
            return new DeviceInfo(settings.Alias, "2.2", "Windows", "desktop", fingerprint, settings.Port, "https", false);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
