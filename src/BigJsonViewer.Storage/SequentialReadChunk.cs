namespace BigJsonViewer.Storage;

public readonly record struct SequentialReadChunk(long Offset, ReadOnlyMemory<byte> Data)
{
    public int Length => Data.Length;

    public long EndOffset => checked(Offset + Data.Length);
}
