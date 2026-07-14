using BenchmarkDotNet.Attributes;
using BigJsonViewer.BenchmarkKernels;

namespace BigJsonViewer.Benchmarks;

[Config(typeof(BenchmarkConfig))]
[MemoryDiagnoser]
public class IndexEncodingBenchmarks
{
    private long[] _offsets = null!;
    private byte[] _fixedBuffer = null!;
    private byte[] _varintBuffer = null!;

    [Params(1_000, 100_000, 1_000_000)]
    public int OffsetCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _offsets = new long[OffsetCount];
        long offset = 0;
        for (var index = 0; index < _offsets.Length; index++)
        {
            offset += 2 + ((index * 17L) % 253);
            _offsets[index] = offset;
        }

        _fixedBuffer = GC.AllocateUninitializedArray<byte>(checked(OffsetCount * sizeof(long)));
        _varintBuffer = GC.AllocateUninitializedArray<byte>(checked(OffsetCount * 10));
    }

    [GlobalCleanup]
    public static void Cleanup() => BenchmarkProcessMetrics.Record(nameof(IndexEncodingBenchmarks));

    [Benchmark(Baseline = true)]
    public int Fixed64() => IndexEncoder.EncodeFixed64(_offsets, _fixedBuffer);

    [Benchmark]
    public int DeltaVarint() => IndexEncoder.EncodeDeltaVarint(_offsets, _varintBuffer);
}
