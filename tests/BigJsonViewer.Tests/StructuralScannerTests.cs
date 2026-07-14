using System.Text;
using BigJsonViewer.BenchmarkKernels;

namespace BigJsonViewer.Tests;

public sealed class StructuralScannerTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"name\":\"value\",\"items\":[1,2,3]}")]
    [InlineData("{\"escaped\":\"quote: \\\" slash: \\\\ braces: {}[],:\"}")]
    [InlineData("[\"\\\\\\\"\",{\"nested\":[true,false,null]}]")]
    [InlineData("{\"unterminated\":\"value")]
    public void SearchValuesMatchesScalarReference(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);

        Assert.Equal(
            StructuralScanner.ScanScalar(bytes),
            StructuralScanner.ScanSearchValues(bytes));
    }

    [Fact]
    public void SearchValuesMatchesScalarForDeterministicCandidateNoise()
    {
        ReadOnlySpan<byte> alphabet = "abc123 \\\"{}[],:xyz"u8;
        var bytes = new byte[32_768];
        uint state = 0xC0FFEE;
        for (var index = 0; index < bytes.Length; index++)
        {
            state = (state * 1_664_525) + 1_013_904_223;
            bytes[index] = alphabet[(int)(state % (uint)alphabet.Length)];
        }

        Assert.Equal(
            StructuralScanner.ScanScalar(bytes),
            StructuralScanner.ScanSearchValues(bytes));
    }

    [Fact]
    public void StateCarriesAcrossEveryPossibleChunkBoundary()
    {
        var bytes = "{\"a\":\"escaped \\\" quote and slash \\\\ value\",\"b\":[1,2]}"u8.ToArray();
        var expected = StructuralScanner.ScanScalar(bytes);

        for (var split = 0; split <= bytes.Length; split++)
        {
            var first = StructuralScanner.ScanSearchValues(bytes.AsSpan(0, split));
            var second = StructuralScanner.ScanSearchValues(bytes.AsSpan(split), first.EndState);
            var combined = new StructuralScanResult(
                first.StructuralCount + second.StructuralCount,
                first.CompletedStringCount + second.CompletedStringCount,
                first.EscapeCount + second.EscapeCount,
                second.EndState);

            Assert.Equal(expected, combined);
        }
    }
}
