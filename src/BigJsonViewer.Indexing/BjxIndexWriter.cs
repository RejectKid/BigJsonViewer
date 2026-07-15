using System.Buffers;
using System.Buffers.Binary;
using BigJsonViewer.Core;
using Microsoft.Win32.SafeHandles;

namespace BigJsonViewer.Indexing;

internal sealed class BjxIndexWriter : IAsyncDisposable
{
    private const int WriteBufferSize = 512 * 1024;
    private readonly SafeFileHandle _handle;
    private readonly SourceMetadata _metadata;
    private readonly Dictionary<long, long> _openContainers = [];
    private readonly FileStream _checkpoints;
    private readonly string _checkpointPath;
    private long _appendOffset = BjxFormat.HeaderSize;
    private long _nextNodeId;
    private long _checkpointCount;

    public BjxIndexWriter(string path, SourceMetadata metadata)
    {
        _metadata = metadata;
        _handle = File.OpenHandle(
            path,
            FileMode.Create,
            FileAccess.ReadWrite,
            FileShare.Read,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        _checkpointPath = path + ".checkpoints";
        _checkpoints = new FileStream(
            _checkpointPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            64 * 1024,
            FileOptions.SequentialScan);
    }

    public void Initialize() =>
        RandomAccess.Write(_handle, BjxFormat.CreateHeader(_metadata, 0, BjxFormat.Incomplete), 0);

    public void WriteNodes(IReadOnlyList<IndexedNode> nodes, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(WriteBufferSize + BjxFormat.MaximumRecordSize);
        var buffered = 0;
        Span<byte> patch = stackalloc byte[BjxFormat.MaximumRecordSize];
        Span<byte> checkpoint = stackalloc byte[sizeof(long)];
        try
        {
            foreach (var node in nodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_openContainers.TryGetValue(node.Id, out var patchOffset))
                {
                    Flush(buffer, ref buffered);
                    var patchLength = BjxFormat.EncodeNode(node, patch);
                    RandomAccess.Write(_handle, patch[..patchLength], patchOffset);
                    _openContainers.Remove(node.Id);
                    continue;
                }

                if (node.Id != _nextNodeId)
                {
                    throw new InvalidDataException("Nodes must be appended in pre-order ID sequence.");
                }

                if (node.Id % BjxFormat.CheckpointInterval == 0)
                {
                    BinaryPrimitives.WriteInt64LittleEndian(checkpoint, _appendOffset + buffered);
                    _checkpoints.Write(checkpoint);
                    _checkpointCount++;
                }

                var recordOffset = _appendOffset + buffered;
                var length = BjxFormat.EncodeNode(node, buffer.AsSpan(buffered));
                buffered += length;
                if (node.Kind is JsonNodeKind.Document or JsonNodeKind.Object or JsonNodeKind.Array)
                {
                    _openContainers.Add(node.Id, recordOffset);
                }

                _nextNodeId++;
                if (buffered >= WriteBufferSize)
                {
                    Flush(buffer, ref buffered);
                }
            }

            Flush(buffer, ref buffered);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
        }
    }

    public void Complete(long nodeCount)
    {
        if (_openContainers.Count != 0 || nodeCount != _nextNodeId)
        {
            throw new InvalidDataException("The .bjx build has unfinished container records.");
        }

        _checkpoints.Flush();
        _checkpoints.Position = 0;
        var checkpointOffset = _appendOffset;
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            int read;
            while ((read = _checkpoints.Read(buffer, 0, buffer.Length)) > 0)
            {
                RandomAccess.Write(_handle, buffer.AsSpan(0, read), _appendOffset);
                _appendOffset += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
        }

        RandomAccess.Write(
            _handle,
            BjxFormat.CreateHeader(
                _metadata,
                nodeCount,
                BjxFormat.Complete,
                checkpointOffset,
                _checkpointCount),
            0);
        RandomAccess.FlushToDisk(_handle);
    }

    public ValueTask DisposeAsync()
    {
        _checkpoints.Dispose();
        _handle.Dispose();
        try
        {
            File.Delete(_checkpointPath);
        }
        catch (IOException)
        {
        }

        return ValueTask.CompletedTask;
    }

    private void Flush(byte[] buffer, ref int buffered)
    {
        if (buffered == 0)
        {
            return;
        }

        RandomAccess.Write(_handle, buffer.AsSpan(0, buffered), _appendOffset);
        _appendOffset += buffered;
        buffered = 0;
    }
}
