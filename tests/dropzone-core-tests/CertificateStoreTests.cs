using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Dropzone.Core.Receive;

namespace Dropzone.Core.Tests;

public sealed class CertificateStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "dropzone-core-tests", Guid.NewGuid().ToString("N"));

    private string CertPath => Path.Combine(_directory, "nested", "cert.bin");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void LoadOrCreate_New_SelfSignedRsa2048ForTenYears()
    {
        using X509Certificate2 cert = CertificateStore.LoadOrCreate(CertPath);

        Assert.True(cert.HasPrivateKey);
        Assert.Equal("CN=localsend-dropzone", cert.Subject);
        Assert.Equal(cert.Subject, cert.Issuer);
        using RSA? rsa = cert.GetRSAPublicKey();
        Assert.NotNull(rsa);
        Assert.Equal(2048, rsa.KeySize);
        double years = (cert.NotAfter - DateTime.Now).TotalDays / 365.25;
        Assert.InRange(years, 9.9, 10.1);
        Assert.True(File.Exists(CertPath));
    }

    [Fact]
    public void LoadOrCreate_SecondCall_LoadsSameCertificate()
    {
        using X509Certificate2 first = CertificateStore.LoadOrCreate(CertPath);
        using X509Certificate2 second = CertificateStore.LoadOrCreate(CertPath);

        Assert.Equal(CertificateStore.Fingerprint(first), CertificateStore.Fingerprint(second));
        Assert.True(second.HasPrivateKey);
    }

    [Fact]
    public void LoadOrCreate_FileIsDpapiProtected()
    {
        using X509Certificate2 cert = CertificateStore.LoadOrCreate(CertPath);
        byte[] stored = File.ReadAllBytes(CertPath);

        Assert.ThrowsAny<CryptographicException>(() => X509CertificateLoader.LoadPkcs12(stored, null));
        byte[] pfx = ProtectedData.Unprotect(stored, null, DataProtectionScope.CurrentUser);
        using X509Certificate2 fromPfx = X509CertificateLoader.LoadPkcs12(pfx, null);
        Assert.Equal(CertificateStore.Fingerprint(cert), CertificateStore.Fingerprint(fromPfx));
    }

    [Fact]
    public void LoadOrCreate_CorruptFile_CreatesNewCertificate()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CertPath)!);
        File.WriteAllBytes(CertPath, [1, 2, 3, 4]);

        using X509Certificate2 cert = CertificateStore.LoadOrCreate(CertPath);

        Assert.True(cert.HasPrivateKey);
        using X509Certificate2 again = CertificateStore.LoadOrCreate(CertPath);
        Assert.Equal(CertificateStore.Fingerprint(cert), CertificateStore.Fingerprint(again));
    }

    [Fact]
    public void Fingerprint_IsLowercaseHexSha256OfDer()
    {
        using X509Certificate2 cert = CertificateStore.LoadOrCreate(CertPath);

        string fingerprint = CertificateStore.Fingerprint(cert);

        Assert.Equal(Convert.ToHexString(SHA256.HashData(cert.RawData)).ToLowerInvariant(), fingerprint);
        Assert.Matches("^[0-9a-f]{64}$", fingerprint);
    }
}
