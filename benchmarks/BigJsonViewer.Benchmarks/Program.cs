using BenchmarkDotNet.Running;

namespace BigJsonViewer.Benchmarks;

public static class Program
{
    public static void Main(string[] args)
    {
        args = BenchmarkArguments.ExpandGroups(args);
        BenchmarkRunMetadataWriter.Prepare(args);
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}
