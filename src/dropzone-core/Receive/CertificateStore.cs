using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Dropzone.Core.Receive;

/// <summary>
/// The dedicated port's TLS certificate: self-signed RSA 2048, CN=localsend-dropzone, valid 10 years. The PFX bytes
/// are stored DPAPI-encrypted (current user) so the private key never sits on disk in the clear.
/// </summary>
public static class CertificateStore
{
    private const string SubjectName = "CN=localsend-dropzone";
    private const string ServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";

    /// <summary>
    /// Loads the certificate stored at <paramref name="path"/>. A missing, undecryptable, corrupt or expired file is
    /// replaced by a newly created certificate.
    /// </summary>
    public static X509Certificate2 LoadOrCreate(string path)
    {
        X509Certificate2? existing = TryLoad(path);
        if (existing is not null)
        {
            return existing;
        }

        byte[] pfx = CreatePfx();
        try
        {
            Save(path, pfx);
            // Re-import from PFX: a key fresh from CreateSelfSigned is ephemeral, which SChannel cannot use.
            return X509CertificateLoader.LoadPkcs12(pfx, null);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
        }
    }

    /// <summary>SHA-256 of the certificate's DER bytes, lowercase hex without separators.</summary>
    public static string Fingerprint(X509Certificate2 cert) =>
        Convert.ToHexStringLower(SHA256.HashData(cert.RawData));

    private static X509Certificate2? TryLoad(string path)
    {
        byte[] stored;
        try
        {
            stored = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }

        byte[]? pfx = null;
        try
        {
            pfx = ProtectedData.Unprotect(stored, null, DataProtectionScope.CurrentUser);
            X509Certificate2 cert = X509CertificateLoader.LoadPkcs12(pfx, null);
            if (!cert.HasPrivateKey || cert.NotAfter.ToUniversalTime() <= DateTime.UtcNow.AddDays(1))
            {
                cert.Dispose();
                return null;
            }

            return cert;
        }
        catch (CryptographicException)
        {
            return null;
        }
        finally
        {
            if (pfx is not null)
            {
                CryptographicOperations.ZeroMemory(pfx);
            }
        }
    }

    private static byte[] CreatePfx()
    {
        using RSA rsa = RSA.Create(2048);
        var request = new CertificateRequest(SubjectName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid(ServerAuthenticationOid) }, false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        DateTimeOffset now = DateTimeOffset.UtcNow;
        using X509Certificate2 cert = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(10));
        return cert.Export(X509ContentType.Pfx);
    }

    private static void Save(string path, byte[] pfx)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        byte[] protectedBytes = ProtectedData.Protect(pfx, null, DataProtectionScope.CurrentUser);
        string temp = path + ".tmp";
        File.WriteAllBytes(temp, protectedBytes);
        File.Move(temp, path, overwrite: true);
    }
}
