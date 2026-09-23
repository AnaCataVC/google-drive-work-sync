using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GoogleDriveWorkSync.Helpers;

/// <summary>
/// Converts TimeSpan to/from a "HH:mm:ss" string for System.Text.Json.
/// System.Text.Json does not natively support TimeSpan roundtrip as a string;
/// this converter guarantees stable serialization across app versions and updates.
/// </summary>
public sealed class TimeSpanJsonConverter : JsonConverter<TimeSpan>
{
    public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var raw = reader.GetString();
            if (!string.IsNullOrWhiteSpace(raw) && TimeSpan.TryParse(raw, out var parsed))
            {
                return parsed;
            }
        }
        else if (reader.TokenType == JsonTokenType.StartObject)
        {
            // Graceful fallback: if it was previously serialized as a complex object,
            // extract the "Ticks" property and reconstruct the TimeSpan.
            long ticks = 0;
            using var doc = JsonDocument.ParseValue(ref reader);
            if (doc.RootElement.TryGetProperty("Ticks", out var ticksProp))
            {
                ticks = ticksProp.GetInt64();
            }
            return new TimeSpan(ticks);
        }

        return TimeSpan.Zero;
    }

    public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options)
    {
        // Always serialize as "HH:mm:ss" for readability and stable deserialization.
        writer.WriteStringValue(value.ToString(@"HH\:mm\:ss"));
    }
}
