using Dropzone.Core.Security;

namespace Dropzone.Core.Tests;

public class PasswordTokenTests
{
    private const string Password = "hunter2";
    private const string Fingerprint = "AB12AB12AB12AB12AB12AB12AB12AB12AB12AB12AB12AB12AB12AB12AB12AB12";
    private const string OtherFingerprint = "CD34CD34CD34CD34CD34CD34CD34CD34CD34CD34CD34CD34CD34CD34CD34CD34";
    private static readonly DateTimeOffset T0 = new(2026, 10, 19, 12, 0, 30, TimeSpan.Zero);

    [Fact]
    public void Create_ThenVerify_SameMinute_True()
    {
        string token = PasswordToken.Create(Password, Fingerprint, T0);

        Assert.True(PasswordToken.Verify(token, Password, Fingerprint, T0));
    }

    [Fact]
    public void Verify_WrongPassword_False()
    {
        string token = PasswordToken.Create(Password, Fingerprint, T0);

        Assert.False(PasswordToken.Verify(token, "hunter3", Fingerprint, T0));
    }

    [Fact]
    public void Verify_DifferentFingerprint_False()
    {
        string token = PasswordToken.Create(Password, Fingerprint, T0);

        Assert.False(PasswordToken.Verify(token, Password, OtherFingerprint, T0));
    }

    [Fact]
    public void Verify_FingerprintCaseInsensitive_True()
    {
        string token = PasswordToken.Create(Password, Fingerprint, T0);

        Assert.True(PasswordToken.Verify(token, Password, Fingerprint.ToLowerInvariant(), T0));
    }

    [Fact]
    public void Verify_TwoMinutesLater_True()
    {
        string token = PasswordToken.Create(Password, Fingerprint, T0);

        Assert.True(PasswordToken.Verify(token, Password, Fingerprint, T0.AddMinutes(2)));
    }

    [Fact]
    public void Verify_TwoMinutesEarlier_True()
    {
        string token = PasswordToken.Create(Password, Fingerprint, T0);

        Assert.True(PasswordToken.Verify(token, Password, Fingerprint, T0.AddMinutes(-2)));
    }

    [Fact]
    public void Verify_ThreeMinutesLater_False()
    {
        string token = PasswordToken.Create(Password, Fingerprint, T0);

        Assert.False(PasswordToken.Verify(token, Password, Fingerprint, T0.AddMinutes(3)));
    }

    [Fact]
    public void Verify_ThreeMinutesEarlier_False()
    {
        string token = PasswordToken.Create(Password, Fingerprint, T0);

        Assert.False(PasswordToken.Verify(token, Password, Fingerprint, T0.AddMinutes(-3)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-base64!!")]
    public void Verify_NullOrGarbage_False(string? token)
    {
        Assert.False(PasswordToken.Verify(token, Password, Fingerprint, T0));
    }

    [Fact]
    public void Create_DoesNotContainPassword()
    {
        string token = PasswordToken.Create(Password, Fingerprint, T0);

        Assert.DoesNotContain(Password, token);
    }

    [Fact]
    public void Create_IsUrlSafe()
    {
        string token = PasswordToken.Create(Password, Fingerprint, T0);

        Assert.Equal(22, token.Length);
        Assert.Matches("^[A-Za-z0-9_-]+$", token);
    }

    [Fact]
    public void Create_MatchesSpecifiedFormula()
    {
        // Known answer computed outside the code under test, with openssl (and cross-checked with .NET Framework
        // Rfc2898DeriveBytes + HMACSHA256 in Windows PowerShell 5.1):
        // key = PBKDF2-HMAC-SHA256("hunter2", salt "localsend-dropzone/v1", 600000 iterations, 32 bytes)
        //     = EFD403A00839678CF246CB1968832279461AE41013E461CF63A200B0117A844F;
        // HMAC-SHA256(key, "lsdz1|<lowercase fingerprint>|29873520"), first 16 bytes, base64url without padding.
        string token = PasswordToken.Create(Password, Fingerprint, T0);

        Assert.Equal("epCTOON1_dV4NwTJA-LXag", token);
    }
}
