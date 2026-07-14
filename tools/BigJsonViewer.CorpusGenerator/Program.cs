using BigJsonViewer.CorpusGenerator;

try
{
    var options = CommandLine.Parse(args);
    var result = CorpusGenerator.Generate(options, ConsoleCancelToken.Token);
    var mib = result.ActualBytes / (double)SizeParser.Mebibyte;
    var throughput = result.Elapsed.TotalSeconds > 0 ? mib / result.Elapsed.TotalSeconds : 0;
    Console.WriteLine($"Generated {ScenarioName.ToKebabCase(result.Scenario)} corpus");
    Console.WriteLine($"Output:  {result.OutputPath}");
    Console.WriteLine($"Size:    {result.ActualBytes:N0} bytes ({mib:N2} MiB)");
    Console.WriteLine($"Markers: {result.MarkerCount:N0}");
    Console.WriteLine($"Elapsed: {result.Elapsed}");
    Console.WriteLine($"Rate:    {throughput:N2} MiB/s");
    return 0;
}
catch (HelpRequestedException)
{
    CommandLine.PrintHelp(Console.Out);
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Generation cancelled.");
    return 2;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Error: {exception.Message}");
    return 1;
}

internal static class ConsoleCancelToken
{
    private static readonly CancellationTokenSource Source = new();

    static ConsoleCancelToken()
    {
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            Source.Cancel();
        };
    }

    public static CancellationToken Token => Source.Token;
}
