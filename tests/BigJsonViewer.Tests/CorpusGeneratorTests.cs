using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BigJsonViewer.CorpusGenerator;
using Generator = BigJsonViewer.CorpusGenerator.CorpusGenerator;

namespace BigJsonViewer.Tests;

public sealed class CorpusGeneratorTests
{
    public static TheoryData<CorpusScenario> ValidDocumentScenarios => new()
    {
        CorpusScenario.WideArray,
        CorpusScenario.DeepObject,
        CorpusScenario.Minified,
        CorpusScenario.LargeString,
        CorpusScenario.Whitespace,
        CorpusScenario.EscapedStrings
    };

    public static TheoryData<CorpusScenario> InvalidDocumentScenarios => new()
    {
        CorpusScenario.Truncated,
        CorpusScenario.Malformed
    };

    [Theory]
    [MemberData(nameof(ValidDocumentScenarios))]
    public void ValidScenariosProduceValidJson(CorpusScenario scenario)
    {
        using var directory = new TemporaryDirectory();
        var result = Generate(directory, scenario);
        var bytes = File.ReadAllBytes(result.OutputPath);

        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 128 });

        Assert.NotEqual(JsonValueKind.Undefined, document.RootElement.ValueKind);
        Assert.True(result.ActualBytes >= result.RequestedBytes);
        Assert.True(result.ActualBytes <= result.RequestedBytes + 256);
    }

    [Fact]
    public void JsonLinesProducesIndependentDocuments()
    {
        using var directory = new TemporaryDirectory();
        var result = Generate(directory, CorpusScenario.JsonLines, targetBytes: 8_192);
        var lines = File.ReadLines(result.OutputPath).Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();

        Assert.NotEmpty(lines);
        foreach (var line in lines)
        {
            using var document = JsonDocument.Parse(line);
            Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        }

        Assert.Equal(result.RequestedBytes, result.ActualBytes);
    }

    [Theory]
    [MemberData(nameof(InvalidDocumentScenarios))]
    public void InvalidScenariosAreRejectedBySystemTextJson(CorpusScenario scenario)
    {
        using var directory = new TemporaryDirectory();
        var result = Generate(directory, scenario);
        var bytes = File.ReadAllBytes(result.OutputPath);

        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(bytes));
    }

    [Fact]
    public void InvalidUtf8ScenarioIsRejectedByStrictUtf8Decoder()
    {
        using var directory = new TemporaryDirectory();
        var result = Generate(directory, CorpusScenario.InvalidUtf8);
        var bytes = File.ReadAllBytes(result.OutputPath);
        var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        Assert.Throws<DecoderFallbackException>(() => strictUtf8.GetString(bytes));
    }

    [Theory]
    [InlineData(CorpusScenario.WideArray)]
    [InlineData(CorpusScenario.DeepObject)]
    [InlineData(CorpusScenario.JsonLines)]
    [InlineData(CorpusScenario.LargeString)]
    [InlineData(CorpusScenario.Whitespace)]
    [InlineData(CorpusScenario.EscapedStrings)]
    [InlineData(CorpusScenario.InvalidUtf8)]
    [InlineData(CorpusScenario.Truncated)]
    [InlineData(CorpusScenario.Malformed)]
    public void FixedSizeScenariosWriteTheRequestedNumberOfBytes(CorpusScenario scenario)
    {
        using var directory = new TemporaryDirectory();
        var result = Generate(directory, scenario);

        Assert.Equal(result.RequestedBytes, result.ActualBytes);
        Assert.Equal(result.ActualBytes, new FileInfo(result.OutputPath).Length);
    }

    [Fact]
    public void MinifiedScenarioContainsNoWhitespaceOutsideStringData()
    {
        using var directory = new TemporaryDirectory();
        var result = Generate(directory, CorpusScenario.Minified);
        var bytes = File.ReadAllBytes(result.OutputPath);

        Assert.DoesNotContain(bytes, value => value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n');
    }

    [Fact]
    public void SameSeedProducesIdenticalBytes()
    {
        using var directory = new TemporaryDirectory();
        var first = Generate(directory, CorpusScenario.WideArray, fileName: "first.json", seed: 42);
        var second = Generate(directory, CorpusScenario.WideArray, fileName: "second.json", seed: 42);

        Assert.Equal(Hash(first.OutputPath), Hash(second.OutputPath));
    }

    [Fact]
    public void DifferentSeedsChangeGeneratedValues()
    {
        using var directory = new TemporaryDirectory();
        var first = Generate(directory, CorpusScenario.WideArray, fileName: "first.json", seed: 1);
        var second = Generate(directory, CorpusScenario.WideArray, fileName: "second.json", seed: 2);

        Assert.NotEqual(Hash(first.OutputPath), Hash(second.OutputPath));
    }

    [Fact]
    public void ManifestMarkerOffsetsPointAtMarkerBytes()
    {
        using var directory = new TemporaryDirectory();
        var result = Generate(directory, CorpusScenario.WideArray, targetBytes: 32_768);
        var bytes = File.ReadAllBytes(result.OutputPath);
        var marker = Encoding.ASCII.GetBytes(result.Marker);

        Assert.NotEmpty(result.MarkerOffsets);
        foreach (var offset in result.MarkerOffsets)
        {
            Assert.Equal(marker, bytes.AsSpan((int)offset, marker.Length).ToArray());
        }

        var manifestPath = result.OutputPath + ".manifest.json";
        Assert.True(File.Exists(manifestPath));
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        Assert.Equal(result.MarkerOffsets.Count, manifest.RootElement.GetProperty("markerOffsets").GetArrayLength());
    }

    [Fact]
    public void ExistingOutputRequiresExplicitOverwrite()
    {
        using var directory = new TemporaryDirectory();
        var result = Generate(directory, CorpusScenario.WideArray);
        var options = Options(result.OutputPath, CorpusScenario.WideArray);

        Assert.Throws<IOException>(() => Generator.Generate(options));
    }

    [Theory]
    [InlineData(CorpusScenario.WideArray)]
    [InlineData(CorpusScenario.LargeString)]
    [InlineData(CorpusScenario.Whitespace)]
    [InlineData(CorpusScenario.InvalidUtf8)]
    [InlineData(CorpusScenario.Truncated)]
    [InlineData(CorpusScenario.Malformed)]
    public void CancellationRemovesPartialOutput(CorpusScenario scenario)
    {
        using var directory = new TemporaryDirectory();
        var output = Path.Combine(directory.Path, "cancelled.json");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => Generator.Generate(Options(output, scenario), cancellation.Token));
        Assert.False(File.Exists(output));
        Assert.False(File.Exists(output + ".partial"));
    }

    private static GenerationResult Generate(
        TemporaryDirectory directory,
        CorpusScenario scenario,
        long targetBytes = 4_096,
        string fileName = "corpus.json",
        ulong seed = 20_260_714)
    {
        return Generator.Generate(Options(Path.Combine(directory.Path, fileName), scenario, targetBytes, seed));
    }

    private static CorpusOptions Options(string outputPath, CorpusScenario scenario, long targetBytes = 4_096, ulong seed = 20_260_714)
    {
        return new CorpusOptions
        {
            OutputPath = outputPath,
            Scenario = scenario,
            TargetBytes = targetBytes,
            Seed = seed,
            Depth = 8,
            MarkerEvery = 4,
            WriteManifest = true
        };
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"BigJsonViewer-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
