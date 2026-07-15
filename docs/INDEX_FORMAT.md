# `.bjx` persistent index

`.bjx` version 3 is an internal, rebuildable sidecar format. It is not a source of truth and must never be trusted without validation.

## Identity and commit state

The 128-byte header contains:

- magic and format version;
- incomplete/complete build state;
- JSON/JSON Lines and source-encoding markers;
- source length, UTC modification ticks, and sampled FNV-1a content hash;
- node count, checkpoint interval, checkpoint-table offset/count;
- a header checksum.

Builds are written to a unique temporary path. The complete header is written and flushed only after structural scanning finishes and the source identity is resampled. The temporary file is atomically moved into place afterward. Cancellation, validation failure, a changed source, or an exception removes the temporary file; an older complete index is not overwritten until success.

## Records

Node IDs are implicit pre-order sequence numbers. Objects, arrays, and the synthetic document root use 64-byte fixed records because their final range, child count, and subtree end are patched when the closing delimiter is observed. Their first child is derivable as `id + 1` when child count is nonzero.

String, number, Boolean, and null nodes use variable records containing unsigned varints for parent distance, absolute source offset, range length, property-name distance, and property-name length. Each record has its own checksum. Giant source ranges therefore remain 64-bit without making ordinary scalar records fixed-width.

Every 64th node has a 64-bit record-offset checkpoint. A lookup reads one checkpoint and decodes at most 63 preceding variable records. The reader keeps index pages in a bounded 4 MiB window cache.

Direct children are found without a per-child pointer: pre-order makes the first child `id + 1`, and `child.SubtreeEndId + 1` jumps to the next sibling. Tree expansion cost follows the requested child page, not the total document node count.

## Compatibility

The index is an implementation cache. A magic/version mismatch, incomplete state, source-identity mismatch, invalid checkpoint, truncated stream, checksum failure, or broken parent chain causes a safe rebuild or an actionable error. New incompatible layouts increment the version; no migration is required because the source JSON remains authoritative.
