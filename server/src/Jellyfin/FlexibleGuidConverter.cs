using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pickyfin.Server.Jellyfin;

/// <summary>Jellyfin writes ids without dashes ("N" format); .NET only reads the dashed form by default.</summary>
public sealed class FlexibleGuidConverter : JsonConverter<Guid>
{
    public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Guid.TryParse(reader.GetString(), out var id) ? id : Guid.Empty;

    public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString("N"));
}
