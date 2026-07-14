using System.Text.Json;

namespace BigJsonViewer.CorpusGenerator;

internal static class ManifestWriter
{
    public static void Write(GenerationResult result)
    {
        using var stream = new FileStream(result.OutputPath + ".manifest.json", FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteString("scenario", ScenarioName.ToKebabCase(result.Scenario));
        writer.WriteNumber("requestedBytes", result.RequestedBytes);
        writer.WriteNumber("actualBytes", result.ActualBytes);
        writer.WriteNumber("seed", result.Seed);
        writer.WriteString("marker", result.Marker);
        writer.WriteNumber("markerCount", result.MarkerCount);
        writer.WriteBoolean("markerOffsetsComplete", result.MarkerOffsetsComplete);
        writer.WriteStartArray("markerOffsets");
        foreach (var offset in result.MarkerOffsets)
        {
            writer.WriteNumberValue(offset);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}
