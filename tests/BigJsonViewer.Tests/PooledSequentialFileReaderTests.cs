using System.Buffers;
using BigJsonViewer.Storage;

namespace BigJsonViewer.Tests;

public sealed class PooledSequentialFileReaderTests
{
    [Fact]
    public void UsesMeasuredDefaultBufferSize()
    {
        var options = new SequentialReadOptions();

        Assert.Equal(8 * 1024 * 1024, options.BufferSize);
    }

    [Theory]
    [InlineData(SequentialReadOptions.MinimumBufferSize - 1)]
    [InlineData(SequentialReadOptions.MaximumBufferSize + 1)]
    public void RejectsBufferSizesOutsideSupportedRange(int bufferSize)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SequentialReadOptions(bufferSize));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(SequentialReadOptions.MinimumBufferSize, 1)]
    [InlineData(SequentialReadOptions.MinimumBufferSize + 17, 2)]
    public void ReadsEveryByteAcrossBufferBoundaries(int fileSize, int expectedChunks)
    {
        var source = CreatePattern(fileSize);
        var path = CreateFile(source);
        var pool = new TrackingArrayPool(SequentialReadOptions.MinimumBufferSize);
        try
        {
            using var reader = new PooledSequentialFileReader(
                path,
                new SequentialReadOptions(SequentialReadOptions.MinimumBufferSize),
                pool);
            using var output = new MemoryStream();
            var offsets = new List<long>();

            var bytesRead = reader.Read((chunk, _) =>
            {
                offsets.Add(chunk.Offset);
                output.Write(chunk.Data.Span);
            });

            Assert.Equal(source.Length, bytesRead);
            Assert.Equal(source, output.ToArray());
            Assert.Equal(expectedChunks, offsets.Count);
            Assert.Equal(Enumerable.Range(0, expectedChunks).Select(index => (long)index * SequentialReadOptions.MinimumBufferSize), offsets);
            Assert.Equal(fileSize == 0 ? 0 : 1, pool.RentCount);
            Assert.Equal(pool.RentCount, pool.ReturnCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadsRangesAtOffsetsBeyondTenGiB()
    {
        var path = Path.GetTempFileName();
        const long fileLength = 12L * 1024 * 1024 * 1024 + 16 * 1024;
        try
        {
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.SetLength(fileLength);
                stream.Position = fileLength - 4;
                stream.Write("tail"u8);
            }

            using var reader = new PooledSequentialFileReader(path);
            var output = new byte[8];
            long chunkOffset = -1;

            Assert.Equal(fileLength, reader.Length);

            var bytesRead = reader.Read(fileLength - output.Length, output.Length, (chunk, _) =>
            {
                chunkOffset = chunk.Offset;
                chunk.Data.CopyTo(output);
            });

            Assert.Equal(output.Length, bytesRead);
            Assert.Equal(fileLength - output.Length, chunkOffset);
            Assert.Equal([0, 0, 0, 0, .. "tail"u8.ToArray()], output);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReturnsBufferWhenCancelledBetweenChunks()
    {
        var path = CreateFile(new byte[SequentialReadOptions.MinimumBufferSize + 1]);
        var pool = new TrackingArrayPool(SequentialReadOptions.MinimumBufferSize);
        using var cancellation = new CancellationTokenSource();
        try
        {
            using var reader = new PooledSequentialFileReader(
                path,
                new SequentialReadOptions(SequentialReadOptions.MinimumBufferSize),
                pool);

            Assert.Throws<OperationCanceledException>(() => reader.Read((_, _) => cancellation.Cancel(), cancellation.Token));
            Assert.Equal(1, pool.RentCount);
            Assert.Equal(1, pool.ReturnCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReturnsBufferWhenHandlerThrows()
    {
        var path = CreateFile([1]);
        var pool = new TrackingArrayPool(SequentialReadOptions.MinimumBufferSize);
        try
        {
            using var reader = new PooledSequentialFileReader(
                path,
                new SequentialReadOptions(SequentialReadOptions.MinimumBufferSize),
                pool);

            Assert.Throws<InvalidDataException>(() => reader.Read(static (_, _) => throw new InvalidDataException()));
            Assert.Equal(1, pool.RentCount);
            Assert.Equal(1, pool.ReturnCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RejectsNestedReadsOnTheSameReader()
    {
        var path = CreateFile([1]);
        try
        {
            using var reader = new PooledSequentialFileReader(path);

            reader.Read((_, _) => Assert.Throws<InvalidOperationException>(() => reader.Read(static (_, _) => { })));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RejectsRangesOutsideTheSourceSnapshot()
    {
        var path = CreateFile([1, 2, 3]);
        try
        {
            using var reader = new PooledSequentialFileReader(path);
            SequentialChunkHandler handler = static (_, _) => { };

            Assert.Throws<ArgumentOutOfRangeException>(() => reader.Read(-1, 1, handler));
            Assert.Throws<ArgumentOutOfRangeException>(() => reader.Read(4, 0, handler));
            Assert.Throws<ArgumentOutOfRangeException>(() => reader.Read(2, 2, handler));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RejectsReadsAfterDisposal()
    {
        var path = CreateFile([1]);
        try
        {
            var reader = new PooledSequentialFileReader(path);
            reader.Dispose();

            Assert.Throws<ObjectDisposedException>(() => reader.Read(static (_, _) => { }));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] CreatePattern(int length)
    {
        var data = GC.AllocateUninitializedArray<byte>(length);
        for (var index = 0; index < data.Length; index++)
        {
            data[index] = (byte)(index * 31 + 7);
        }

        return data;
    }

    private static string CreateFile(byte[] contents)
    {
        var path = Path.GetTempFileName();
        File.WriteAllBytes(path, contents);
        return path;
    }

    private sealed class TrackingArrayPool(int bufferSize) : ArrayPool<byte>
    {
        private readonly byte[] _buffer = new byte[bufferSize];
        private bool _rented;

        public int RentCount { get; private set; }

        public int ReturnCount { get; private set; }

        public override byte[] Rent(int minimumLength)
        {
            Assert.False(_rented);
            Assert.True(minimumLength <= _buffer.Length);
            _rented = true;
            RentCount++;
            return _buffer;
        }

        public override void Return(byte[] array, bool clearArray = false)
        {
            Assert.Same(_buffer, array);
            Assert.True(_rented);
            _rented = false;
            ReturnCount++;
        }
    }
}
