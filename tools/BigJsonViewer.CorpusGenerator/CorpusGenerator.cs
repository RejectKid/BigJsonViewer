using System.Diagnostics;

namespace BigJsonViewer.CorpusGenerator;

public static class CorpusGenerator
{
    private const int MaximumRecordBytes = 256;

    public static GenerationResult Generate(CorpusOptions options, CancellationToken cancellationToken = default)
    {
        Validate(options);
        var outputPath = Path.GetFullPath(options.OutputPath);
        var directory = Path.GetDirectoryName(outputPath)!;
        Directory.CreateDirectory(directory);
        if (File.Exists(outputPath) && !options.Overwrite)
        {
            throw new IOException($"Output already exists: {outputPath}. Pass --force to replace it.");
        }

        var partialPath = outputPath + ".partial";
        File.Delete(partialPath);
        var stopwatch = Stopwatch.StartNew();
        IReadOnlyList<long> markerOffsets;
        long markerCount;
        long actualBytes;

        try
        {
            using (var stream = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.SequentialScan))
            using (var writer = new PooledFileWriter(stream))
            {
                GenerateScenario(writer, options, cancellationToken);
                actualBytes = writer.Position;
                markerCount = writer.MarkerCount;
                markerOffsets = [.. writer.MarkerOffsets];
            }

            File.Move(partialPath, outputPath, options.Overwrite);
        }
        catch
        {
            File.Delete(partialPath);
            throw;
        }

        stopwatch.Stop();
        var result = new GenerationResult(
            outputPath,
            options.Scenario,
            options.TargetBytes,
            actualBytes,
            options.Seed,
            options.Marker,
            markerCount,
            markerOffsets,
            markerCount == markerOffsets.Count,
            stopwatch.Elapsed);

        if (options.WriteManifest)
        {
            ManifestWriter.Write(result);
        }

        return result;
    }

    private static void GenerateScenario(PooledFileWriter writer, CorpusOptions options, CancellationToken cancellationToken)
    {
        switch (options.Scenario)
        {
            case CorpusScenario.WideArray:
                GenerateWideArray(writer, options, cancellationToken, minified: false);
                break;
            case CorpusScenario.DeepObject:
                GenerateDeepObject(writer, options, cancellationToken);
                break;
            case CorpusScenario.JsonLines:
                GenerateJsonLines(writer, options, cancellationToken);
                break;
            case CorpusScenario.Minified:
                GenerateWideArray(writer, options, cancellationToken, minified: true);
                break;
            case CorpusScenario.LargeString:
                GenerateLargeString(writer, options, cancellationToken);
                break;
            case CorpusScenario.Whitespace:
                GenerateWhitespace(writer, options, cancellationToken);
                break;
            case CorpusScenario.EscapedStrings:
                GenerateEscapedStrings(writer, options, cancellationToken);
                break;
            case CorpusScenario.InvalidUtf8:
                GenerateInvalidUtf8(writer, options, cancellationToken);
                break;
            case CorpusScenario.Truncated:
                GenerateTruncated(writer, options, cancellationToken);
                break;
            case CorpusScenario.Malformed:
                GenerateMalformed(writer, options, cancellationToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(options), options.Scenario, "Unknown scenario.");
        }
    }

    private static void GenerateWideArray(PooledFileWriter writer, CorpusOptions options, CancellationToken cancellationToken, bool minified)
    {
        var random = new DeterministicRandom(options.Seed);
        writer.WriteByte((byte)'[');
        long index = 0;
        var recordReserve = (long)MaximumRecordBytes + options.Marker.Length;
        while (minified || writer.Position + recordReserve + 1 < options.TargetBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (index > 0)
            {
                writer.WriteAscii(minified ? "," : ",\n");
            }

            WriteObjectRecord(writer, options, ref random, index);
            index++;
            if (minified && writer.Position >= options.TargetBytes - 1)
            {
                break;
            }
        }

        if (!minified)
        {
            writer.WriteRepeated((byte)' ', options.TargetBytes - writer.Position - 1);
        }

        writer.WriteByte((byte)']');
    }

    private static void WriteObjectRecord(PooledFileWriter writer, CorpusOptions options, ref DeterministicRandom random, long index)
    {
        writer.WriteAscii("{\"id\":");
        writer.WriteInt64(index);
        writer.WriteAscii(",\"name\":\"item-");
        writer.WriteInt64(index);
        if (index % options.MarkerEvery == 0)
        {
            writer.WriteByte((byte)'-');
            writer.WriteMarker(options.Marker);
        }

        writer.WriteAscii("\",\"active\":");
        writer.WriteAscii((index & 1) == 0 ? "true" : "false");
        writer.WriteAscii(",\"value\":");
        writer.WriteUInt64(random.NextUInt64() % 1_000_000);
        writer.WriteByte((byte)'}');
    }

    private static void GenerateDeepObject(PooledFileWriter writer, CorpusOptions options, CancellationToken cancellationToken)
    {
        var depthBudget = Math.Max(1, (options.TargetBytes - options.Marker.Length - 64) / 24);
        var depth = Math.Min(options.Depth, (int)Math.Min(int.MaxValue, depthBudget));
        for (var level = 0; level < depth; level++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.WriteAscii("{\"level\":");
            writer.WriteInt64(level);
            writer.WriteAscii(",\"child\":");
        }

        writer.WriteAscii("{\"marker\":\"");
        writer.WriteMarker(options.Marker);
        writer.WriteAscii("\",\"values\":[0");
        var closingBytes = depth + 2L;
        long value = 1;
        while (writer.Position + 32 + closingBytes < options.TargetBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.WriteByte((byte)',');
            writer.WriteInt64(value++);
        }

        writer.WriteRepeated((byte)' ', options.TargetBytes - writer.Position - closingBytes, cancellationToken);
        writer.WriteAscii("]}");
        writer.WriteRepeated((byte)'}', depth, cancellationToken);
    }

    private static void GenerateJsonLines(PooledFileWriter writer, CorpusOptions options, CancellationToken cancellationToken)
    {
        var random = new DeterministicRandom(options.Seed);
        long index = 0;
        var recordReserve = (long)MaximumRecordBytes + options.Marker.Length;
        while (writer.Position + recordReserve < options.TargetBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteObjectRecord(writer, options, ref random, index++);
            writer.WriteByte((byte)'\n');
        }

        writer.WriteRepeated((byte)' ', options.TargetBytes - writer.Position);
    }

    private static void GenerateLargeString(PooledFileWriter writer, CorpusOptions options, CancellationToken cancellationToken)
    {
        const string prefix = "{\"payload\":\"";
        const string suffix = "\"}";
        writer.WriteAscii(prefix);
        var contentBytes = options.TargetBytes - prefix.Length - suffix.Length;
        var beforeMarker = (contentBytes - options.Marker.Length) / 2;
        writer.WriteRepeated((byte)'a', beforeMarker, cancellationToken);
        writer.WriteMarker(options.Marker);
        writer.WriteRepeated((byte)'z', contentBytes - beforeMarker - options.Marker.Length, cancellationToken);
        writer.WriteAscii(suffix);
    }

    private static void GenerateWhitespace(PooledFileWriter writer, CorpusOptions options, CancellationToken cancellationToken)
    {
        writer.WriteByte((byte)'[');
        writer.WriteRepeated((byte)' ', options.TargetBytes - 3, cancellationToken);
        writer.WriteAscii("0]");
    }

    private static void GenerateEscapedStrings(PooledFileWriter writer, CorpusOptions options, CancellationToken cancellationToken)
    {
        writer.WriteByte((byte)'[');
        long index = 0;
        var recordReserve = (long)MaximumRecordBytes + options.Marker.Length;
        while (writer.Position + recordReserve + 1 < options.TargetBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (index > 0)
            {
                writer.WriteByte((byte)',');
            }

            writer.WriteAscii("\"line\\nquote\\\"slash\\\\unicode\\u263A-");
            writer.WriteInt64(index);
            if (index % options.MarkerEvery == 0)
            {
                writer.WriteByte((byte)'-');
                writer.WriteMarker(options.Marker);
            }

            writer.WriteByte((byte)'\"');
            index++;
        }

        writer.WriteRepeated((byte)' ', options.TargetBytes - writer.Position - 1);
        writer.WriteByte((byte)']');
    }

    private static void GenerateInvalidUtf8(PooledFileWriter writer, CorpusOptions options, CancellationToken cancellationToken)
    {
        const string prefix = "{\"value\":\"";
        const string suffix = "\"}";
        writer.WriteAscii(prefix);
        writer.WriteMarker(options.Marker);
        writer.WriteByte(0xFF);
        writer.WriteRepeated((byte)'x', options.TargetBytes - writer.Position - suffix.Length, cancellationToken);
        writer.WriteAscii(suffix);
    }

    private static void GenerateTruncated(PooledFileWriter writer, CorpusOptions options, CancellationToken cancellationToken)
    {
        writer.WriteAscii("{\"items\":[{\"value\":\"");
        writer.WriteMarker(options.Marker);
        writer.WriteRepeated((byte)'x', options.TargetBytes - writer.Position, cancellationToken);
    }

    private static void GenerateMalformed(PooledFileWriter writer, CorpusOptions options, CancellationToken cancellationToken)
    {
        const string prefix = "{\"valid\":true,\"broken\":[1,2,,3],\"padding\":\"";
        const string suffix = "\"}";
        writer.WriteAscii(prefix);
        writer.WriteMarker(options.Marker);
        writer.WriteRepeated((byte)'x', options.TargetBytes - writer.Position - suffix.Length, cancellationToken);
        writer.WriteAscii(suffix);
    }

    private static void Validate(CorpusOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.OutputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Marker);
        if (!options.Marker.All(char.IsAscii) || options.Marker.Contains('"') || options.Marker.Contains('\\'))
        {
            throw new ArgumentException("Marker must be unescaped ASCII without quotes or backslashes.", nameof(options));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(options.TargetBytes, 512);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Depth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MarkerEvery, 1);
        if ((long)options.Marker.Length + 64 > options.TargetBytes)
        {
            throw new ArgumentException("Target size is too small for the selected marker.", nameof(options));
        }
    }
}
