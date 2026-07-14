using BigJsonViewer.CorpusGenerator;

namespace BigJsonViewer.Benchmarks;

internal static class BenchmarkEnvironment
{
    public const string FileVariable = "BIGJSONVIEWER_BENCHMARK_FILE";
    public const string FileSizeVariable = "BIGJSONVIEWER_BENCHMARK_SIZE";
    public const string MemorySizeVariable = "BIGJSONVIEWER_BENCHMARK_MEMORY_SIZE";
    public const string MetricsDirectoryVariable = "BIGJSONVIEWER_BENCHMARK_METRICS_DIRECTORY";
    public const string CacheStateVariable = "BIGJSONVIEWER_BENCHMARK_CACHE_STATE";
    public const string StorageVariable = "BIGJSONVIEWER_BENCHMARK_STORAGE";
    public const string CommitVariable = "BIGJSONVIEWER_BENCHMARK_COMMIT";
    public const string WorktreeVariable = "BIGJSONVIEWER_BENCHMARK_WORKTREE";
    public const string PowerModeVariable = "BIGJSONVIEWER_BENCHMARK_POWER_MODE";
    public const string BackgroundActivityVariable = "BIGJSONVIEWER_BENCHMARK_BACKGROUND_ACTIVITY";

    public static long GeneratedFileBytes => ParseSize(FileSizeVariable, "64MiB");

    public static int InMemoryBytes
    {
        get
        {
            var bytes = ParseSize(MemorySizeVariable, "16MiB");
            if (bytes > int.MaxValue)
            {
                throw new InvalidOperationException($"{MemorySizeVariable} must not exceed {int.MaxValue} bytes.");
            }

            return (int)bytes;
        }
    }

    public static long CorpusBytes
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable(FileVariable);
            return !string.IsNullOrWhiteSpace(configured) && File.Exists(configured)
                ? new FileInfo(configured).Length
                : GeneratedFileBytes;
        }
    }

    internal static long ParseSize(string variable, string defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        return SizeParser.Parse(string.IsNullOrWhiteSpace(value) ? defaultValue : value);
    }
}
