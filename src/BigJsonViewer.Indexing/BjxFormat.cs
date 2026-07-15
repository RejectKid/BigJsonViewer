using System.Buffers.Binary;
using BigJsonViewer.Core;

namespace BigJsonViewer.Indexing;

internal static class BjxFormat
{
    public const int HeaderSize = 128;
    public const int MaximumRecordSize = 64;
    public const int ContainerRecordSize = 64;
    public const int CheckpointInterval = 64;
    public const int Version = 3;
    public const byte Incomplete = 0;
    public const byte Complete = 1;
    private const byte ContainerFlag = 0x80;
    private static ReadOnlySpan<byte> Magic => "BJXIDX3\0"u8;

    public static byte[] CreateHeader(
        SourceMetadata metadata,
        long nodeCount,
        byte state,
        long checkpointOffset = 0,
        long checkpointCount = 0)
    {
        var header = new byte[HeaderSize];
        Magic.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), Version);
        header[12] = state;
        header[13] = (byte)metadata.Format;
        header[14] = (byte)metadata.Encoding;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), CheckpointInterval);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(24), nodeCount);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(32), metadata.Identity.Length);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(40), metadata.Identity.LastWriteTimeUtcTicks);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(48), metadata.Identity.SampleHash);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(64), checkpointOffset);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(72), checkpointCount);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(120), Checksum64(header.AsSpan(0, 120)));
        return header;
    }

    public static BjxHeader ReadHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length < HeaderSize || !header[..Magic.Length].SequenceEqual(Magic))
        {
            throw new InvalidDataException("The file is not a BigJsonViewer index.");
        }

        var version = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        var checkpointInterval = BinaryPrimitives.ReadInt32LittleEndian(header[16..]);
        if (version != Version || checkpointInterval != CheckpointInterval)
        {
            throw new InvalidDataException($"Unsupported .bjx index version {version}.");
        }

        var expected = BinaryPrimitives.ReadUInt64LittleEndian(header[120..]);
        if (expected != Checksum64(header[..120]))
        {
            throw new InvalidDataException("The .bjx header checksum is invalid.");
        }

        return new BjxHeader(
            header[12] == Complete,
            (JsonDocumentFormat)header[13],
            (SourceEncoding)header[14],
            BinaryPrimitives.ReadInt64LittleEndian(header[24..]),
            new SourceFileIdentity(
                BinaryPrimitives.ReadInt64LittleEndian(header[32..]),
                BinaryPrimitives.ReadInt64LittleEndian(header[40..]),
                BinaryPrimitives.ReadUInt64LittleEndian(header[48..])),
            BinaryPrimitives.ReadInt64LittleEndian(header[64..]),
            BinaryPrimitives.ReadInt64LittleEndian(header[72..]),
            checkpointInterval);
    }

    public static int EncodeNode(IndexedNode node, Span<byte> destination)
    {
        if (destination.Length < MaximumRecordSize)
        {
            throw new ArgumentException("Node destination is too small.", nameof(destination));
        }

        destination[..MaximumRecordSize].Clear();
        if (node.Kind is JsonNodeKind.Document or JsonNodeKind.Object or JsonNodeKind.Array)
        {
            destination[0] = (byte)(ContainerFlag | (byte)node.Kind);
            BinaryPrimitives.WriteInt64LittleEndian(destination[8..], node.ParentId);
            BinaryPrimitives.WriteInt64LittleEndian(destination[16..], node.Range.Offset);
            BinaryPrimitives.WriteInt64LittleEndian(destination[24..], node.Range.Length);
            BinaryPrimitives.WriteInt64LittleEndian(destination[32..], node.NameRange.Offset);
            BinaryPrimitives.WriteInt64LittleEndian(destination[40..], node.NameRange.Length);
            BinaryPrimitives.WriteInt64LittleEndian(destination[48..], node.ChildCount);
            BinaryPrimitives.WriteInt64LittleEndian(destination[56..], node.SubtreeEndId);
            BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], Checksum32(destination[8..ContainerRecordSize]));
            return ContainerRecordSize;
        }

        var written = 0;
        destination[written++] = (byte)node.Kind;
        written += WriteVarUInt64(destination[written..], checked((ulong)(node.Id - node.ParentId)));
        written += WriteVarUInt64(destination[written..], checked((ulong)node.Range.Offset));
        written += WriteVarUInt64(destination[written..], checked((ulong)node.Range.Length));
        var nameDistance = node.NameRange.Length == 0
            ? 0UL
            : checked((ulong)(node.Range.Offset - node.NameRange.Offset) + 1);
        written += WriteVarUInt64(destination[written..], nameDistance);
        written += WriteVarUInt64(destination[written..], checked((ulong)node.NameRange.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(destination[written..], Checksum32(destination[..written]));
        return written + sizeof(uint);
    }

    public static IndexedNode DecodeNode(long id, ReadOnlySpan<byte> record, out int bytesRead)
    {
        if (record.Length < 1)
        {
            throw new InvalidDataException("The .bjx node record is truncated.");
        }

        var kind = (JsonNodeKind)(record[0] & ~ContainerFlag);
        if ((record[0] & ContainerFlag) != 0)
        {
            if (record.Length < ContainerRecordSize ||
                BinaryPrimitives.ReadUInt32LittleEndian(record[4..]) != Checksum32(record[8..ContainerRecordSize]))
            {
                throw new InvalidDataException("A .bjx container record checksum is invalid.");
            }

            bytesRead = ContainerRecordSize;
            var childCount = BinaryPrimitives.ReadInt64LittleEndian(record[48..]);
            return new IndexedNode(
                id,
                BinaryPrimitives.ReadInt64LittleEndian(record[8..]),
                new SourceRange(
                    BinaryPrimitives.ReadInt64LittleEndian(record[16..]),
                    BinaryPrimitives.ReadInt64LittleEndian(record[24..])),
                new SourceRange(
                    BinaryPrimitives.ReadInt64LittleEndian(record[32..]),
                    BinaryPrimitives.ReadInt64LittleEndian(record[40..])),
                childCount,
                kind,
                childCount == 0 ? -1 : checked(id + 1),
                BinaryPrimitives.ReadInt64LittleEndian(record[56..]));
        }

        var consumed = 1;
        var parentDistance = ReadVarUInt64(record, ref consumed);
        var offset = ReadVarUInt64(record, ref consumed);
        var length = ReadVarUInt64(record, ref consumed);
        var nameDistance = ReadVarUInt64(record, ref consumed);
        var nameLength = ReadVarUInt64(record, ref consumed);
        if (record.Length - consumed < sizeof(uint) ||
            BinaryPrimitives.ReadUInt32LittleEndian(record[consumed..]) != Checksum32(record[..consumed]))
        {
            throw new InvalidDataException("A .bjx scalar record checksum is invalid.");
        }

        bytesRead = consumed + sizeof(uint);
        var sourceOffset = checked((long)offset);
        var sourceNameLength = checked((long)nameLength);
        var nameOffset = nameDistance == 0 ? 0 : checked(sourceOffset - (long)nameDistance + 1);
        return new IndexedNode(
            id,
            checked(id - (long)parentDistance),
            new SourceRange(sourceOffset, checked((long)length)),
            new SourceRange(nameOffset, sourceNameLength),
            0,
            kind,
            -1,
            id);
    }

    private static int WriteVarUInt64(Span<byte> destination, ulong value)
    {
        var written = 0;
        while (value >= 0x80)
        {
            destination[written++] = (byte)(value | 0x80);
            value >>= 7;
        }

        destination[written++] = (byte)value;
        return written;
    }

    private static ulong ReadVarUInt64(ReadOnlySpan<byte> source, ref int consumed)
    {
        ulong result = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if ((uint)consumed >= (uint)source.Length)
            {
                throw new InvalidDataException("A .bjx varint is truncated.");
            }

            var value = source[consumed++];
            result |= (ulong)(value & 0x7F) << shift;
            if ((value & 0x80) == 0)
            {
                return result;
            }
        }

        throw new InvalidDataException("A .bjx varint is too large.");
    }

    private static ulong Checksum64(ReadOnlySpan<byte> bytes)
    {
        const ulong offset = 14695981039346656037;
        const ulong prime = 1099511628211;
        var result = offset;
        foreach (var value in bytes)
        {
            result = (result ^ value) * prime;
        }

        return result;
    }

    private static uint Checksum32(ReadOnlySpan<byte> bytes)
    {
        const uint offset = 2166136261;
        const uint prime = 16777619;
        var result = offset;
        foreach (var value in bytes)
        {
            result = (result ^ value) * prime;
        }

        return result;
    }
}

public readonly record struct BjxHeader(
    bool IsComplete,
    JsonDocumentFormat Format,
    SourceEncoding Encoding,
    long NodeCount,
    SourceFileIdentity SourceIdentity,
    long CheckpointOffset,
    long CheckpointCount,
    int CheckpointInterval);
