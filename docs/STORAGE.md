# Source storage

The storage layer is designed around files that may be much larger than available memory. It never exposes an API that requires the complete source to fit in a managed array or string.

## Pooled sequential reading

`PooledSequentialFileReader` performs long-offset positional reads through one buffer rented from `ArrayPool<byte>`. The production buffer range is 4–32 MiB and the measured starting default is 8 MiB.

```csharp
using var reader = new PooledSequentialFileReader(path);
var bytesRead = reader.Read((chunk, cancellationToken) =>
{
    scanner.Process(chunk.Offset, chunk.Data.Span);
}, cancellationToken);
```

The memory in `SequentialReadChunk.Data` belongs to the reader and is valid only until the callback returns. Consumers must copy any bytes they need to retain. Parsers should instead preserve compact lexical state and absolute source ranges between callbacks.

Ranged reads use 64-bit offsets and are useful for resuming work or validating distant portions of sparse files:

```csharp
reader.Read(offset, length, ProcessChunk, cancellationToken);
```

One reader supports one active scan at a time. Cancellation is checked before renting, before every operating-system read, and therefore between chunks. An active operating-system read cannot be interrupted by the synchronous API; indexing should run on a background worker.

The buffer is returned without clearing after success, cancellation, or an exception. Callbacks must treat input as read-only and must not retain it. The source length is snapshotted when the reader opens; source identity and live modification detection are later Milestone 2 work.

## Validation

Tests cover empty files, exact and crossed 4 MiB boundaries, cancellation, callback failures, invalid ranges, disposal, nested-read rejection, and ranged access beyond 10 GiB using a 12 GiB sparse file. The sequential benchmark group compares the production reader with direct `FileStream`, positional, and memory-mapped candidates at 4, 8, and 32 MiB.
