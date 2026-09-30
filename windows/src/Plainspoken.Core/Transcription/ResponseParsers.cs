using System.Text;
using System.Text.Json;

namespace Plainspoken.Core.Transcription;

/// <summary>What the Interactions API returned. <see cref="Text"/> is null when no text field was found at all.</summary>
public sealed record InteractionResult(string? Id, string? Status, string? Text, bool HasOutputContainer);

/// <summary>
/// Tolerant parsers for the two response formats. Verified shape (2026-09-29):
/// {"id":"…","status":"completed","steps":[{"type":"model_output","content":[{"type":"text","text":"…"}]}]}
/// </summary>
public static class ResponseParsers
{
    public static InteractionResult ParseInteraction(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return new InteractionResult(null, null, null, false);
        }

        var id = GetString(root, "id");
        var status = GetString(root, "status");

        // 1. SDK-style convenience field, in case the REST API adds it.
        foreach (var name in new[] { "output_text", "outputText" })
        {
            if (GetString(root, name) is { } direct)
            {
                return new InteractionResult(id, status, direct, true);
            }
        }

        var pieces = new List<string>();
        var found = false;
        var container = false;

        // 2. "outputs": [{"type":"text","text":"…"}]
        if (root.TryGetProperty("outputs", out var outputs) && outputs.ValueKind == JsonValueKind.Array)
        {
            container = true;
            found |= AppendTextItems(outputs, pieces);
        }

        // 3. "steps": [{"type":"model_output","content":[{"type":"text","text":"…"}]}]
        if (!found && root.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array)
        {
            container = true;
            foreach (var step in steps.EnumerateArray())
            {
                if (step.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var type = GetString(step, "type");
                if (type is not null && type != "model_output")
                {
                    continue;
                }

                foreach (var name in new[] { "content", "outputs" })
                {
                    if (step.TryGetProperty(name, out var content) && content.ValueKind == JsonValueKind.Array)
                    {
                        found |= AppendTextItems(content, pieces);
                    }
                }

                if (GetString(step, "text") is { } stepText)
                {
                    pieces.Add(stepText);
                    found = true;
                }
            }
        }

        // Several text items (e.g. one per spoken segment) may be trimmed, so join them with care.
        return new InteractionResult(id, status, found ? TranscriptJoiner.Join(pieces) : null, container);
    }

    /// <summary>Returns the transcript, "" when the model produced no text, or throws for blocked/odd shapes.</summary>
    public static string ParseGenerateContent(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw Unexpected(root);
        }

        if (root.TryGetProperty("promptFeedback", out var fb) && fb.ValueKind == JsonValueKind.Object &&
            GetString(fb, "blockReason") is { } block)
        {
            throw new TranscriptionException(TranscriptionErrorKind.Blocked, $"generateContent blocked: {block}");
        }

        if (!root.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array ||
            candidates.GetArrayLength() == 0)
        {
            throw Unexpected(root);
        }

        var candidate = candidates[0];
        var finish = GetString(candidate, "finishReason");
        var sb = new StringBuilder();
        if (candidate.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Object &&
            content.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in parts.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Object ||
                    (part.TryGetProperty("thought", out var thought) && thought.ValueKind == JsonValueKind.True))
                {
                    continue;
                }

                if (GetString(part, "text") is { } t)
                {
                    sb.Append(t);
                }
            }
        }

        if (sb.Length == 0 && finish is "SAFETY" or "RECITATION" or "BLOCKLIST" or "PROHIBITED_CONTENT" or "SPII")
        {
            throw new TranscriptionException(TranscriptionErrorKind.Blocked, $"generateContent finishReason {finish}");
        }

        return sb.ToString();
    }

    private static bool AppendTextItems(JsonElement array, List<string> pieces)
    {
        var found = false;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var type = GetString(item, "type");
            if ((type is null || type == "text") && GetString(item, "text") is { } text)
            {
                pieces.Add(text);
                found = true;
            }
        }

        return found;
    }

    private static TranscriptionException Unexpected(JsonElement root) =>
        new(TranscriptionErrorKind.UnexpectedResponse, "unexpected response shape: " + JsonShape.Describe(root));

    private static string? GetString(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
