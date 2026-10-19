using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;

namespace Dropzone.Core.Discovery;

/// <summary>A LocalSend server seen recently, kept so that it can be registered to again directly.</summary>
public sealed record KnownHost(IPAddress Ip, int Port, string Protocol, DateTimeOffset LastSeen);

/// <summary>
/// Hosts seen in the last 7 days, at most 50 (the newest), persisted as JSON. Each refresh registers to them, which
/// finds devices on subnets too large for the sweep (for example a Wi-Fi network that is a /20). Keyed by IP. Thread-safe.
/// </summary>
/// <param name="path">The JSON file; normally <see cref="DefaultPath"/>.</param>
public sealed class KnownHostsStore
{
    public const int MaxHosts = 50;

    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly object _gate = new();
    private readonly Dictionary<IPAddress, KnownHost> _hosts = [];
    private bool _dirty;

    public KnownHostsStore(string path)
    {
        _path = path;
        Read();
    }

    /// <summary><c>%LOCALAPPDATA%\localsend-dropzone\known-hosts.json</c>.</summary>
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "localsend-dropzone",
        "known-hosts.json");

    /// <summary>Hosts seen within the last 7 days, newest first.</summary>
    public IReadOnlyList<KnownHost> Hosts(DateTimeOffset now)
    {
        lock (_gate)
        {
            return Current(now).ToList();
        }
    }

    /// <summary>Adds the host at <paramref name="ip"/> or refreshes it with the given port, protocol and time.</summary>
    public void Record(IPAddress ip, int port, string protocol, DateTimeOffset now)
    {
        var host = new KnownHost(ip, port, protocol, now);
        lock (_gate)
        {
            if (_hosts.TryGetValue(ip, out KnownHost? old) && old == host)
            {
                return;
            }

            _hosts[ip] = host;
            _dirty = true;
            if (_hosts.Count > MaxHosts * 2)
            {
                Prune(now);
            }
        }
    }

    /// <summary>
    /// Drops hosts older than 7 days and all but the 50 newest, then writes the file through a temp file when
    /// anything changed since the last save. I/O errors propagate.
    /// </summary>
    public void Save(DateTimeOffset now)
    {
        lock (_gate)
        {
            int before = _hosts.Count;
            Prune(now);
            if (!_dirty && _hosts.Count == before)
            {
                return;
            }

            string? directory = Path.GetDirectoryName(Path.GetFullPath(_path));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            Entry[] entries = Current(now)
                .Select(h => new Entry(h.Ip.ToString(), h.Port, h.Protocol, h.LastSeen))
                .ToArray();
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(entries, JsonOptions));
            File.Move(temp, _path, overwrite: true);
            _dirty = false;
        }
    }

    private IEnumerable<KnownHost> Current(DateTimeOffset now) =>
        _hosts.Values
            .Where(h => now - h.LastSeen <= MaxAge)
            .OrderByDescending(h => h.LastSeen)
            .Take(MaxHosts);

    private void Prune(DateTimeOffset now)
    {
        HashSet<IPAddress> keep = Current(now).Select(h => h.Ip).ToHashSet();
        foreach (IPAddress ip in _hosts.Keys.Where(ip => !keep.Contains(ip)).ToList())
        {
            _hosts.Remove(ip);
        }
    }

    private void Read()
    {
        Entry[]? entries;
        try
        {
            entries = JsonSerializer.Deserialize<Entry[]>(File.ReadAllText(_path), JsonOptions);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // Missing, locked or corrupt: start empty; the next save replaces it.
            return;
        }

        foreach (Entry entry in entries ?? [])
        {
            if (entry is { Ip: not null, Protocol: "http" or "https", Port: >= 1 and <= 65535 }
                && IPAddress.TryParse(entry.Ip, out IPAddress? ip))
            {
                if (!_hosts.TryGetValue(ip, out KnownHost? existing) || existing.LastSeen < entry.LastSeen)
                {
                    _hosts[ip] = new KnownHost(ip, entry.Port, entry.Protocol, entry.LastSeen);
                }
            }
        }
    }

    private sealed record Entry(string? Ip, int Port, string? Protocol, DateTimeOffset LastSeen);
}
