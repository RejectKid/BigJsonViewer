using System.Diagnostics;
using System.Text.Json;

namespace BigJsonViewer.Benchmarks;

internal static class BenchmarkProcessMetrics
{
    public static void Record(string benchmark)
    {
        var directory = Environment.GetEnvironmentVariable(BenchmarkEnvironment.MetricsDirectoryVariable);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        using var process = Process.GetCurrentProcess();
        var metrics = new
        {
            benchmark,
            processId = Environment.ProcessId,
            capturedAtUtc = DateTimeOffset.UtcNow,
            peakWorkingSetBytes = process.PeakWorkingSet64,
            workingSetBytes = process.WorkingSet64,
            managedHeapBytes = GC.GetTotalMemory(forceFullCollection: false)
        };
        var path = Path.Combine(directory, $"{benchmark}-{Environment.ProcessId}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(metrics, new JsonSerializerOptions { WriteIndented = true }));
    }
}
