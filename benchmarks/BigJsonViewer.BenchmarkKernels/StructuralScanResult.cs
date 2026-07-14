namespace BigJsonViewer.BenchmarkKernels;

public readonly record struct StructuralScanResult(
    long StructuralCount,
    long CompletedStringCount,
    long EscapeCount,
    StructuralScanState EndState);
