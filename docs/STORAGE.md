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

## Bounded random-access windows

`RandomAccessWindowCache` wraps any `IRandomAccessSource` and copies requested ranges into caller-owned memory. Cached pooled buffers remain private, so eviction can never invalidate memory held by a UI preview, scanner, or search result.

The starting configuration uses 1 MiB windows and a 64 MiB capacity. Window size is configurable from 64 KiB to 8 MiB in power-of-two increments, and capacity must be a whole number of windows. Both use 64-bit source offsets.

```csharp
await using var cache = new RandomAccessWindowCache(path);
var bytesRead = await cache.ReadAsync(offset, destination, cancellationToken);
```

Reads may cross any number of window boundaries and stop at the snapshotted end of the source. The cache reserves capacity before loading, evicts the least-recently-used unleased window, and never allows active resident window bytes to exceed the configured budget. Concurrent requests for the same missing window share one source read.

Caller cancellation stops that caller without cancelling a shared load needed by other callers. A window is published only after every expected byte has been read; truncated or failed loads return their buffer and remain retryable. Disposal cancels internal loads, drains active reads, returns every resident buffer, and disposes the wrapped source unless `leaveOpen` was requested.

`Statistics` reports hits, unique misses, coalesced requests, completed loads, evictions, resident bytes/windows, and in-flight loads. These counters are snapshots intended for diagnostics and performance tuning.

## Validation

Sequential-reader tests cover empty files, exact and crossed 4 MiB boundaries, cancellation, callback failures, invalid ranges, disposal, nested-read rejection, and ranged access beyond 10 GiB using a 12 GiB sparse file. Window-cache tests cover warm hits, LRU eviction, concurrent miss coalescing, cancellation, truncated loads, EOF and multi-window boundaries, strict capacity, buffer return, and another 12 GiB sparse source.

The sequential benchmark group compares the production reader with direct `FileStream`, positional, and memory-mapped candidates at 4, 8, and 32 MiB. Random-access benchmarks compare direct positional and mapped reads with warm-cache hits and a deliberately undersized cache working set that forces eviction.
