namespace BigJsonViewer.CorpusGenerator;

public sealed record GenerationResult(
    string OutputPath,
    CorpusScenario Scenario,
    long RequestedBytes,
    long ActualBytes,
    ulong Seed,
    string Marker,
    long MarkerCount,
    IReadOnlyList<long> MarkerOffsets,
    bool MarkerOffsetsComplete,
    TimeSpan Elapsed);
