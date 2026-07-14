using System.IO.MemoryMappedFiles;
using Microsoft.Win32.SafeHandles;
using BenchmarkDotNet.Attributes;
using BigJsonViewer.Storage;

namespace BigJsonViewer.Benchmarks;

[Config(typeof(BenchmarkConfig))]
[MemoryDiagnoser]
public class RandomAccessBenchmarks
{
    private const int ReadsPerInvocation = 256;
    private BenchmarkCorpusLease _corpus = null!;
    private SafeFileHandle _handle = null!;
    private MemoryMappedFile _mapping = null!;
    private MemoryMappedViewAccessor _accessor = null!;
    private RandomAccessWindowCache _cache = null!;
    private byte[] _buffer = null!;
    private long[] _offsets = null!;
    private long[] _warmOffsets = null!;

    [Params(4 * 1024, 64 * 1024, 1024 * 1024)]
    public int WindowSize { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _corpus = BenchmarkCorpusLease.Create();
        if (_corpus.Length < WindowSize)
        {
            throw new InvalidOperationException("Benchmark corpus is smaller than the requested random-access window.");
        }

        _handle = File.OpenHandle(_corpus.Path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        _mapping = MemoryMappedFile.CreateFromFile(_corpus.Path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        _accessor = _mapping.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        _cache = new RandomAccessWindowCache(
            new FileSource(_corpus.Path),
            new RandomAccessWindowCacheOptions(
                RandomAccessWindowCacheOptions.DefaultWindowSize,
                8L * RandomAccessWindowCacheOptions.DefaultWindowSize));
        _buffer = GC.AllocateUninitializedArray<byte>(WindowSize);
        _offsets = CreateOffsets(_corpus.Length - WindowSize, ReadsPerInvocation);
        var warmRange = Math.Min(
            _corpus.Length - WindowSize,
            4L * RandomAccessWindowCacheOptions.DefaultWindowSize);
        _warmOffsets = CreateOffsets(warmRange, ReadsPerInvocation);
        foreach (var offset in _warmOffsets)
        {
            await _cache.ReadAsync(offset, _buffer);
        }
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        BenchmarkProcessMetrics.Record(nameof(RandomAccessBenchmarks));
        await _cache.DisposeAsync();
        _accessor.Dispose();
        _mapping.Dispose();
        _handle.Dispose();
        _corpus.Dispose();
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = ReadsPerInvocation)]
    public long PositionalReads()
    {
        long total = 0;
        foreach (var offset in _offsets)
        {
            total += RandomAccess.Read(_handle, _buffer, offset);
        }

        return total;
    }

    [Benchmark(OperationsPerInvoke = ReadsPerInvocation)]
    public long MemoryMappedReads()
    {
        long total = 0;
        foreach (var offset in _offsets)
        {
            total += _accessor.ReadArray(offset, _buffer, 0, WindowSize);
        }

        return total;
    }

    [Benchmark(OperationsPerInvoke = ReadsPerInvocation)]
    public async Task<long> WindowCacheWarmHits()
    {
        long total = 0;
        foreach (var offset in _warmOffsets)
        {
            total += await _cache.ReadAsync(offset, _buffer);
        }

        return total;
    }

    [Benchmark(OperationsPerInvoke = ReadsPerInvocation)]
    public async Task<long> WindowCacheThrashingReads()
    {
        long total = 0;
        foreach (var offset in _offsets)
        {
            total += await _cache.ReadAsync(offset, _buffer);
        }

        return total;
    }

    private static long[] CreateOffsets(long maximumOffset, int count)
    {
        var offsets = new long[count];
        var range = (ulong)maximumOffset + 1;
        for (var index = 0; index < offsets.Length; index++)
        {
            offsets[index] = (long)(((ulong)index * 2_654_435_761UL) % range);
        }

        return offsets;
    }
}
