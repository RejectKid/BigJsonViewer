namespace BigJsonViewer.CorpusGenerator;

internal static class CommandLine
{
    public static CorpusOptions Parse(string[] args)
    {
        string? output = null;
        var scenario = CorpusScenario.WideArray;
        var size = 100 * SizeParser.Mebibyte;
        ulong seed = 20_260_714;
        var depth = 256;
        var marker = "BIGJSONVIEWER_SEARCH_TARGET";
        var markerEvery = 10_000;
        var manifest = true;
        var overwrite = false;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            switch (argument)
            {
                case "--output" or "-o":
                    output = NextValue(args, ref index, argument);
                    break;
                case "--scenario" or "-s":
                    scenario = ScenarioName.Parse(NextValue(args, ref index, argument));
                    break;
                case "--size":
                    size = SizeParser.Parse(NextValue(args, ref index, argument));
                    break;
                case "--seed":
                    seed = ulong.Parse(NextValue(args, ref index, argument), System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--depth":
                    depth = int.Parse(NextValue(args, ref index, argument), System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--marker":
                    marker = NextValue(args, ref index, argument);
                    break;
                case "--marker-every":
                    markerEvery = int.Parse(NextValue(args, ref index, argument), System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--no-manifest":
                    manifest = false;
                    break;
                case "--force" or "-f":
                    overwrite = true;
                    break;
                case "--help" or "-h":
                    throw new HelpRequestedException();
                default:
                    throw new ArgumentException($"Unknown argument '{argument}'. Use --help for usage.");
            }
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            throw new ArgumentException("--output is required.");
        }

        return new CorpusOptions
        {
            OutputPath = output,
            Scenario = scenario,
            TargetBytes = size,
            Seed = seed,
            Depth = depth,
            Marker = marker,
            MarkerEvery = markerEvery,
            WriteManifest = manifest,
            Overwrite = overwrite
        };
    }

    public static void PrintHelp(TextWriter writer)
    {
        writer.WriteLine("BigJsonViewer deterministic corpus generator");
        writer.WriteLine();
        writer.WriteLine("Usage:");
        writer.WriteLine("  dotnet run --project tools/BigJsonViewer.CorpusGenerator -- [options]");
        writer.WriteLine();
        writer.WriteLine("Required:");
        writer.WriteLine("  -o, --output PATH       Destination file");
        writer.WriteLine();
        writer.WriteLine("Options:");
        writer.WriteLine("  -s, --scenario NAME     wide-array, deep-object, json-lines, minified,");
        writer.WriteLine("                          large-string, whitespace, escaped-strings,");
        writer.WriteLine("                          invalid-utf8, truncated, malformed");
        writer.WriteLine("      --size SIZE         Target size, for example 500MB or 10GiB (default 100MiB)");
        writer.WriteLine("      --seed NUMBER       Deterministic seed (default 20260714)");
        writer.WriteLine("      --depth NUMBER      Requested depth for deep-object (default 256)");
        writer.WriteLine("      --marker TEXT       ASCII search marker");
        writer.WriteLine("      --marker-every N    Record interval for markers (default 10000)");
        writer.WriteLine("      --no-manifest       Do not write the .manifest.json sidecar");
        writer.WriteLine("  -f, --force             Replace an existing output file");
        writer.WriteLine("  -h, --help              Show this help");
    }

    private static string NextValue(string[] args, ref int index, string argument)
    {
        if (++index >= args.Length)
        {
            throw new ArgumentException($"{argument} requires a value.");
        }

        return args[index];
    }
}

internal sealed class HelpRequestedException : Exception;
