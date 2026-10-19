using System.Collections.Generic;

namespace Dropzone.Core.Protocol;

/// <summary>Device identity: the body of <c>POST /api/localsend/v2/register</c> and the core of an announcement.</summary>
public record DeviceInfo(
    string Alias,
    string Version,
    string? DeviceModel,
    string? DeviceType,
    string Fingerprint,
    int Port,
    string Protocol,
    bool Download);

/// <summary>Multicast announcement: all DeviceInfo fields plus <c>announce</c>, which is true when peers should reply.</summary>
public record Announcement(
    string Alias,
    string Version,
    string? DeviceModel,
    string? DeviceType,
    string Fingerprint,
    int Port,
    string Protocol,
    bool Download,
    bool Announce)
{
    public DeviceInfo ToDeviceInfo() =>
        new(Alias, Version, DeviceModel, DeviceType, Fingerprint, Port, Protocol, Download);
}

/// <summary>Optional file timestamps, ISO 8601 strings.</summary>
public record FileMetadata(string? Modified, string? Accessed);

/// <summary>One file in a prepare-upload request. <c>FileType</c> is a MIME type such as <c>image/jpeg</c>.</summary>
public record FileDto(
    string Id,
    string FileName,
    long Size,
    string FileType,
    string? Sha256,
    string? Preview,
    FileMetadata? Metadata);

/// <summary>Body of <c>POST /api/localsend/v2/prepare-upload</c>.</summary>
public record PrepareUploadRequest(DeviceInfo Info, Dictionary<string, FileDto> Files);

/// <summary>Reply to <c>POST /api/localsend/v2/prepare-upload</c>: the session id and one token per accepted file id.</summary>
public record PrepareUploadResponse(string SessionId, Dictionary<string, string> Files);

/// <summary>Reply of the dedicated port's probe endpoint. <c>App</c> is always "localsend-dropzone".</summary>
public record HelloResponse(string App, string Version);
