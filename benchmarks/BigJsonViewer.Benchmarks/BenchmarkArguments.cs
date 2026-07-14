namespace BigJsonViewer.Benchmarks;

internal static class BenchmarkArguments
{
    public static string[] ExpandGroups(string[] args)
    {
        var groupIndex = Array.FindIndex(args, argument =>
            string.Equals(argument, "--group", StringComparison.OrdinalIgnoreCase));
        if (groupIndex < 0)
        {
            return args;
        }

        if (groupIndex == args.Length - 1)
        {
            throw new ArgumentException("--group requires all, storage, scanning, or indexing.");
        }

        string[] filters = args[groupIndex + 1].ToLowerInvariant() switch
        {
            "all" => ["*"],
            "storage" => ["*SequentialReadBenchmarks*", "*RandomAccessBenchmarks*"],
            "scanning" => ["*StructuralScanBenchmarks*", "*Utf8DecodeBenchmarks*"],
            "indexing" => ["*IndexEncodingBenchmarks*"],
            var group => throw new ArgumentException($"Unknown benchmark group '{group}'.")
        };

        var expanded = new List<string>(args.Length + filters.Length);
        expanded.AddRange(args[..groupIndex]);
        expanded.AddRange(args[(groupIndex + 2)..]);
        expanded.Add("--filter");
        expanded.AddRange(filters);
        return [.. expanded];
    }
}
