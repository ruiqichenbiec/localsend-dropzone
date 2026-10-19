using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Dropzone.Core.Security;

/// <summary>
/// Password token carried in the <c>pin</c> query parameter of the dedicated port:
/// base64url(first 16 bytes of HMAC-SHA256(key: PBKDF2-HMAC-SHA256(UTF8(password), salt: UTF8("localsend-dropzone/v1"),
/// 600000 iterations, 32 bytes), data: UTF8("lsdz1|" + lowercase receiver fingerprint + "|" + Unix minutes))).
/// </summary>
public static class PasswordToken
{
    private const string Label = "lsdz1";
    private const int TagLength = 16;
    private const int WindowMinutes = 2;
    private const int KeyIterations = 600_000;
    private const int KeyLength = 32;
    private const int MaxCachedKeys = 8;
    private static readonly byte[] KeySalt = Encoding.UTF8.GetBytes("localsend-dropzone/v1");

    // PBKDF2 is deliberately slow; derived keys are cached per password so repeated checks stay cheap.
    private static readonly Dictionary<string, byte[]> KeyCache = new(StringComparer.Ordinal);
    private static readonly object KeyCacheGate = new();

    public static string Create(string password, string receiverFingerprint, DateTimeOffset now)
    {
        return Compute(DeriveKey(password), receiverFingerprint, UnixMinutes(now));
    }

    public static bool Verify(string? token, string password, string ownFingerprint, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        byte[] key = DeriveKey(password);
        byte[] presented = Encoding.UTF8.GetBytes(token);
        long minute = UnixMinutes(now);
        for (long candidate = minute - WindowMinutes; candidate <= minute + WindowMinutes; candidate++)
        {
            byte[] expected = Encoding.UTF8.GetBytes(Compute(key, ownFingerprint, candidate));
            if (CryptographicOperations.FixedTimeEquals(presented, expected))
            {
                return true;
            }
        }

        return false;
    }

    private static byte[] DeriveKey(string password)
    {
        lock (KeyCacheGate)
        {
            if (KeyCache.TryGetValue(password, out byte[]? cached))
            {
                return cached;
            }
        }

        byte[] key = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), KeySalt, KeyIterations, HashAlgorithmName.SHA256, KeyLength);
        lock (KeyCacheGate)
        {
            if (KeyCache.Count >= MaxCachedKeys)
            {
                KeyCache.Clear();
            }

            KeyCache[password] = key;
        }

        return key;
    }

    private static string Compute(byte[] key, string fingerprint, long minute)
    {
        string data = Label + "|" + fingerprint.ToLowerInvariant() + "|" + minute.ToString(CultureInfo.InvariantCulture);
        byte[] mac = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));
        return Base64Url.EncodeToString(mac.AsSpan(0, TagLength));
    }

    private static long UnixMinutes(DateTimeOffset now) => now.ToUnixTimeSeconds() / 60;
}
