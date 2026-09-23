# System.Text.Json and TimeSpan: Serialization Gotcha

## Context

`System.Text.Json` (the built-in .NET JSON library) does **not** natively support
round-tripping `TimeSpan` as a human-readable string. This caused `ScheduledTime` in
`ScheduleSettings` to be silently lost or corrupted between application sessions.

## The Problem

Without a custom converter, `System.Text.Json` serializes `TimeSpan` as a JSON object:

```json
{
  "Ticks": 72000000000,
  "Days": 0,
  "Hours": 20,
  "Minutes": 0,
  "Seconds": 0,
  "Milliseconds": 0,
  "Microseconds": 0,
  "Nanoseconds": 0,
  "TotalDays": 0.8333333333333334,
  "TotalHours": 20.0,
  ...
}
```

When reading back, deserializing this object form could fail silently with
`JsonSerializer.Deserialize<T>`, returning `new T()` (default values) instead of the
persisted schedule — effectively resetting `ScheduledTime` to the default `18:00:00`
on every app restart.

## Secondary Bug: 12-Hour Format Specifier

When implementing the fix, a secondary bug was introduced and caught during code audit:

```csharp
// BUG — 'hh' is 12-hour clock
value.ToString(@"hh\:mm\:ss")

// FIX — 'HH' is 24-hour clock
value.ToString(@"HH\:mm\:ss")
```

Using `hh` would serialize `20:00:00` as `"08:00:00"`, causing the schedule
to silently reset to an incorrect PM hour. Always use `HH` for 24-hour time in
.NET format strings.

## The Solution

Implement a `JsonConverter<TimeSpan>` that:
1. **Writes** `TimeSpan` as an `"HH:mm:ss"` string (readable, unambiguous).
2. **Reads** both the string form `"HH:mm:ss"` *and* the legacy object form
   (via `Ticks`) for backward compatibility with data persisted before the fix.

```csharp
public sealed class TimeSpanJsonConverter : JsonConverter<TimeSpan>
{
    public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var raw = reader.GetString();
            if (!string.IsNullOrWhiteSpace(raw) && TimeSpan.TryParse(raw, out var parsed))
                return parsed;
        }
        else if (reader.TokenType == JsonTokenType.StartObject)
        {
            // Backward-compatible fallback for legacy object-format JSON.
            long ticks = 0;
            using var doc = JsonDocument.ParseValue(ref reader);
            if (doc.RootElement.TryGetProperty("Ticks", out var ticksProp))
                ticks = ticksProp.GetInt64();
            return new TimeSpan(ticks);
        }
        return TimeSpan.Zero;
    }

    public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString(@"HH\:mm\:ss"));
    }
}
```

Register it via shared `JsonSerializerOptions`:

```csharp
private static readonly JsonSerializerOptions SerializerOptions = new()
{
    Converters = { new TimeSpanJsonConverter() }
};

// Use in all Serialize/Deserialize calls:
JsonSerializer.Serialize(value, SerializerOptions);
JsonSerializer.Deserialize<T>(json, SerializerOptions);
```

## Key Takeaways

- **Never assume** `System.Text.Json` can round-trip all .NET types as strings.
  `TimeSpan`, `DateOnly`, `Version`, and custom structs often need explicit converters.
- **Always use `HH` (not `hh`)** for 24-hour time in .NET `ToString` format strings.
- Register converters on a **static, shared `JsonSerializerOptions`** instance — never
  instantiate a new one per call, as it is expensive and bypasses caching.
- Implement **backward-compatible deserialization** when fixing serialization format bugs
  to avoid breaking data persisted by older app versions.

## Files Changed

| File | Change |
|---|---|
| `GoogleDriveWorkSync/Helpers/TimeSpanJsonConverter.cs` | New file — converter implementation |
| `GoogleDriveWorkSync/Helpers/LocalSettingsHelper.cs` | Registers converter in `SerializerOptions` |
