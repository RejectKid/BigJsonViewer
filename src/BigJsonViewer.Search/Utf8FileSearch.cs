using System.Buffers.Binary;
using System.Text;
using BigJsonViewer.Storage;

namespace BigJsonViewer.Search;

public sealed class Utf8FileSearch
{
    private const int ProgressBatchSize = 256;

    public async Task<SearchResults> SearchAsync(
        string sourcePath,
        SearchQuery query,
        IProgress<SearchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(query);
        var resultPath = Path.Combine(
            Path.GetTempPath(),
            $"bigjsonviewer-search-{Guid.NewGuid():N}.results");
        try
        {
            var count = await Task.Run(
                () => SearchCore(sourcePath, resultPath, query, progress, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            return new SearchResults(resultPath, count);
        }
        catch
        {
            TryDelete(resultPath);
            throw;
        }
    }

    private static long SearchCore(
        string sourcePath,
        string resultPath,
        SearchQuery query,
        IProgress<SearchProgress>? progress,
        CancellationToken cancellationToken)
    {
        var pattern = Encoding.UTF8.GetBytes(query.CaseSensitive ? query.Text : query.Text.ToLowerInvariant());
        var prefix = BuildPrefixTable(pattern);
        var matched = 0;
        var matchStartedInString = false;
        var inString = false;
        var escaped = false;
        var currentStringIsProperty = false;
        var matchStartedInProperty = false;
        var containers = new List<ContainerState>(64);
        long count = 0;
        var batch = new List<SearchMatch>(ProgressBatchSize);

        using var output = new FileStream(
            resultPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        using var reader = new PooledSequentialFileReader(sourcePath);
        var searchRange = query.Range ?? new Core.SourceRange(0, reader.Length);
        if (searchRange.Offset < 0 || searchRange.Length < 0 || searchRange.Offset > reader.Length ||
            searchRange.Length > reader.Length - searchRange.Offset)
        {
            throw new ArgumentOutOfRangeException(nameof(query), "The search range is outside the source.");
        }

        reader.Read(
            searchRange.Offset,
            searchRange.Length,
            (chunk, token) =>
            {
                var data = chunk.Data.Span;
                for (var index = 0; index < data.Length; index++)
                {
                    token.ThrowIfCancellationRequested();
                    var raw = data[index];
                    var candidate = query.CaseSensitive ? raw : ToLowerAscii(raw);
                    while (matched > 0 && candidate != pattern[matched])
                    {
                        matched = prefix[matched - 1];
                    }

                    if (candidate == pattern[matched])
                    {
                        if (matched == 0)
                        {
                            matchStartedInString = inString;
                            matchStartedInProperty = inString && currentStringIsProperty;
                        }

                        matched++;
                        if (matched == pattern.Length)
                        {
                            var match = new SearchMatch(
                                new Core.SourceRange(chunk.Offset + index - pattern.Length + 1, pattern.Length),
                                matchStartedInString,
                                matchStartedInProperty);
                            if (MatchesMode(match, query.Mode))
                            {
                                Write(output, match);
                                batch.Add(match);
                                count++;
                                if (batch.Count == ProgressBatchSize)
                                {
                                    Report(
                                        progress,
                                        chunk.Offset + index + 1 - searchRange.Offset,
                                        searchRange.Length,
                                        count,
                                        batch);
                                }
                            }

                            matched = prefix[matched - 1];
                        }
                    }

                    if (inString)
                    {
                        if (escaped)
                        {
                            escaped = false;
                        }
                        else if (raw == (byte)'\\')
                        {
                            escaped = true;
                        }
                        else if (raw == (byte)'"')
                        {
                            inString = false;
                            currentStringIsProperty = false;
                        }
                    }
                    else if (raw == (byte)'"')
                    {
                        currentStringIsProperty = containers.Count > 0 &&
                            containers[^1].Kind == ContainerKind.Object &&
                            containers[^1].ExpectingProperty;
                        inString = true;
                    }
                    else if (raw == (byte)'{')
                    {
                        MarkValueStarted(containers);
                        containers.Add(new ContainerState(ContainerKind.Object, true));
                    }
                    else if (raw == (byte)'[')
                    {
                        MarkValueStarted(containers);
                        containers.Add(new ContainerState(ContainerKind.Array, false));
                    }
                    else if (raw is (byte)'}' or (byte)']')
                    {
                        if (containers.Count > 0)
                        {
                            containers.RemoveAt(containers.Count - 1);
                        }
                    }
                    else if (raw == (byte)',' && containers.Count > 0 && containers[^1].Kind == ContainerKind.Object)
                    {
                        containers[^1] = containers[^1] with { ExpectingProperty = true };
                    }
                    else if (raw == (byte)':' && containers.Count > 0 && containers[^1].Kind == ContainerKind.Object)
                    {
                        containers[^1] = containers[^1] with { ExpectingProperty = false };
                    }
                }

                Report(progress, chunk.EndOffset - searchRange.Offset, searchRange.Length, count, batch);
            },
            cancellationToken);
        output.Flush(flushToDisk: false);
        Report(progress, searchRange.Length, searchRange.Length, count, batch);
        return count;
    }

    private static void Write(Stream stream, SearchMatch match)
    {
        Span<byte> record = stackalloc byte[SearchResults.RecordSize];
        BinaryPrimitives.WriteInt64LittleEndian(record, match.Range.Offset);
        BinaryPrimitives.WriteInt64LittleEndian(record[8..], match.Range.Length);
        record[16] = match.IsInsideString ? (byte)1 : (byte)0;
        record[17] = match.IsPropertyName ? (byte)1 : (byte)0;
        stream.Write(record);
    }

    private static void Report(
        IProgress<SearchProgress>? progress,
        long bytes,
        long total,
        long count,
        List<SearchMatch> batch)
    {
        if (progress is null)
        {
            batch.Clear();
            return;
        }

        var snapshot = batch.Count == 0 ? Array.Empty<SearchMatch>() : batch.ToArray();
        batch.Clear();
        progress.Report(new SearchProgress(bytes, total, count, snapshot));
    }

    private static int[] BuildPrefixTable(ReadOnlySpan<byte> pattern)
    {
        var prefix = new int[pattern.Length];
        var length = 0;
        for (var index = 1; index < pattern.Length; index++)
        {
            while (length > 0 && pattern[index] != pattern[length])
            {
                length = prefix[length - 1];
            }

            if (pattern[index] == pattern[length])
            {
                prefix[index] = ++length;
            }
        }

        return prefix;
    }

    private static bool MatchesMode(SearchMatch match, SearchMode mode) => mode switch
    {
        SearchMode.Anywhere => true,
        SearchMode.InsideStrings => match.IsInsideString,
        SearchMode.StringValues => match.IsInsideString && !match.IsPropertyName,
        SearchMode.PropertyNames => match.IsPropertyName,
        SearchMode.OutsideStrings => !match.IsInsideString,
        _ => false
    };

    private static byte ToLowerAscii(byte value) =>
        value is >= (byte)'A' and <= (byte)'Z' ? (byte)(value + 32) : value;

    private static void MarkValueStarted(List<ContainerState> containers)
    {
        if (containers.Count > 0 && containers[^1].Kind == ContainerKind.Object)
        {
            containers[^1] = containers[^1] with { ExpectingProperty = false };
        }
    }

    private readonly record struct ContainerState(ContainerKind Kind, bool ExpectingProperty);

    private enum ContainerKind : byte
    {
        Object,
        Array
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
