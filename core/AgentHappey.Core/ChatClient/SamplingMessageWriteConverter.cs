namespace AgentHappey.Core;

// TEMPORARY workaround due too MCP SDK bug MCP SDK/list converter    ❌ serializes via object, causes extra base64
// TODO remove
/*public sealed class SamplingMessageWriteConverter : JsonConverter<SamplingMessage>
{
    public override SamplingMessage? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => throw new NotSupportedException();

    public override void Write(Utf8JsonWriter writer, SamplingMessage value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        writer.WriteString("role", value.Role.ToString().ToLowerInvariant());

        writer.WritePropertyName("content");

        if (value.Content.Count == 1)
        {
            JsonSerializer.Serialize<ContentBlock>(writer, value.Content[0], options);
        }
        else
        {
            writer.WriteStartArray();
            foreach (var item in value.Content)
                JsonSerializer.Serialize<ContentBlock>(writer, item, options);
            writer.WriteEndArray();
        }

        if (value.Meta is not null)
        {
            writer.WritePropertyName("_meta");
            JsonSerializer.Serialize(writer, value.Meta, options);
        }

        writer.WriteEndObject();
    }
}

public static class SamplingHelper
{
    public static bool TryRepairImage(ImageContentBlock img, out ImageContentBlock repaired)
    {
        repaired = img;

        var maybeBase64Text = Encoding.UTF8.GetString(img.DecodedData.Span).Trim();

        if (!(maybeBase64Text.StartsWith("/9j/") ||
              maybeBase64Text.StartsWith("iVBOR") ||
              maybeBase64Text.StartsWith("UklGR") ||
              maybeBase64Text.StartsWith("R0lGOD")))
            return false;

        try
        {
            var raw = Convert.FromBase64String(maybeBase64Text);
            repaired = ImageContentBlock.FromBytes(raw, img.MimeType);
            return true;
        }
        catch
        {
            return false;
        }
    }


}*/