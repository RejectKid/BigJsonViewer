using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace BigJsonViewer.App;

internal static class JsonDisplayFormatter
{
    private const int MaximumEmbeddedDepth = 3;
    private static readonly JavaScriptEncoder Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

    public static string Format(byte[] utf8Json)
    {
        using var document = JsonDocument.Parse(utf8Json);
        var output = new StringBuilder(Math.Min(utf8Json.Length * 2, 256 * 1024));
        WriteElement(output, document.RootElement, 0, 0);
        return output.ToString();
    }

    private static void WriteElement(StringBuilder output, JsonElement element, int indent, int embeddedDepth)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                WriteObject(output, element, indent, embeddedDepth);
                break;
            case JsonValueKind.Array:
                WriteArray(output, element, indent, embeddedDepth);
                break;
            case JsonValueKind.String:
                WriteString(output, element.GetString() ?? string.Empty, indent, embeddedDepth);
                break;
            case JsonValueKind.Number:
                output.Append(element.GetRawText());
                break;
            case JsonValueKind.True:
                output.Append("true");
                break;
            case JsonValueKind.False:
                output.Append("false");
                break;
            default:
                output.Append("null");
                break;
        }
    }

    private static void WriteObject(StringBuilder output, JsonElement element, int indent, int embeddedDepth)
    {
        output.Append('{');
        var properties = element.EnumerateObject().ToArray();
        if (properties.Length > 0)
        {
            output.AppendLine();
            for (var index = 0; index < properties.Length; index++)
            {
                var property = properties[index];
                AppendIndent(output, indent + 1);
                AppendQuoted(output, property.Name);
                output.Append(": ");
                WriteElement(output, property.Value, indent + 1, embeddedDepth);
                if (index + 1 < properties.Length)
                {
                    output.Append(',');
                }

                output.AppendLine();
            }

            AppendIndent(output, indent);
        }

        output.Append('}');
    }

    private static void WriteArray(StringBuilder output, JsonElement element, int indent, int embeddedDepth)
    {
        output.Append('[');
        var items = element.EnumerateArray().ToArray();
        if (items.Length > 0)
        {
            output.AppendLine();
            for (var index = 0; index < items.Length; index++)
            {
                AppendIndent(output, indent + 1);
                WriteElement(output, items[index], indent + 1, embeddedDepth);
                if (index + 1 < items.Length)
                {
                    output.Append(',');
                }

                output.AppendLine();
            }

            AppendIndent(output, indent);
        }

        output.Append(']');
    }

    private static void WriteString(StringBuilder output, string value, int indent, int embeddedDepth)
    {
        if (embeddedDepth < MaximumEmbeddedDepth && TryParseEmbeddedJson(value, out var embedded))
        {
            using (embedded)
            {
                WriteElement(output, embedded.RootElement, indent, embeddedDepth + 1);
            }

            output.Append("  /* decoded embedded JSON string */");
            return;
        }

        AppendQuoted(output, value);
    }

    private static bool TryParseEmbeddedJson(string value, out JsonDocument document)
    {
        document = null!;
        var trimmed = value.AsSpan().Trim();
        if (trimmed.Length < 2 ||
            (trimmed[0] == '{' && trimmed[^1] != '}') ||
            (trimmed[0] == '[' && trimmed[^1] != ']') ||
            (trimmed[0] is not ('{' or '[')))
        {
            return false;
        }

        try
        {
            document = JsonDocument.Parse(value);
            return document.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void AppendQuoted(StringBuilder output, string value)
    {
        output.Append('"');
        output.Append(JsonEncodedText.Encode(value, Encoder).ToString());
        output.Append('"');
    }

    private static void AppendIndent(StringBuilder output, int indent) => output.Append(' ', indent * 2);
}
