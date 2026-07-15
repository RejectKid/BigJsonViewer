using BigJsonViewer.Core;
using BigJsonViewer.Storage;

namespace BigJsonViewer.Indexing;

public sealed class StreamingJsonIndexer : IJsonIndexer
{
    private readonly JsonIndexOptions _options;

    public StreamingJsonIndexer(JsonIndexOptions? options = null)
    {
        _options = options ?? new JsonIndexOptions();
    }

    public async Task BuildAsync(
        string sourcePath,
        string indexPath,
        IProgress<IndexBuildProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexPath);
        var metadata = await SourceInspector.InspectAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        ValidateSource(metadata);

        var fullIndexPath = Path.GetFullPath(indexPath);
        var directory = Path.GetDirectoryName(fullIndexPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = fullIndexPath + $".{Guid.NewGuid():N}.building";
        try
        {
            await Task.Run(
                () => BuildCore(metadata, temporaryPath, progress, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, fullIndexPath, overwrite: true);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    private void BuildCore(
        SourceMetadata metadata,
        string indexPath,
        IProgress<IndexBuildProgress>? progress,
        CancellationToken cancellationToken)
    {
        var bomLength = metadata.Encoding == SourceEncoding.Utf8Bom ? 3 : 0;
        var builder = new StructuralIndexBuilder(
            metadata.Format,
            _options.MaximumDepth,
            _options.WriteBatchSize);
        using var reader = new PooledSequentialFileReader(metadata.Path);
        using var writer = new SynchronousWriterScope(new BjxIndexWriter(indexPath, metadata));
        writer.Value.Initialize();
        Flush(builder, writer.Value, cancellationToken);

        reader.Read(
            bomLength,
            reader.Length - bomLength,
            (chunk, token) =>
            {
                builder.Process(chunk.Data.Span, chunk.Offset);
                if (builder.PendingCount >= _options.WriteBatchSize)
                {
                    Flush(builder, writer.Value, token);
                }

                progress?.Report(new IndexBuildProgress(chunk.EndOffset, reader.Length, builder.NodeCount));
            },
            cancellationToken);

        builder.Complete(reader.Length);
        Flush(builder, writer.Value, cancellationToken);
        var currentIdentity = SourceInspector.InspectAsync(metadata.Path, cancellationToken)
            .GetAwaiter()
            .GetResult()
            .Identity;
        if (currentIdentity != metadata.Identity)
        {
            throw new IOException("The source changed while it was being indexed.");
        }

        writer.Value.Complete(builder.NodeCount);
        progress?.Report(new IndexBuildProgress(reader.Length, reader.Length, builder.NodeCount));
    }

    private static void Flush(
        StructuralIndexBuilder builder,
        BjxIndexWriter writer,
        CancellationToken cancellationToken)
    {
        if (builder.PendingCount == 0)
        {
            return;
        }

        writer.WriteNodes(builder.Pending, cancellationToken);
        builder.ClearPending();
    }

    private static void ValidateSource(SourceMetadata metadata)
    {
        if (metadata.Format == JsonDocumentFormat.Compressed)
        {
            throw new NotSupportedException(
                $"{metadata.CompressionKind} input must be decompressed before random-access viewing.");
        }

        if (metadata.Encoding is not SourceEncoding.Utf8 and not SourceEncoding.Utf8Bom)
        {
            throw new NotSupportedException(
                $"{metadata.Encoding} input is detected. The first release indexes UTF-8 JSON without transcoding.");
        }
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

    private sealed class SynchronousWriterScope : IDisposable
    {
        public SynchronousWriterScope(BjxIndexWriter value) => Value = value;

        public BjxIndexWriter Value { get; }

        public void Dispose() => Value.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
