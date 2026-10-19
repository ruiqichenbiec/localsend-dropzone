using System.Text.Json;
using Dropzone.Core.Protocol;

namespace Dropzone.Core.Tests;

public class ProtocolJsonTests
{

    [Fact]
    public void Deserialize_PrepareUploadExample()
    {
        // Shape of a LocalSend v2 prepare-upload request. Protocol: https://github.com/localsend/protocol
        const string example = """
            {
              "info": {
                "alias": "Blue Falcon",
                "version": "2.0",
                "deviceModel": "Pixel 8",
                "deviceType": "mobile",
                "fingerprint": "0123456789abcdef",
                "port": 53317,
                "protocol": "https",
                "download": true
              },
              "files": {
                "file-a": {
                  "id": "file-a",
                  "fileName": "holiday.jpg",
                  "size": 324242,
                  "fileType": "image/jpeg",
                  "sha256": "0123456789abcdef",
                  "preview": "preview-data",
                  "metadata": {
                    "modified": "2021-01-01T12:34:56Z",
                    "accessed": "2021-01-01T12:34:56Z"
                  }
                },
                "file-b": {
                  "id": "file-b",
                  "fileName": "notes.txt",
                  "size": 1234,
                  "fileType": "text/plain",
                  "sha256": null,
                  "preview": null
                }
              }
            }
            """;

        PrepareUploadRequest? request = JsonSerializer.Deserialize<PrepareUploadRequest>(example, LocalSendJson.Options);

        Assert.NotNull(request);
        Assert.Equal("Blue Falcon", request.Info.Alias);
        Assert.Equal(2, request.Files.Count);
        Assert.Equal(324242L, request.Files["file-a"].Size);
        Assert.Equal(1234L, request.Files["file-b"].Size);
    }

    [Fact]
    public void Deserialize_AnnouncementWithUnknownFields_Ok()
    {
        const string json = """
            {
              "alias": "Test Desktop",
              "version": "2.2",
              "deviceModel": "Windows",
              "deviceType": "desktop",
              "fingerprint": "0123456789ABCDEF",
              "port": 53317,
              "protocol": "https",
              "download": false,
              "announce": true,
              "foo": 1
            }
            """;

        Announcement? announcement = JsonSerializer.Deserialize<Announcement>(json, LocalSendJson.Options);

        Assert.NotNull(announcement);
        Assert.Equal("Test Desktop", announcement.Alias);
        Assert.True(announcement.Announce);
    }

    [Fact]
    public void Deserialize_AnnouncementWithoutDownload_DefaultsToFalse()
    {
        const string json = """{"alias":"Test Phone","version":"2.2","fingerprint":"FEDCBA9876543210","port":53317,"protocol":"http","announce":true}""";

        Announcement? announcement = JsonSerializer.Deserialize<Announcement>(json, LocalSendJson.Options);

        Assert.NotNull(announcement);
        Assert.False(announcement.Download);
    }

    [Fact]
    public void Deserialize_AnnouncementWithoutAnnounce_DefaultsToFalse()
    {
        const string json = """{"alias":"Test Phone","version":"2.2","fingerprint":"FEDCBA9876543210","port":53317,"protocol":"http","download":false}""";

        Announcement? announcement = JsonSerializer.Deserialize<Announcement>(json, LocalSendJson.Options);

        Assert.NotNull(announcement);
        Assert.False(announcement.Announce);
    }

    [Fact]
    public void Deserialize_PropertyNamesMatchCaseInsensitively()
    {
        const string json = """{"ALIAS":"Test Phone","Version":"2.2","FINGERPRINT":"FEDCBA9876543210","PORT":53317,"Protocol":"http","Download":true,"Announce":true}""";

        Announcement? announcement = JsonSerializer.Deserialize<Announcement>(json, LocalSendJson.Options);

        Assert.NotNull(announcement);
        Assert.Equal("Test Phone", announcement.Alias);
        Assert.Equal(53317, announcement.Port);
        Assert.True(announcement.Download);
    }

    [Fact]
    public void Announcement_ToDeviceInfo_DropsAnnounce()
    {
        const string json = """{"alias":"Test Desktop","version":"2.2","deviceModel":"Windows","deviceType":"desktop","fingerprint":"0123456789ABCDEF","port":53317,"protocol":"https","download":false,"announce":true}""";
        Announcement? announcement = JsonSerializer.Deserialize<Announcement>(json, LocalSendJson.Options);
        Assert.NotNull(announcement);

        DeviceInfo info = announcement.ToDeviceInfo();

        Assert.Equal(new DeviceInfo("Test Desktop", "2.2", "Windows", "desktop", "0123456789ABCDEF", 53317, "https", false), info);
        Assert.DoesNotContain("announce", JsonSerializer.Serialize(info, LocalSendJson.Options));
    }

    [Fact]
    public void Serialize_UsesCamelCase_AndOmitsNulls()
    {
        var file = new FileDto(Id: "file-1", FileName: "photo.jpg", Size: 1234, FileType: "image/jpeg", Sha256: null, Preview: null, Metadata: null);

        string json = JsonSerializer.Serialize(file, LocalSendJson.Options);

        Assert.Contains("\"fileName\":\"photo.jpg\"", json);
        Assert.DoesNotContain("sha256", json);
        Assert.DoesNotContain("preview", json);
        Assert.DoesNotContain("metadata", json);
    }

    [Fact]
    public void Serialize_PrepareUploadResponse_MatchesReadmeShape()
    {
        var response = new PrepareUploadResponse("mySessionId", new Dictionary<string, string> { ["someFileId"] = "someFileToken" });

        string json = JsonSerializer.Serialize(response, LocalSendJson.Options);

        Assert.Equal("{\"sessionId\":\"mySessionId\",\"files\":{\"someFileId\":\"someFileToken\"}}", json);
    }

    [Fact]
    public void FileDto_SizeAboveInt32_RoundTrips()
    {
        var file = new FileDto("file-1", "movie.mp4", 5_000_000_000, "video/mp4", null, null, null);

        string json = JsonSerializer.Serialize(file, LocalSendJson.Options);
        FileDto? back = JsonSerializer.Deserialize<FileDto>(json, LocalSendJson.Options);

        Assert.Contains("5000000000", json);
        Assert.NotNull(back);
        Assert.Equal(5_000_000_000L, back.Size);
        Assert.Equal(file, back);
    }

    [Fact]
    public void LocalSendJson_Options_AreReadOnly()
    {
        Assert.True(LocalSendJson.Options.IsReadOnly);
    }

    [Theory]
    [InlineData("a.jpg", "image/jpeg")]
    [InlineData("a.jpeg", "image/jpeg")]
    [InlineData("PHOTO.JPG", "image/jpeg")]
    [InlineData("a.png", "image/png")]
    [InlineData("a.gif", "image/gif")]
    [InlineData("a.webp", "image/webp")]
    [InlineData("a.heic", "image/heic")]
    [InlineData("a.bmp", "image/bmp")]
    [InlineData("a.mp4", "video/mp4")]
    [InlineData("a.mov", "video/quicktime")]
    [InlineData("a.mkv", "video/x-matroska")]
    [InlineData("a.avi", "video/x-msvideo")]
    [InlineData("a.webm", "video/webm")]
    [InlineData("a.txt", "text/plain")]
    [InlineData("a.md", "text/markdown")]
    [InlineData("a.csv", "text/csv")]
    [InlineData("a.json", "application/json")]
    [InlineData("a.log", "text/plain")]
    [InlineData("a.pdf", "application/pdf")]
    [InlineData("a.apk", "application/vnd.android.package-archive")]
    [InlineData("a.zip", "application/zip")]
    [InlineData("a.exe", "application/octet-stream")]
    [InlineData("README", "application/octet-stream")]
    [InlineData(@"C:\photos.v2\holiday", "application/octet-stream")]
    public void FileTypes_FromPath(string path, string expectedMimeType)
    {
        Assert.Equal(expectedMimeType, FileTypes.FromPath(path));
    }
}
