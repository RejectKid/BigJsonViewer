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
        CancellationToken cancellationToken = default)
    {
        var name = node.NameRange.Length > 0
            ? await ReadTextAsync(node.NameRange, MaximumNameBytes, cancellationToken).ConfigureAwait(false)
            : node.ParentId == 0 ? "$" : $"[{node.Id:N0}]";
        var suffix = node.Kind switch
        {
            JsonNodeKind.Object => $"{{…}}  ({node.ChildCount:N0})",
            JsonNodeKind.Array => $"[…]  ({node.ChildCount:N0})",
            JsonNodeKind.Document => $"document  ({node.ChildCount:N0})",
            _ => await ReadTextAsync(node.Range, 160, cancellationToken).ConfigureAwait(false)
        };
        return $"{name}: {suffix}";
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
            using var document = JsonDocument.Parse(bytes);
            using var output = new MemoryStream();
            using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true }))
            {
                document.RootElement.WriteTo(writer);
            }

            return Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
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
        var output = new StringBuilder();
        output.AppendLine("First 50 array items (bounded sample)");
        output.AppendLine();
        var rowNumber = 0;
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.Append('[').Append(rowNumber++).Append("] ");
            if (row.Kind == JsonNodeKind.Object)
            {
                var cells = await Index.GetChildrenAsync(row.Id, take: 32, cancellationToken: cancellationToken).ConfigureAwait(false);
                foreach (var cell in cells)
                {
                    output.Append(await GetNodeLabelAsync(cell, cancellationToken).ConfigureAwait(false)).Append("  |  ");
                }
            }
            else
            {
                output.Append(await GetNodeLabelAsync(row, cancellationToken).ConfigureAwait(false));
            }

            output.AppendLine();
        }

        if (node.ChildCount > rows.Count)
        {
            output.AppendLine($"… {node.ChildCount - rows.Count:N0} additional rows not materialized.");
        }

        return output.ToString();
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
                var name = await ReadTextAsync(current.NameRange, MaximumNameBytes, cancellationToken).ConfigureAwait(false);
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
