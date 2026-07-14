using BenchmarkDotNet.Attributes;
using BigJsonViewer.BenchmarkKernels;

namespace BigJsonViewer.Benchmarks;

[Config(typeof(BenchmarkConfig))]
[MemoryDiagnoser]
public class StructuralScanBenchmarks
{
    private BenchmarkCorpusLease _corpus = null!;
    private byte[] _data = null!;

    [GlobalSetup]
    public void Setup()
    {
        _corpus = BenchmarkCorpusLease.Create();
        _data = _corpus.ReadPrefix();
    }

    [GlobalCleanup]
    public void Cleanup() => _corpus.Dispose();

    [Benchmark(Baseline = true)]
    public StructuralScanResult Scalar() => StructuralScanner.ScanScalar(_data);

    [Benchmark]
    public StructuralScanResult SearchValues() => StructuralScanner.ScanSearchValues(_data);
}
