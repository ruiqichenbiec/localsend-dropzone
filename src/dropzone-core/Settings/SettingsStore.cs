using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dropzone.Core.Settings;

/// <summary>
/// Reads and writes <see cref="AppSettings"/> as JSON. The password is stored only DPAPI-encrypted (current user) as
/// base64 in <c>passwordProtected</c>; the plain text never reaches the disk. Keeps the current settings in memory:
/// every change should go through <see cref="Update"/>, which applies it to the latest settings under a lock, so no
/// caller can write back a stale copy. Thread-safe.
/// </summary>
/// <param name="path">The JSON file; normally <see cref="DefaultPath"/>.</param>
/// <param name="originalPort">
/// The port the official app is configured for, or null when unknown; called on every read of the file. A saved
/// private port equal to it is invalid. Without it (the default), only 53317 is.
/// </param>
public sealed class SettingsStore(string path, Func<int?>? originalPort = null)
{
    private const string PasswordKey = "passwordProtected";
    private const string PortKey = "privatePort";
    private const string CornerKey = "corner";
    private const string AutoStartKey = "autoStart";
    private const string FirstRunDoneKey = "firstRunDone";
    private const string OriginalAutoStartKey = "originalAutoStart";
    private const int OriginalPort = 53317;

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("localsend-dropzone/settings/v1");

    /// <summary><c>%LOCALAPPDATA%\localsend-dropzone\settings.json</c>.</summary>
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "localsend-dropzone",
        "settings.json");

    private readonly object _gate = new();
    private AppSettings? _current;

    /// <summary>The settings in force: the last loaded, saved or updated ones (read from the file on first use).</summary>
    public AppSettings Current
    {
        get
        {
            lock (_gate)
            {
                return _current ??= Read();
            }
        }
    }

    /// <summary>
    /// Reads the file and makes it <see cref="Current"/>. A missing, unreadable or non-JSON file gives the defaults;
    /// a field that is missing or invalid (including a port of 53317 or the one the official app is configured for)
    /// gives that field's default, and a password that cannot be decrypted is treated as not set.
    /// </summary>
    public AppSettings Load()
    {
        lock (_gate)
        {
            _current = Read();
            return _current;
        }
    }

    /// <summary>Writes <paramref name="settings"/> and makes them <see cref="Current"/>. Prefer <see cref="Update"/>.</summary>
    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            Write(settings);
            _current = settings;
        }
    }

    /// <summary>
    /// Read-modify-write: applies <paramref name="change"/> to <see cref="Current"/>, writes the result and makes it
    /// current. If writing throws, <see cref="Current"/> is unchanged. Returns the new settings.
    /// </summary>
    public AppSettings Update(Func<AppSettings, AppSettings> change)
    {
        lock (_gate)
        {
            AppSettings next = change(_current ??= Read());
            Write(next);
            _current = next;
            return next;
        }
    }

    private AppSettings Read()
    {
        AppSettings defaults = new(null);
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return defaults;
        }

        if (root is null)
        {
            return defaults;
        }

        // 53317, or the port the official app was moved to, belongs to the official app; using it would make both
        // servers fight over the port.
        int port = TryGet(root, PortKey, out int p) && p is >= 1 and <= 65535 && p != OriginalPort
                   && p != originalPort?.Invoke()
            ? p
            : defaults.PrivatePort;
        Corner corner = TryGet(root, CornerKey, out string? c) && Enum.TryParse(c, ignoreCase: false, out Corner parsed)
                                                               && Enum.IsDefined(parsed)
            ? parsed
            : defaults.Corner;
        bool autoStart = TryGet(root, AutoStartKey, out bool a) ? a : defaults.AutoStart;
        bool firstRunDone = TryGet(root, FirstRunDoneKey, out bool f) ? f : defaults.FirstRunDone;
        bool? originalAutoStart = TryGet(root, OriginalAutoStartKey, out bool o) ? o : defaults.OriginalAutoStart;
        string? password = TryGet(root, PasswordKey, out string? protectedPassword) ? Unprotect(protectedPassword) : null;

        return new AppSettings(password, port, corner, autoStart, firstRunDone, originalAutoStart);
    }

    /// <summary>Writes the settings through a temp file, creating the folder when needed. Caller holds the lock.</summary>
    private void Write(AppSettings settings)
    {
        JsonObject root = new()
        {
            [PortKey] = settings.PrivatePort,
            [CornerKey] = settings.Corner.ToString(),
            [AutoStartKey] = settings.AutoStart,
            [FirstRunDoneKey] = settings.FirstRunDone,
        };
        if (settings.OriginalAutoStart is { } originalAutoStart)
        {
            root[OriginalAutoStartKey] = originalAutoStart;
        }

        if (!string.IsNullOrWhiteSpace(settings.Password))
        {
            root[PasswordKey] = Protect(settings.Password);
        }

        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temp = path + ".tmp";
        File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, overwrite: true);
    }

    private static string Protect(string password)
    {
        byte[] plain = Encoding.UTF8.GetBytes(password);
        try
        {
            return Convert.ToBase64String(ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private static string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return null;
        }

        try
        {
            byte[] plain = ProtectedData.Unprotect(Convert.FromBase64String(stored), Entropy, DataProtectionScope.CurrentUser);
            try
            {
                string password = Encoding.UTF8.GetString(plain);
                return string.IsNullOrWhiteSpace(password) ? null : password;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return null;
        }
    }

    private static bool TryGet<T>(JsonObject root, string key, out T? value)
    {
        value = default;
        if (root[key] is not JsonValue node)
        {
            return false;
        }

        return node.TryGetValue(out value);
    }
}
