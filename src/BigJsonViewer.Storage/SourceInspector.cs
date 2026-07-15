using System.Buffers.Binary;
using BigJsonViewer.Core;

namespace BigJsonViewer.Storage;

public static class SourceInspector
{
    private const int SampleSize = 64 * 1024;

    public static async Task<SourceMetadata> InspectAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists)
        {
            throw new FileNotFoundException("The JSON source does not exist.", fullPath);
        }

        var initialLength = info.Length;
        var initialWriteTime = info.LastWriteTimeUtc;
        var firstLength = (int)Math.Min(initialLength, SampleSize);
        var lastLength = (int)Math.Min(Math.Max(0, initialLength - firstLength), SampleSize);
        var first = GC.AllocateUninitializedArray<byte>(firstLength);
        var last = GC.AllocateUninitializedArray<byte>(lastLength);

        await using (var source = new FileSource(fullPath))
        {
            await ReadExactlyAsync(source, 0, first, cancellationToken).ConfigureAwait(false);
            if (lastLength > 0)
            {
                await ReadExactlyAsync(source, initialLength - lastLength, last, cancellationToken).ConfigureAwait(false);
            }
        }

        info.Refresh();
        if (!info.Exists || info.Length != initialLength || info.LastWriteTimeUtc != initialWriteTime)
        {
            throw new IOException("The source changed while its identity was sampled.");
        }

        var encoding = DetectEncoding(first);
        var compression = DetectCompression(first);
        var extension = Path.GetExtension(fullPath);
        var format = compression is not null
            ? JsonDocumentFormat.Compressed
            : extension.Equals(".jsonl", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".ndjson", StringComparison.OrdinalIgnoreCase)
                ? JsonDocumentFormat.JsonLines
                : DetectFormat(first, encoding);
        var identity = new SourceFileIdentity(
            initialLength,
            initialWriteTime.Ticks,
            ComputeSampleHash(first, last, initialLength));

        return new SourceMetadata(
            fullPath,
            identity,
            encoding,
            format,
            IsProbablySparse(info),
            compression);
    }

    private static async Task ReadExactlyAsync(
        IRandomAccessSource source,
        long offset,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < destination.Length)
        {
            var count = await source.ReadAsync(offset + read, destination[read..], cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                throw new EndOfStreamException("The source changed while its identity was sampled.");
            }

            read += count;
        }
    }

    private static SourceEncoding DetectEncoding(ReadOnlySpan<byte> sample)
    {
        if (sample.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            return SourceEncoding.Utf8Bom;
        }

        if (sample.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE, 0x00, 0x00]))
        {
            return SourceEncoding.Utf32LittleEndian;
        }

        if (sample.StartsWith((ReadOnlySpan<byte>)[0x00, 0x00, 0xFE, 0xFF]))
        {
            return SourceEncoding.Utf32BigEndian;
        }

        if (sample.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
        {
            return SourceEncoding.Utf16LittleEndian;
        }

        if (sample.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            return SourceEncoding.Utf16BigEndian;
        }

        if (sample.Length >= 4)
        {
            if (sample[0] != 0 && sample[1] == 0 && sample[2] != 0 && sample[3] == 0)
            {
                return SourceEncoding.Utf16LittleEndian;
            }

            if (sample[0] == 0 && sample[1] != 0 && sample[2] == 0 && sample[3] != 0)
            {
                return SourceEncoding.Utf16BigEndian;
            }
        }

        return SourceEncoding.Utf8;
    }

    private static string? DetectCompression(ReadOnlySpan<byte> sample)
    {
        if (sample.StartsWith((ReadOnlySpan<byte>)[0x1F, 0x8B]))
        {
            return "gzip";
        }

        if (sample.StartsWith((ReadOnlySpan<byte>)[0x50, 0x4B, 0x03, 0x04]))
        {
            return "zip";
        }

        if (sample.StartsWith((ReadOnlySpan<byte>)[0x28, 0xB5, 0x2F, 0xFD]))
        {
            return "zstd";
        }

        return null;
    }

    private static JsonDocumentFormat DetectFormat(ReadOnlySpan<byte> sample, SourceEncoding encoding)
    {
        if (encoding is not SourceEncoding.Utf8 and not SourceEncoding.Utf8Bom)
        {
            return JsonDocumentFormat.Unknown;
        }

        var offset = encoding == SourceEncoding.Utf8Bom ? 3 : 0;
        var depth = 0;
        var inString = false;
        var escaped = false;
        var sawRootEnd = false;
        for (var index = offset; index < sample.Length; index++)
        {
            var value = sample[index];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (value == (byte)'\\')
                {
                    escaped = true;
                }
                else if (value == (byte)'"')
                {
                    inString = false;
                }

                continue;
            }

            if (value == (byte)'"')
            {
                inString = true;
            }
            else if (value is (byte)'{' or (byte)'[')
            {
                if (sawRootEnd && depth == 0)
                {
                    return JsonDocumentFormat.JsonLines;
                }

                depth++;
            }
            else if (value is (byte)'}' or (byte)']')
            {
                depth--;
                sawRootEnd = depth == 0;
            }
            else if (value == (byte)'\n' && sawRootEnd)
            {
                for (var next = index + 1; next < sample.Length; next++)
                {
                    var candidate = sample[next];
                    if (candidate is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
                    {
                        continue;
                    }

                    return candidate is (byte)'{' or (byte)'['
                        ? JsonDocumentFormat.JsonLines
                        : JsonDocumentFormat.Json;
                }
            }
        }

        return JsonDocumentFormat.Json;
    }

    private static ulong ComputeSampleHash(ReadOnlySpan<byte> first, ReadOnlySpan<byte> last, long length)
    {
        const ulong offsetBasis = 14695981039346656037;
        const ulong prime = 1099511628211;
        var hash = offsetBasis;
        foreach (var value in first)
        {
            hash = (hash ^ value) * prime;
        }

        foreach (var value in last)
        {
            hash = (hash ^ value) * prime;
        }

        Span<byte> lengthBytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(lengthBytes, length);
        foreach (var value in lengthBytes)
        {
            hash = (hash ^ value) * prime;
        }

        return hash;
    }

    private static bool IsProbablySparse(FileInfo info)
    {
        return (info.Attributes & FileAttributes.SparseFile) != 0;
    }
}
