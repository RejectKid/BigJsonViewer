using System.Text;
using BenchmarkDotNet.Attributes;

namespace BigJsonViewer.Benchmarks;

[Config(typeof(BenchmarkConfig))]
[MemoryDiagnoser]
public class Utf8DecodeBenchmarks
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private BenchmarkCorpusLease _corpus = null!;
    private byte[] _data = null!;
    private char[] _characters = null!;

    [GlobalSetup]
    public void Setup()
    {
        _corpus = BenchmarkCorpusLease.Create();
        _data = _corpus.ReadPrefix();
        _characters = GC.AllocateUninitializedArray<char>(_data.Length);
    }

    [GlobalCleanup]
    public void Cleanup() => _corpus.Dispose();

    [Benchmark(Baseline = true)]
    public int ValidateAndCountCharacters() => StrictUtf8.GetCharCount(_data);

    [Benchmark]
    public int DecodeIntoReusableBuffer() => StrictUtf8.GetChars(_data, _characters);
}
