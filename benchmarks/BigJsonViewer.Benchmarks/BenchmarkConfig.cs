using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters.Json;

namespace BigJsonViewer.Benchmarks;

public sealed class BenchmarkConfig : ManualConfig
{
    public BenchmarkConfig()
    {
        AddColumn(StatisticColumn.P95);
        AddColumn(new ThroughputColumn());
        AddExporter(JsonExporter.Full);
    }
}
