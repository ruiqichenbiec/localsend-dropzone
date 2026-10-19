using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Dropzone.Core.Protocol;

/// <summary>
/// The one JSON configuration for LocalSend v2 bodies: camelCase names, case-insensitive reads, unknown
/// properties skipped (the default), null properties omitted on write.
/// </summary>
public static class LocalSendJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            // MakeReadOnly() throws unless a resolver is set. Reflection-based metadata; no source generator.
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.MakeReadOnly();
        return options;
    }
}
