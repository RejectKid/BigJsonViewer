using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace BigJsonViewer.Benchmarks;

internal static class BenchmarkRunMetadataWriter
{
    public static string Prepare(string[] args)
    {
        var artifactsDirectory = GetArtifactsDirectory(args);
        Directory.CreateDirectory(artifactsDirectory);

        var metricsDirectory = Path.Combine(artifactsDirectory, "process-metrics");
        Environment.SetEnvironmentVariable(BenchmarkEnvironment.MetricsDirectoryVariable, metricsDirectory);

        var corpusPath = Environment.GetEnvironmentVariable(BenchmarkEnvironment.FileVariable);
        var drive = GetDrive(corpusPath ?? artifactsDirectory);
        var metadata = new
        {
            capturedAtUtc = DateTimeOffset.UtcNow,
            commit = GetValue(BenchmarkEnvironment.CommitVariable, "GITHUB_SHA"),
            worktree = GetValue(BenchmarkEnvironment.WorktreeVariable),
            command = Environment.CommandLine,
            operatingSystem = RuntimeInformation.OSDescription,
            processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            framework = RuntimeInformation.FrameworkDescription,
            processorCount = Environment.ProcessorCount,
            processorModel = GetProcessorModel(),
            availableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            cacheState = GetValue(BenchmarkEnvironment.CacheStateVariable),
            powerMode = GetValue(BenchmarkEnvironment.PowerModeVariable),
            backgroundActivity = GetValue(BenchmarkEnvironment.BackgroundActivityVariable),
            storage = new
            {
                label = GetValue(BenchmarkEnvironment.StorageVariable),
                name = drive?.Name,
                driveType = drive?.DriveType.ToString(),
                driveFormat = drive?.DriveFormat,
                totalSize = drive?.TotalSize,
                availableFreeSpace = drive?.AvailableFreeSpace
            },
            corpus = new
            {
                path = corpusPath,
                bytes = BenchmarkEnvironment.CorpusBytes,
                generated = string.IsNullOrWhiteSpace(corpusPath),
                inMemoryPrefixBytes = BenchmarkEnvironment.InMemoryBytes
            }
        };

        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(
            Path.Combine(artifactsDirectory, "environment.json"),
            JsonSerializer.Serialize(metadata, options));
        File.WriteAllText(
            Path.Combine(artifactsDirectory, "environment.md"),
            CreateMarkdown(metadata));
        return artifactsDirectory;
    }

    private static string GetArtifactsDirectory(string[] args)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], "--artifacts", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFullPath(args[index + 1]);
            }
        }

        return Path.GetFullPath("BenchmarkDotNet.Artifacts");
    }

    private static DriveInfo? GetDrive(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return DriveInfo.GetDrives()
            .Where(drive => drive.IsReady)
            .OrderByDescending(drive => drive.RootDirectory.FullName.Length)
            .FirstOrDefault(drive => fullPath.StartsWith(drive.RootDirectory.FullName, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetProcessorModel()
    {
        var windowsModel = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
        if (!string.IsNullOrWhiteSpace(windowsModel))
        {
            return windowsModel;
        }

        if (OperatingSystem.IsLinux() && File.Exists("/proc/cpuinfo"))
        {
            var model = File.ReadLines("/proc/cpuinfo")
                .FirstOrDefault(line => line.StartsWith("model name", StringComparison.OrdinalIgnoreCase));
            if (model is not null)
            {
                return model[(model.IndexOf(':') + 1)..].Trim();
            }
        }

        if (OperatingSystem.IsMacOS())
        {
            using var process = Process.Start(new ProcessStartInfo("/usr/sbin/sysctl", "-n machdep.cpu.brand_string")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false
            });
            var model = process?.StandardOutput.ReadToEnd().Trim();
            process?.WaitForExit();
            if (!string.IsNullOrWhiteSpace(model))
            {
                return model;
            }
        }

        return "unknown";
    }

    private static string GetValue(string variable, string? fallbackVariable = null)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(value) && fallbackVariable is not null)
        {
            value = Environment.GetEnvironmentVariable(fallbackVariable);
        }

        return string.IsNullOrWhiteSpace(value) ? "not-provided" : value;
    }

    private static string CreateMarkdown(object metadata)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(metadata));
        var root = document.RootElement;
        var storage = root.GetProperty("storage");
        var corpus = root.GetProperty("corpus");
        var builder = new StringBuilder()
            .AppendLine("# Benchmark environment")
            .AppendLine()
            .AppendLine($"- Captured: {root.GetProperty("capturedAtUtc")}")
            .AppendLine($"- Commit: `{root.GetProperty("commit").GetString()}`")
            .AppendLine($"- Working tree: {root.GetProperty("worktree").GetString()}")
            .AppendLine($"- OS: {root.GetProperty("operatingSystem").GetString()}")
            .AppendLine($"- Runtime: {root.GetProperty("framework").GetString()}")
            .AppendLine($"- CPU: {root.GetProperty("processorModel").GetString()} ({root.GetProperty("processorCount")} logical processors)")
            .AppendLine($"- Available memory: {root.GetProperty("availableMemoryBytes")} bytes")
            .AppendLine($"- Storage: {storage.GetProperty("label").GetString()}, {storage.GetProperty("driveType")}, {storage.GetProperty("driveFormat")}")
            .AppendLine($"- Cache state: {root.GetProperty("cacheState").GetString()}")
            .AppendLine($"- Power mode: {root.GetProperty("powerMode").GetString()}")
            .AppendLine($"- Background activity: {root.GetProperty("backgroundActivity").GetString()}")
            .AppendLine($"- Corpus: {corpus.GetProperty("bytes")} bytes; in-memory prefix {corpus.GetProperty("inMemoryPrefixBytes")} bytes")
            .AppendLine()
            .AppendLine("BenchmarkDotNet reports are in the `results` directory. Per-process peak working-set snapshots are in `process-metrics`.");
        return builder.ToString();
    }
}
