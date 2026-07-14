using System.Buffers;

namespace BigJsonViewer.BenchmarkKernels;

public static class StructuralScanner
{
    private static readonly SearchValues<byte> OutsideStringCandidates = SearchValues.Create(
        [(byte)'"', (byte)'{', (byte)'}', (byte)'[', (byte)']', (byte)',', (byte)':']);

    private static readonly SearchValues<byte> InsideStringCandidates = SearchValues.Create(
        [(byte)'"', (byte)'\\']);

    public static StructuralScanResult ScanScalar(ReadOnlySpan<byte> source, StructuralScanState initialState = default)
    {
        var inString = initialState.InString;
        var escaped = initialState.Escaped;
        long structuralCount = 0;
        long completedStringCount = 0;
        long escapeCount = 0;

        foreach (var value in source)
        {
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (value == (byte)'\\')
                {
                    escaped = true;
                    escapeCount++;
                }
                else if (value == (byte)'"')
                {
                    inString = false;
                    completedStringCount++;
                }

                continue;
            }

            if (value == (byte)'"')
            {
                inString = true;
            }
            else if (IsStructural(value))
            {
                structuralCount++;
            }
        }

        return new StructuralScanResult(
            structuralCount,
            completedStringCount,
            escapeCount,
            new StructuralScanState(inString, escaped));
    }

    public static StructuralScanResult ScanSearchValues(ReadOnlySpan<byte> source, StructuralScanState initialState = default)
    {
        var inString = initialState.InString;
        var escaped = initialState.Escaped;
        long structuralCount = 0;
        long completedStringCount = 0;
        long escapeCount = 0;
        var offset = 0;

        if (inString && escaped && !source.IsEmpty)
        {
            offset = 1;
            escaped = false;
        }

        while (offset < source.Length)
        {
            if (inString)
            {
                var relative = source[offset..].IndexOfAny(InsideStringCandidates);
                if (relative < 0)
                {
                    break;
                }

                var position = offset + relative;
                if (source[position] == (byte)'"')
                {
                    inString = false;
                    completedStringCount++;
                    offset = position + 1;
                }
                else
                {
                    escapeCount++;
                    if (position == source.Length - 1)
                    {
                        escaped = true;
                        break;
                    }

                    offset = position + 2;
                }

                continue;
            }

            var outsideRelative = source[offset..].IndexOfAny(OutsideStringCandidates);
            if (outsideRelative < 0)
            {
                break;
            }

            var outsidePosition = offset + outsideRelative;
            if (source[outsidePosition] == (byte)'"')
            {
                inString = true;
            }
            else
            {
                structuralCount++;
            }

            offset = outsidePosition + 1;
        }

        return new StructuralScanResult(
            structuralCount,
            completedStringCount,
            escapeCount,
            new StructuralScanState(inString, escaped));
    }

    private static bool IsStructural(byte value) => value is
        (byte)'{' or (byte)'}' or (byte)'[' or (byte)']' or (byte)',' or (byte)':';
}
