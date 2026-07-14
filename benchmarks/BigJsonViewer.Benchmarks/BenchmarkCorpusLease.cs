using BigJsonViewer.CorpusGenerator;
using Generator = BigJsonViewer.CorpusGenerator.CorpusGenerator;

namespace BigJsonViewer.Benchmarks;

internal sealed class BenchmarkCorpusLease : IDisposable
{
    private readonly string? _ownedDirectory;

    private BenchmarkCorpusLease(string path, string? ownedDirectory)
    {
        Path = path;
        _ownedDirectory = ownedDirectory;
    }

    public string Path { get; }

    public long Length => new FileInfo(Path).Length;

    public static BenchmarkCorpusLease Create()
    {
        var configured = Environment.GetEnvironmentVariable(BenchmarkEnvironment.FileVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var path = System.IO.Path.GetFullPath(configured);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"Benchmark corpus from {BenchmarkEnvironment.FileVariable} does not exist.", path);
            }

            return new BenchmarkCorpusLease(path, ownedDirectory: null);
        }

        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"BigJsonViewer-Benchmarks-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var output = System.IO.Path.Combine(directory, "wide-array.json");
        Generator.Generate(new CorpusOptions
        {
            OutputPath = output,
            Scenario = CorpusScenario.WideArray,
            TargetBytes = BenchmarkEnvironment.GeneratedFileBytes,
            WriteManifest = false
        });

        return new BenchmarkCorpusLease(output, directory);
    }

    public byte[] ReadPrefix()
    {
        var length = (int)Math.Min(Length, BenchmarkEnvironment.InMemoryBytes);
        var buffer = GC.AllocateUninitializedArray<byte>(length);
        using var handle = File.OpenHandle(Path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        var read = 0;
        while (read < buffer.Length)
        {
            var count = RandomAccess.Read(handle, buffer.AsSpan(read), read);
            if (count == 0)
            {
                break;
            }

            read += count;
        }

        return read == buffer.Length ? buffer : buffer[..read];
    }

    public void Dispose()
    {
        if (_ownedDirectory is not null && Directory.Exists(_ownedDirectory))
        {
            Directory.Delete(_ownedDirectory, recursive: true);
        }
    }
}
