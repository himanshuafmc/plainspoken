using System.Text;
using System.Text.Json;

namespace Plainspoken.Core.Transcription;

/// <summary>
/// Describes the structure of a JSON document without its content, for logging unexpected
/// responses safely. Only short enum-like values of well-known keys (status, type, …) are kept.
/// Example: {id:str,status:"completed",steps:[1×{content:[1×{text:str(123),type:"text"}],type:"model_output"}]}
/// </summary>
public static class JsonShape
{
    private static readonly HashSet<string> SafeValueKeys = new(StringComparer.Ordinal)
    {
        "status", "type", "object", "state", "code", "finishReason", "blockReason", "mimeType", "mime_type", "reason",
    };

    public static string Describe(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "empty";
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var sb = new StringBuilder();
            Describe(doc.RootElement, null, sb, 0);
            return sb.Length > 1500 ? sb.ToString(0, 1500) + "…" : sb.ToString();
        }
        catch (JsonException)
        {
            return $"non-JSON ({body.Length} chars)";
        }
    }

    public static string Describe(JsonElement element)
    {
        var sb = new StringBuilder();
        Describe(element, null, sb, 0);
        return sb.ToString();
    }

    private static void Describe(JsonElement e, string? key, StringBuilder sb, int depth)
    {
        if (depth > 8)
        {
            sb.Append('…');
            return;
        }

        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                sb.Append('{');
                var first = true;
                foreach (var p in e.EnumerateObject())
                {
                    if (!first)
                    {
                        sb.Append(',');
                    }

                    first = false;
                    sb.Append(p.Name).Append(':');
                    Describe(p.Value, p.Name, sb, depth + 1);
                }

                sb.Append('}');
                break;
            case JsonValueKind.Array:
                var len = e.GetArrayLength();
                sb.Append('[').Append(len);
                if (len > 0)
                {
                    sb.Append('×');
                    Describe(e[0], null, sb, depth + 1);
                }

                sb.Append(']');
                break;
            case JsonValueKind.String:
                var s = e.GetString() ?? string.Empty;
                if (key is not null && SafeValueKeys.Contains(key) && s.Length <= 40)
                {
                    sb.Append('"').Append(s).Append('"');
                }
                else
                {
                    sb.Append("str(").Append(s.Length).Append(')');
                }

                break;
            case JsonValueKind.Number:
                sb.Append(key is "code" ? e.GetRawText() : "num");
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                sb.Append("bool");
                break;
            default:
                sb.Append("null");
                break;
        }
    }
}
