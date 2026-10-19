using System;
using System.Text.Json;
using Dropzone.Core.Protocol;

namespace Dropzone.Core.Discovery;

/// <summary>Reads a LocalSend multicast announcement datagram.</summary>
public static class AnnouncementParser
{
    /// <summary>
    /// The announcement in <paramref name="datagram"/>, or null when it is not one: not JSON, no alias or fingerprint,
    /// a port outside 1–65535, or a protocol other than http/https. Missing <c>version</c>, <c>port</c> and
    /// <c>protocol</c> take LocalSend's defaults ("1.0", 53317, "https"), as older versions omit them. The announce flag
    /// is set when either <c>announce</c> or the older <c>announcement</c> is true.
    /// </summary>
    public static Announcement? TryParse(ReadOnlySpan<byte> datagram)
    {
        Wire? wire;
        try
        {
            wire = JsonSerializer.Deserialize<Wire>(datagram, LocalSendJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }

        if (wire is null || string.IsNullOrWhiteSpace(wire.Alias) || string.IsNullOrWhiteSpace(wire.Fingerprint))
        {
            return null;
        }

        int port = wire.Port ?? 53317;
        string protocol = (wire.Protocol ?? "https").ToLowerInvariant();
        if (port is < 1 or > 65535 || protocol is not ("http" or "https"))
        {
            return null;
        }

        return new Announcement(wire.Alias, wire.Version ?? "1.0", wire.DeviceModel, wire.DeviceType, wire.Fingerprint,
            port, protocol, wire.Download ?? false, wire.Announce == true || wire.Announcement == true);
    }

    /// <summary>Every field optional, so that validation decides rather than the deserializer.</summary>
    private sealed record Wire(
        string? Alias,
        string? Version,
        string? DeviceModel,
        string? DeviceType,
        string? Fingerprint,
        int? Port,
        string? Protocol,
        bool? Download,
        bool? Announce,
        bool? Announcement);
}
