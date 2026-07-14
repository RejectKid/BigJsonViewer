using System.Buffers.Binary;

namespace BigJsonViewer.BenchmarkKernels;

public static class IndexEncoder
{
    public static int EncodeFixed64(ReadOnlySpan<long> offsets, Span<byte> destination)
    {
        var required = checked(offsets.Length * sizeof(long));
        if (destination.Length < required)
        {
            throw new ArgumentException("Destination is too small.", nameof(destination));
        }

        for (var index = 0; index < offsets.Length; index++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(destination[(index * sizeof(long))..], offsets[index]);
        }

        return required;
    }

    public static int DecodeFixed64(ReadOnlySpan<byte> source, Span<long> destination)
    {
        var required = checked(destination.Length * sizeof(long));
        if (source.Length < required)
        {
            throw new ArgumentException("Source does not contain enough encoded offsets.", nameof(source));
        }

        for (var index = 0; index < destination.Length; index++)
        {
            destination[index] = BinaryPrimitives.ReadInt64LittleEndian(source[(index * sizeof(long))..]);
        }

        return required;
    }

    public static int EncodeDeltaVarint(ReadOnlySpan<long> offsets, Span<byte> destination)
    {
        long previous = 0;
        var written = 0;
        foreach (var offset in offsets)
        {
            if (offset < previous)
            {
                throw new ArgumentException("Offsets must be sorted in ascending order.", nameof(offsets));
            }

            var delta = (ulong)(offset - previous);
            do
            {
                if (written >= destination.Length)
                {
                    throw new ArgumentException("Destination is too small.", nameof(destination));
                }

                var next = (byte)(delta & 0x7F);
                delta >>= 7;
                destination[written++] = delta == 0 ? next : (byte)(next | 0x80);
            }
            while (delta != 0);

            previous = offset;
        }

        return written;
    }

    public static int DecodeDeltaVarint(ReadOnlySpan<byte> source, Span<long> destination)
    {
        var consumed = 0;
        long previous = 0;
        for (var index = 0; index < destination.Length; index++)
        {
            ulong delta = 0;
            var shift = 0;
            while (true)
            {
                if (consumed >= source.Length || shift >= 64)
                {
                    throw new ArgumentException("Source contains a truncated or invalid varint.", nameof(source));
                }

                var next = source[consumed++];
                if (shift == 63 && (next & 0xFE) != 0)
                {
                    throw new ArgumentException("Source contains an overflowing varint.", nameof(source));
                }

                delta |= (ulong)(next & 0x7F) << shift;
                if ((next & 0x80) == 0)
                {
                    break;
                }

                shift += 7;
            }

            var offset = checked(previous + (long)delta);
            destination[index] = offset;
            previous = offset;
        }

        return consumed;
    }
}
