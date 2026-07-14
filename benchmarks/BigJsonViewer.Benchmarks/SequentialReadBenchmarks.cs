using System.IO.MemoryMappedFiles;
using BenchmarkDotNet.Attributes;

namespace BigJsonViewer.Benchmarks;

[Config(typeof(BenchmarkConfig))]
[MemoryDiagnoser]
public class SequentialReadBenchmarks
{
    private BenchmarkCorpusLease _corpus = null!;
    private byte[] _buffer = null!;

    [Params(64 * 1024, 1024 * 1024, 8 * 1024 * 1024)]
    public int BufferSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _corpus = BenchmarkCorpusLease.Create();
        _buffer = GC.AllocateUninitializedArray<byte>(BufferSize);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        BenchmarkProcessMetrics.Record(nameof(SequentialReadBenchmarks));
        _corpus.Dispose();
    }

    [Benchmark(Baseline = true)]
    public long FileStreamSequential()
    {
        using var stream = new FileStream(
            _corpus.Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.SequentialScan);
        return ReadStream(stream);
    }

    [Benchmark]
    public long PositionalSequential()
    {
        using var handle = File.OpenHandle(
            _corpus.Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            FileOptions.SequentialScan);
        long offset = 0;
        while (true)
        {
            var read = RandomAccess.Read(handle, _buffer, offset);
            if (read == 0)
            {
                return offset;
            }

            offset += read;
        }
    }

    [Benchmark]
    public long MemoryMappedSequential()
    {
        using var mapping = MemoryMappedFile.CreateFromFile(
            _corpus.Path,
            FileMode.Open,
            mapName: null,
            capacity: 0,
            MemoryMappedFileAccess.Read);
        using var stream = mapping.CreateViewStream(0, 0, MemoryMappedFileAccess.Read);
        return ReadStream(stream);
    }

    private long ReadStream(Stream stream)
    {
        long total = 0;
        while (true)
        {
            var read = stream.Read(_buffer);
            if (read == 0)
            {
                return total;
            }

            total += read;
        }
    }
}
