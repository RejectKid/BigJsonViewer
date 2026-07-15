using System.Text;
using System.Text.Json;
using BigJsonViewer.Core;
using BigJsonViewer.Indexing;
using BigJsonViewer.Storage;

namespace BigJsonViewer.App;

internal sealed class DocumentSession : IAsyncDisposable
{
    private const int MaximumNameBytes = 4096;
    private const int MaximumPreviewBytes = 64 * 1024;
    private readonly RandomAccessWindowCache _source;

    private DocumentSession(
        SourceMetadata metadata,
        SourceSessionGuard guard,
        RandomAccessWindowCache source,
        BjxIndexReader index,
        string indexPath)
    {
        Metadata = metadata;
        Guard = guard;
        _source = source;
        Index = index;
        IndexPath = indexPath;
    }

    public SourceMetadata Metadata { get; }

    public SourceSessionGuard Guard { get; }

    public BjxIndexReader Index { get; }

    public string IndexPath { get; }

    public RandomAccessWindowCacheStatistics SourceCacheStatistics => _source.Statistics;

    public long IndexLength => new FileInfo(IndexPath).Length;

    public static async Task<DocumentSession> OpenAsync(
        string path,
        IProgress<IndexBuildProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var metadata = await SourceInspector.InspectAsync(path, cancellationToken).ConfigureAwait(false);
        var indexPath = GetIndexPath(metadata);
        BjxIndexReader? index = null;
        if (File.Exists(indexPath))
        {
            try
            {
                index = await BjxIndexReader.OpenAsync(
                    indexPath,
                    metadata.Identity,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                index = null;
            }
        }

        if (index is null)
        {
            await new StreamingJsonIndexer().BuildAsync(
                metadata.Path,
                indexPath,
                progress,
                cancellationToken).ConfigureAwait(false);
            index = await BjxIndexReader.OpenAsync(
                indexPath,
                metadata.Identity,
                cancellationToken).ConfigureAwait(false);
        }

        var source = new RandomAccessWindowCache(metadata.Path);
        return new DocumentSession(
            metadata,
            new SourceSessionGuard(metadata),
            source,
            index,
            indexPath);
    }

    public async Task<string> GetNodeLabelAsync(
        IndexedNode node,
        CancellationToken cancellationToken = default,
        long? arrayOrdinal = null)
    {
        var name = node.NameRange.Length > 0
            ? await GetNodeNameAsync(node, cancellationToken).ConfigureAwait(false)
            : arrayOrdinal is { } ordinal ? $"[{ordinal:N0}]" : await GetNodePathSegmentAsync(node, cancellationToken).ConfigureAwait(false);
        var suffix = node.Kind switch
        {
            JsonNodeKind.Object => $"{{…}}  ({node.ChildCount:N0})",
            JsonNodeKind.Array => $"[…]  ({node.ChildCount:N0})",
            JsonNodeKind.Document => $"document  ({node.ChildCount:N0})",
            _ => await ReadTextAsync(node.Range, 160, cancellationToken).ConfigureAwait(false)
        };
        return $"{name}: {suffix}";
    }

    public async Task<string> GetNodePathSegmentAsync(
        IndexedNode node,
        CancellationToken cancellationToken = default)
    {
        if (node.NameRange.Length > 0)
        {
            return await GetNodeNameAsync(node, cancellationToken).ConfigureAwait(false);
        }

        if (node.ParentId < 0)
        {
            return "$";
        }

        var parent = await Index.GetNodeAsync(node.ParentId, cancellationToken).ConfigureAwait(false);
        if (parent.Kind == JsonNodeKind.Array)
        {
            var ordinal = await FindChildOrdinalAsync(parent.Id, node.Id, cancellationToken).ConfigureAwait(false);
            return $"[{ordinal:N0}]";
        }

        if (parent.Kind == JsonNodeKind.Document)
        {
            if (parent.ChildCount <= 1)
            {
                return "$";
            }

            var ordinal = await FindChildOrdinalAsync(parent.Id, node.Id, cancellationToken).ConfigureAwait(false);
            return $"$[{ordinal:N0}]";
        }

        return $"[{node.Id:N0}]";
    }

    public Task<string> GetRawPreviewAsync(
        SourceRange range,
        CancellationToken cancellationToken = default) =>
        ReadTextAsync(range, MaximumPreviewBytes, cancellationToken);

    public async Task<string> GetPrettyPreviewAsync(
        IndexedNode node,
        CancellationToken cancellationToken = default)
    {
        if (node.Range.Length > MaximumPreviewBytes)
        {
            var prefix = await ReadTextAsync(node.Range, MaximumPreviewBytes, cancellationToken).ConfigureAwait(false);
            return prefix + $"\n\n… preview limited to {MaximumPreviewBytes / 1024:N0} KiB; node is {node.Range.Length:N0} bytes.";
        }

        var bytes = await ReadBytesAsync(node.Range, MaximumPreviewBytes, cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonDisplayFormatter.Format(bytes);
        }
        catch (JsonException)
        {
            return Encoding.UTF8.GetString(bytes);
        }
    }

    public async Task<string> BuildTableSampleAsync(
        IndexedNode node,
        CancellationToken cancellationToken = default)
    {
        if (node.Kind != JsonNodeKind.Array)
        {
            return "Select an array to see a bounded table sample.";
        }

        var rows = await Index.GetChildrenAsync(node.Id, take: 50, cancellationToken: cancellationToken).ConfigureAwait(false);
        var columns = await InferColumnsAsync(rows, 16, cancellationToken).ConfigureAwait(false);
        var output = new StringBuilder();
        output.AppendLine($"Inferred table · first {rows.Count:N0} of {node.ChildCount:N0} rows · {columns.Count:N0} columns");
        output.AppendLine();
        output.Append("#\t").AppendLine(string.Join('\t', columns));
        var rowNumber = 0;
        foreach (var row in rows)
        {
            var cells = await ReadRowAsync(row, columns, cancellationToken).ConfigureAwait(false);
            output.Append(rowNumber++).Append('\t').AppendLine(string.Join('\t', cells));
        }

        if (node.ChildCount > rows.Count)
        {
            output.AppendLine($"… {node.ChildCount - rows.Count:N0} additional rows not materialized.");
        }

        return output.ToString();
    }

    public async Task ExportCsvAsync(
        IndexedNode array,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        if (array.Kind != JsonNodeKind.Array)
        {
            throw new InvalidOperationException("Select an array before exporting CSV.");
        }

        var sample = await Index.GetChildrenAsync(array.Id, take: 100, cancellationToken: cancellationToken).ConfigureAwait(false);
        var columns = await InferColumnsAsync(sample, 64, cancellationToken).ConfigureAwait(false);
        var temporaryPath = destinationPath + $".{Guid.NewGuid():N}.exporting";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, true))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                await writer.WriteLineAsync(string.Join(',', columns.Select(EscapeCsv))).ConfigureAwait(false);
                long skip = 0;
                while (skip < array.ChildCount)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var rows = await Index.GetChildrenAsync(array.Id, skip, 256, cancellationToken).ConfigureAwait(false);
                    if (rows.Count == 0)
                    {
                        break;
                    }

                    foreach (var row in rows)
                    {
                        var cells = await ReadRowAsync(row, columns, cancellationToken).ConfigureAwait(false);
                        await writer.WriteLineAsync(string.Join(',', cells.Select(EscapeCsv))).ConfigureAwait(false);
                    }

                    skip += rows.Count;
                }

                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        catch
        {
            File.Delete(temporaryPath);
            throw;
        }
    }

    public async Task<IndexedNode> FindNodeByOffsetAsync(long offset, CancellationToken cancellationToken = default)
    {
        if ((ulong)offset >= (ulong)Metadata.Identity.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), $"Offset must be between 0 and {Metadata.Identity.Length - 1:N0}.");
        }

        long low = 0;
        long high = Index.Header.NodeCount - 1;
        long candidate = 0;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            var node = await Index.GetNodeAsync(middle, cancellationToken).ConfigureAwait(false);
            if (node.Range.Offset <= offset)
            {
                candidate = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        var current = await Index.GetNodeAsync(candidate, cancellationToken).ConfigureAwait(false);
        while (current.Id > 0 && (offset < current.Range.Offset || offset >= current.Range.End))
        {
            current = await Index.GetNodeAsync(current.ParentId, cancellationToken).ConfigureAwait(false);
        }

        return current;
    }

    public async Task<IndexedNode> FindNodeByPointerAsync(string pointer, CancellationToken cancellationToken = default)
    {
        if (pointer is not ("" or "$" or "/") && !pointer.StartsWith("/", StringComparison.Ordinal))
        {
            throw new FormatException("A JSON Pointer must be empty, '$', or begin with '/'.");
        }

        var current = await Index.GetNodeAsync(0, cancellationToken).ConfigureAwait(false);
        if (current.ChildCount == 1)
        {
            current = (await Index.GetChildrenAsync(0, take: 1, cancellationToken: cancellationToken).ConfigureAwait(false))[0];
        }

        if (pointer is "" or "$" or "/")
        {
            return current;
        }

        foreach (var encodedSegment in pointer[1..].Split('/'))
        {
            var segment = encodedSegment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            current = await FindNamedChildAsync(current, segment, cancellationToken).ConfigureAwait(false);
        }

        return current;
    }

    public async Task<IReadOnlyList<IndexedNode>> GetNodePathAsync(
        IndexedNode node,
        CancellationToken cancellationToken = default)
    {
        var path = new List<IndexedNode> { node };
        while (node.ParentId >= 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (node.ParentId == 0)
            {
                break;
            }

            node = await Index.GetNodeAsync(node.ParentId, cancellationToken).ConfigureAwait(false);
            path.Add(node);
        }

        path.Reverse();
        return path;
    }

    public async Task<string> BuildStatisticsAsync(
        IndexedNode root,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        const long maximumNodes = 100_000;
        const int maximumTrackedKeys = 2_048;
        var endId = Math.Min(root.SubtreeEndId < root.Id ? root.Id : root.SubtreeEndId, root.Id + maximumNodes - 1);
        var counts = new long[Enum.GetValues<JsonNodeKind>().Length];
        var keys = new Dictionary<string, long>(StringComparer.Ordinal);
        long totalBytes = 0;
        for (var id = root.Id; id <= endId; id++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var node = await Index.GetNodeAsync(id, cancellationToken).ConfigureAwait(false);
            counts[(int)node.Kind]++;
            totalBytes += node.Range.Length;
            if (node.NameRange.Length > 0)
            {
                var key = await GetNodeNameAsync(node, cancellationToken).ConfigureAwait(false);
                if (keys.TryGetValue(key, out var count))
                {
                    keys[key] = count + 1;
                }
                else if (keys.Count < maximumTrackedKeys)
                {
                    keys[key] = 1;
                }
            }

            if ((id - root.Id) % 512 == 0)
            {
                progress?.Report((double)(id - root.Id + 1) / (endId - root.Id + 1));
            }
        }

        var scanned = endId - root.Id + 1;
        var output = new StringBuilder();
        output.AppendLine("BOUNDED STRUCTURE PROFILE");
        output.AppendLine($"Scanned nodes: {scanned:N0}{(endId < root.SubtreeEndId ? " (100,000-node sample)" : string.Empty)}");
        output.AppendLine($"Selected source span: {root.Range.Length:N0} bytes");
        output.AppendLine($"Aggregate indexed spans: {totalBytes:N0} bytes");
        output.AppendLine();
        output.AppendLine("TYPE DISTRIBUTION");
        foreach (var kind in Enum.GetValues<JsonNodeKind>())
        {
            output.AppendLine($"{kind,-10} {counts[(int)kind],12:N0}  {(scanned == 0 ? 0 : counts[(int)kind] * 100d / scanned),6:N2}%");
        }

        output.AppendLine().AppendLine("MOST FREQUENT KEYS");
        foreach (var pair in keys.OrderByDescending(item => item.Value).ThenBy(item => item.Key, StringComparer.Ordinal).Take(20))
        {
            output.AppendLine($"{pair.Value,12:N0}  {pair.Key}");
        }

        return output.ToString();
    }

    public async Task<string> CompareAsync(
        DocumentSession other,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var leftCount = Index.Header.NodeCount;
        var rightCount = other.Index.Header.NodeCount;
        var shared = Math.Min(leftCount, rightCount);
        long changed = 0;
        long kindChanges = 0;
        long nameChanges = 0;
        long valueChanges = 0;
        var examples = new List<string>();
        for (long id = 0; id < shared; id++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var left = await Index.GetNodeAsync(id, cancellationToken).ConfigureAwait(false);
            var right = await other.Index.GetNodeAsync(id, cancellationToken).ConfigureAwait(false);
            var different = false;
            if (left.Kind != right.Kind || left.ParentId != right.ParentId || left.ChildCount != right.ChildCount)
            {
                kindChanges++;
                different = true;
            }

            if (await GetNodeNameAsync(left, cancellationToken).ConfigureAwait(false) !=
                await other.GetNodeNameAsync(right, cancellationToken).ConfigureAwait(false))
            {
                nameChanges++;
                different = true;
            }

            if (left.ChildCount == 0 && right.ChildCount == 0 &&
                !await RangeEqualsAsync(left.Range, other, right.Range, cancellationToken).ConfigureAwait(false))
            {
                valueChanges++;
                different = true;
            }

            if (different)
            {
                changed++;
                if (examples.Count < 12)
                {
                    examples.Add($"node {id:N0}: {left.Kind} @ {left.Range.Offset:N0} ↔ {right.Kind} @ {right.Range.Offset:N0}");
                }
            }

            if (id % 512 == 0)
            {
                progress?.Report(shared == 0 ? 1 : (double)(id + 1) / shared);
            }
        }

        var output = new StringBuilder();
        output.AppendLine("INDEX-AWARE PREORDER COMPARISON");
        output.AppendLine($"Left:  {Metadata.Path}");
        output.AppendLine($"Right: {other.Metadata.Path}");
        output.AppendLine();
        output.AppendLine($"Nodes: {leftCount:N0} ↔ {rightCount:N0}");
        output.AppendLine($"Changed aligned nodes: {changed:N0}");
        output.AppendLine($"Structural/type changes: {kindChanges:N0}");
        output.AppendLine($"Property-name changes: {nameChanges:N0}");
        output.AppendLine($"Scalar-value changes: {valueChanges:N0}");
        output.AppendLine($"Unpaired trailing nodes: {Math.Abs(leftCount - rightCount):N0}");
        if (examples.Count > 0)
        {
            output.AppendLine().AppendLine("FIRST DIFFERENCES");
            foreach (var example in examples)
            {
                output.AppendLine(example);
            }
        }

        return output.ToString();
    }

    public async Task<string> GetNodeNameAsync(IndexedNode node, CancellationToken cancellationToken = default)
    {
        if (node.NameRange.Length == 0)
        {
            return string.Empty;
        }

        var escaped = await ReadTextAsync(node.NameRange, MaximumNameBytes, cancellationToken).ConfigureAwait(false);
        try
        {
            var encoded = Encoding.UTF8.GetBytes($"\"{escaped}\"");
            var reader = new Utf8JsonReader(encoded);
            return reader.Read() ? reader.GetString() ?? escaped : escaped;
        }
        catch (JsonException)
        {
            return escaped;
        }
    }

    public async Task<string> ReadAroundAsync(
        SourceRange range,
        CancellationToken cancellationToken = default)
    {
        const int context = 256;
        var start = Math.Max(0, range.Offset - context);
        var length = Math.Min(MaximumPreviewBytes, Metadata.Identity.Length - start);
        var text = await ReadTextAsync(new SourceRange(start, length), MaximumPreviewBytes, cancellationToken).ConfigureAwait(false);
        return $"Byte {range.Offset:N0} · length {range.Length:N0}\n\n{text}";
    }

    public async Task<string> GetJsonPointerAsync(
        IndexedNode node,
        CancellationToken cancellationToken = default)
    {
        var segments = new List<string>();
        var current = node;
        while (current.ParentId > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (current.NameRange.Length > 0)
            {
                var name = await GetNodeNameAsync(current, cancellationToken).ConfigureAwait(false);
                segments.Add(name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal));
            }
            else
            {
                segments.Add((await FindChildOrdinalAsync(current.ParentId, current.Id, cancellationToken).ConfigureAwait(false)).ToString());
            }

            current = await Index.GetNodeAsync(current.ParentId, cancellationToken).ConfigureAwait(false);
        }

        segments.Reverse();
        return segments.Count == 0 ? string.Empty : "/" + string.Join('/', segments);
    }

    public async Task ExportAsync(
        SourceRange range,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        const int bufferSize = 1024 * 1024;
        var buffer = new byte[(int)Math.Min(bufferSize, Math.Max(1, range.Length))];
        var temporaryPath = destinationPath + $".{Guid.NewGuid():N}.exporting";
        try
        {
            await using (var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var offset = range.Offset;
                var remaining = range.Length;
                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var requested = (int)Math.Min(remaining, buffer.Length);
                    var read = await _source.ReadAsync(offset, buffer.AsMemory(0, requested), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        throw new EndOfStreamException("The source ended during export.");
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    offset += read;
                    remaining -= read;
                }
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
            }

            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Index.DisposeAsync().ConfigureAwait(false);
        await _source.DisposeAsync().ConfigureAwait(false);
    }

    private async Task<string> ReadTextAsync(
        SourceRange range,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var bytes = await ReadBytesAsync(range, maximumBytes, cancellationToken).ConfigureAwait(false);
        var text = Encoding.UTF8.GetString(bytes);
        return range.Length > bytes.Length ? text + "…" : text;
    }

    private async Task<byte[]> ReadBytesAsync(
        SourceRange range,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var length = (int)Math.Min(Math.Max(0, range.Length), maximumBytes);
        var bytes = GC.AllocateUninitializedArray<byte>(length);
        var read = 0;
        while (read < length)
        {
            var count = await _source.ReadAsync(
                range.Offset + read,
                bytes.AsMemory(read),
                cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            read += count;
        }

        return read == bytes.Length ? bytes : bytes[..read];
    }

    private async Task<long> FindChildOrdinalAsync(
        long parentId,
        long childId,
        CancellationToken cancellationToken)
    {
        var parent = await Index.GetNodeAsync(parentId, cancellationToken).ConfigureAwait(false);
        var id = parent.FirstChildId;
        for (long ordinal = 0; ordinal < parent.ChildCount; ordinal++)
        {
            var child = await Index.GetNodeAsync(id, cancellationToken).ConfigureAwait(false);
            if (child.Id == childId)
            {
                return ordinal;
            }

            id = checked(child.SubtreeEndId + 1);
        }

        throw new InvalidDataException("The selected node is not linked from its parent.");
    }

    private async Task<IndexedNode> FindNamedChildAsync(
        IndexedNode parent,
        string segment,
        CancellationToken cancellationToken)
    {
        if (parent.Kind == JsonNodeKind.Array && long.TryParse(segment, out var ordinal) && ordinal >= 0)
        {
            var match = await Index.GetChildrenAsync(parent.Id, ordinal, 1, cancellationToken).ConfigureAwait(false);
            return match.Count == 1 ? match[0] : throw new KeyNotFoundException($"Array index {ordinal:N0} does not exist.");
        }

        long skip = 0;
        while (skip < parent.ChildCount)
        {
            var page = await Index.GetChildrenAsync(parent.Id, skip, 256, cancellationToken).ConfigureAwait(false);
            foreach (var child in page)
            {
                if (string.Equals(await GetNodeNameAsync(child, cancellationToken).ConfigureAwait(false), segment, StringComparison.Ordinal))
                {
                    return child;
                }
            }

            skip += page.Count;
        }

        throw new KeyNotFoundException($"JSON Pointer segment '{segment}' was not found.");
    }

    private async Task<IReadOnlyList<string>> InferColumnsAsync(
        IReadOnlyList<IndexedNode> rows,
        int maximumColumns,
        CancellationToken cancellationToken)
    {
        var columns = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row.Kind != JsonNodeKind.Object)
            {
                if (seen.Add("value"))
                {
                    columns.Add("value");
                }

                continue;
            }

            var cells = await Index.GetChildrenAsync(row.Id, take: maximumColumns, cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (var cell in cells)
            {
                var name = await GetNodeNameAsync(cell, cancellationToken).ConfigureAwait(false);
                if (seen.Add(name))
                {
                    columns.Add(name);
                    if (columns.Count == maximumColumns)
                    {
                        return columns;
                    }
                }
            }
        }

        return columns.Count == 0 ? ["value"] : columns;
    }

    private async Task<IReadOnlyList<string>> ReadRowAsync(
        IndexedNode row,
        IReadOnlyList<string> columns,
        CancellationToken cancellationToken)
    {
        if (row.Kind != JsonNodeKind.Object)
        {
            var scalar = await GetCellTextAsync(row, cancellationToken).ConfigureAwait(false);
            return columns.Select(column => string.Equals(column, "value", StringComparison.Ordinal) ? scalar : string.Empty).ToArray();
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var cells = await Index.GetChildrenAsync(row.Id, take: 4096, cancellationToken: cancellationToken).ConfigureAwait(false);
        foreach (var cell in cells)
        {
            var name = await GetNodeNameAsync(cell, cancellationToken).ConfigureAwait(false);
            if (columns.Contains(name, StringComparer.Ordinal))
            {
                values[name] = await GetCellTextAsync(cell, cancellationToken).ConfigureAwait(false);
            }
        }

        return columns.Select(column => values.GetValueOrDefault(column, string.Empty)).ToArray();
    }

    private async Task<string> GetCellTextAsync(IndexedNode node, CancellationToken cancellationToken)
    {
        var text = await ReadTextAsync(node.Range, 4096, cancellationToken).ConfigureAwait(false);
        if (node.Kind != JsonNodeKind.String || node.Range.Length > 4096)
        {
            return text.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        }

        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(text));
            return reader.Read() ? reader.GetString() ?? string.Empty : text;
        }
        catch (JsonException)
        {
            return text;
        }
    }

    private async Task<bool> RangeEqualsAsync(
        SourceRange left,
        DocumentSession other,
        SourceRange right,
        CancellationToken cancellationToken)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        var leftBuffer = new byte[(int)Math.Min(64 * 1024, Math.Max(1, left.Length))];
        var rightBuffer = new byte[leftBuffer.Length];
        long compared = 0;
        while (compared < left.Length)
        {
            var length = (int)Math.Min(leftBuffer.Length, left.Length - compared);
            var leftRead = await _source.ReadAsync(left.Offset + compared, leftBuffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
            var rightRead = await other._source.ReadAsync(right.Offset + compared, rightBuffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
            if (leftRead == 0 || rightRead == 0)
            {
                throw new EndOfStreamException("A compared source ended unexpectedly.");
            }

            if (leftRead != rightRead || !leftBuffer.AsSpan(0, leftRead).SequenceEqual(rightBuffer.AsSpan(0, rightRead)))
            {
                return false;
            }

            compared += leftRead;
        }

        return true;
    }

    private static string EscapeCsv(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : value;

    private static string GetIndexPath(SourceMetadata metadata)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BigJsonViewer",
            "indexes");
        Directory.CreateDirectory(root);
        var fileName = $"{metadata.Identity.SampleHash:X16}-{metadata.Identity.Length:X16}.bjx";
        return Path.Combine(root, fileName);
    }
}
