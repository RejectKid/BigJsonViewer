namespace BigJsonViewer.CorpusGenerator;

public sealed record CorpusOptions
{
    public required string OutputPath { get; init; }

    public CorpusScenario Scenario { get; init; } = CorpusScenario.WideArray;

    public long TargetBytes { get; init; } = 100 * SizeParser.Mebibyte;

    public ulong Seed { get; init; } = 20_260_714;

    public int Depth { get; init; } = 256;

    public string Marker { get; init; } = "BIGJSONVIEWER_SEARCH_TARGET";

    public int MarkerEvery { get; init; } = 10_000;

    public bool WriteManifest { get; init; } = true;

    public bool Overwrite { get; init; }
}
