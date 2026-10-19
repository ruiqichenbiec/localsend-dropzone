using System;
using System.Net;

namespace Dropzone.Core.Discovery;

/// <summary>A device seen on the network.</summary>
/// <param name="Alias">Name the device announces.</param>
/// <param name="DeviceType">LocalSend device type such as "mobile" or "desktop"; null when not announced.</param>
/// <param name="Fingerprint">Fingerprint the device announces (the official app: uppercase hex SHA-256 of its certificate).</param>
/// <param name="Ip">Address the device was seen at.</param>
/// <param name="Port">Port of its LocalSend v2 server.</param>
/// <param name="Protocol">"https" or "http" (Android phones announce http).</param>
/// <param name="PrivateFingerprint">
/// Lowercase certificate fingerprint of this program's dedicated port on that device, learned by probing; null when the
/// device does not run this program.
/// </param>
/// <param name="LastSeen">When the device was last heard from.</param>
public record DiscoveredDevice(
    string Alias,
    string? DeviceType,
    string Fingerprint,
    IPAddress Ip,
    int Port,
    string Protocol,
    string? PrivateFingerprint,
    DateTimeOffset LastSeen);
