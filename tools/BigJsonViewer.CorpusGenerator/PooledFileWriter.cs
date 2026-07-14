using System.Buffers;
using System.Buffers.Text;
using System.Text;

namespace BigJsonViewer.CorpusGenerator;

internal sealed class PooledFileWriter : IDisposable
{
    private const int BufferSize = 1024 * 1024;
    private const int MaximumRecordedMarkerOffsets = 1_000_000;
    private readonly Stream _stream;
    private readonly byte[] _buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
    private int _buffered;
    private bool _disposed;

    public PooledFileWriter(Stream stream)
    {
        _stream = stream;
    }

    public long Position { get; private set; }

    public List<long> MarkerOffsets { get; } = [];

    public long MarkerCount { get; private set; }

    public void Write(ReadOnlySpan<byte> bytes)
    {
        while (!bytes.IsEmpty)
        {
            var available = BufferSize - _buffered;
            if (available == 0)
            {
                FlushBuffer();
                available = BufferSize;
            }

            var count = Math.Min(available, bytes.Length);
            bytes[..count].CopyTo(_buffer.AsSpan(_buffered));
            _buffered += count;
            Position += count;
            bytes = bytes[count..];
        }
    }

    public void WriteAscii(string value)
    {
        if (!value.All(char.IsAscii))
        {
            throw new ArgumentException("Corpus writer values must be ASCII so byte offsets stay deterministic.", nameof(value));
        }

        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            var count = Math.Min(remaining.Length, BufferSize - _buffered);
            Encoding.ASCII.GetBytes(remaining[..count], _buffer.AsSpan(_buffered));
            _buffered += count;
            Position += count;
            remaining = remaining[count..];
            if (_buffered == BufferSize)
            {
                FlushBuffer();
            }
        }
    }

    public void WriteInt64(long value)
    {
        Span<byte> formatted = stackalloc byte[32];
        if (!Utf8Formatter.TryFormat(value, formatted, out var written))
        {
            throw new InvalidOperationException("Failed to format an integer.");
        }

        Write(formatted[..written]);
    }

    public void WriteUInt64(ulong value)
    {
        Span<byte> formatted = stackalloc byte[32];
        if (!Utf8Formatter.TryFormat(value, formatted, out var written))
        {
            throw new InvalidOperationException("Failed to format an integer.");
        }

        Write(formatted[..written]);
    }

    public void WriteByte(byte value)
    {
        if (_buffered == BufferSize)
        {
            FlushBuffer();
        }

        _buffer[_buffered++] = value;
        Position++;
    }

    public void WriteRepeated(byte value, long count, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        while (count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_buffered == BufferSize)
            {
                FlushBuffer();
            }

            var chunk = (int)Math.Min(count, BufferSize - _buffered);
            _buffer.AsSpan(_buffered, chunk).Fill(value);
            _buffered += chunk;
            Position += chunk;
            count -= chunk;
        }
    }

    public void WriteMarker(string marker)
    {
        MarkerCount++;
        if (MarkerOffsets.Count < MaximumRecordedMarkerOffsets)
        {
            MarkerOffsets.Add(Position);
        }

        WriteAscii(marker);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        FlushBuffer();
        _stream.Dispose();
        ArrayPool<byte>.Shared.Return(_buffer);
        _disposed = true;
    }

    private void FlushBuffer()
    {
        if (_buffered == 0)
        {
            return;
        }

        _stream.Write(_buffer, 0, _buffered);
        _buffered = 0;
    }
}
