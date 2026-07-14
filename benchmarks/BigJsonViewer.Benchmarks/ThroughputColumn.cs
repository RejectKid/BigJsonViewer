using System.Globalization;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

namespace BigJsonViewer.Benchmarks;

internal sealed class ThroughputColumn : IColumn
{
    public string Id => nameof(ThroughputColumn);

    public string ColumnName => "MiB/s";

    public string Legend => "Logical input throughput in mebibytes per second.";

    public ColumnCategory Category => ColumnCategory.Statistics;

    public int PriorityInCategory => 100;

    public bool IsNumeric => true;

    public UnitType UnitType => UnitType.Dimensionless;

    public bool AlwaysShow => true;

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase) =>
        GetValue(summary, benchmarkCase, summary.Style);

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style)
    {
        var meanNanoseconds = summary[benchmarkCase]?.ResultStatistics?.Mean;
        var bytes = GetLogicalBytes(benchmarkCase);
        if (meanNanoseconds is null || meanNanoseconds <= 0 || bytes is null)
        {
            return "-";
        }

        var mebibytesPerSecond = bytes.Value * 1_000_000_000d / meanNanoseconds.Value / (1024d * 1024d);
        return mebibytesPerSecond.ToString("N1", CultureInfo.InvariantCulture);
    }

    public bool IsAvailable(Summary summary) => true;

    public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;

    private static long? GetLogicalBytes(BenchmarkCase benchmarkCase)
    {
        var type = benchmarkCase.Descriptor.Type;
        if (type == typeof(SequentialReadBenchmarks))
        {
            return BenchmarkEnvironment.CorpusBytes;
        }

        if (type == typeof(RandomAccessBenchmarks))
        {
            return GetParameter(benchmarkCase, nameof(RandomAccessBenchmarks.WindowSize));
        }

        if (type == typeof(StructuralScanBenchmarks) || type == typeof(Utf8DecodeBenchmarks))
        {
            return Math.Min(BenchmarkEnvironment.CorpusBytes, BenchmarkEnvironment.InMemoryBytes);
        }

        if (type == typeof(IndexEncodingBenchmarks))
        {
            var count = GetParameter(benchmarkCase, nameof(IndexEncodingBenchmarks.OffsetCount));
            return count is null ? null : checked(count.Value * sizeof(long));
        }

        return null;
    }

    private static long? GetParameter(BenchmarkCase benchmarkCase, string name)
    {
        var parameter = benchmarkCase.Parameters.Items.FirstOrDefault(item => item.Name == name);
        return parameter?.Value is null ? null : Convert.ToInt64(parameter.Value, CultureInfo.InvariantCulture);
    }
}
