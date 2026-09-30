using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Plainspoken.Core.Settings;

/// <summary>JSON (de)serialisation for settings, plus export/import without the local section.</summary>
public static class SettingsSerializer
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string Serialize(PlainspokenSettings settings) =>
        JsonSerializer.Serialize(settings, Options);

    /// <summary>Tolerant: unknown fields ignored, unknown enum values fall back to defaults.</summary>
    public static PlainspokenSettings Deserialize(string json)
    {
        var s = JsonSerializer.Deserialize<PlainspokenSettings>(json, Options) ?? new PlainspokenSettings();
        return s.Normalize();
    }

    /// <summary>Shareable JSON: everything except <c>local</c> (so never the API key).</summary>
    public static string Export(PlainspokenSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var node = JsonSerializer.SerializeToNode(settings, Options)!.AsObject();
        node.Remove("local");
        return node.ToJsonString(Options);
    }

    /// <summary>Applies an exported file on top of <paramref name="current"/>, keeping local values.</summary>
    public static PlainspokenSettings Import(string json, PlainspokenSettings current)
    {
        ArgumentNullException.ThrowIfNull(current);
        var node = JsonNode.Parse(json) as JsonObject ?? throw new JsonException("Settings file must be a JSON object.");
        node.Remove("local");
        var imported = node.Deserialize<PlainspokenSettings>(Options) ?? new PlainspokenSettings();
        imported.Local = Clone(current).Local;
        return imported.Normalize();
    }

    public static PlainspokenSettings Clone(PlainspokenSettings settings) => Deserialize(Serialize(settings));

    private static JsonSerializerOptions CreateOptions()
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            PropertyNameCaseInsensitive = true,
        };
        o.Converters.Add(new TolerantEnumConverterFactory());
        o.MakeReadOnly(populateMissingResolver: true);
        return o;
    }
}

/// <summary>Enums as camelCase strings; unknown values become the enum's default (first) member.</summary>
internal sealed class TolerantEnumConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(TolerantEnumConverter<>).MakeGenericType(typeToConvert))!;

    private sealed class TolerantEnumConverter<T> : JsonConverter<T>
        where T : struct, Enum
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String &&
                Enum.TryParse<T>(reader.GetString(), ignoreCase: true, out var value) &&
                Enum.IsDefined(value))
            {
                return value;
            }

            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            {
                reader.Skip();
            }

            return default;
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            var name = value.ToString();
            writer.WriteStringValue(char.ToLowerInvariant(name[0]) + name[1..]);
        }
    }
}
