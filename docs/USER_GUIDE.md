# User guide

## Opening a document

Use **Open JSON**, choose a recent file, or drop a local file onto the window. BigJsonViewer samples the beginning and end of the file, records its length and modification time, detects its format, and either reopens a matching index or builds a new one.

The initial release indexes UTF-8 and UTF-8-with-BOM JSON/JSON Lines. UTF-16/UTF-32 files should be transcoded to UTF-8 first. Gzip, ZIP, and Zstandard signatures are detected, but compressed inputs must be decompressed because useful random navigation is impossible without a seekable decompressed source.

Indexes are stored under the platform local application-data directory, not beside the source. This supports read-only sources and avoids modifying shared folders. A source identity mismatch makes an old index unusable and triggers a rebuild.

## Browsing

The left pane is a flat, virtualized projection of the JSON hierarchy. Expanding a container inserts at most 250 children. **Load more** advances to the next page. Collapsing removes descendants from the visible-row collection.

Selecting a node shows a pretty/raw preview limited to 64 KiB. Large strings and containers are never copied in full merely for display. Arrays also produce a bounded sample of the first 50 rows and first 32 properties per sampled object.

**Copy pointer** places the selected node's RFC 6901 JSON Pointer on the clipboard. Finding an array ordinal may walk that parent's compact child chain, so pointers for extremely late array items can take longer. **Export selection** copies the exact selected source range to a new file in 1 MiB chunks.

## Search

Search scans UTF-8 bytes directly and does not decode the complete document. Modes are:

- **Anywhere** — every byte match;
- **String values** — matches inside strings that are not property names;
- **Property names** — matches inside object keys;
- **Scalars / syntax** — matches outside strings.

Enable **Selected subtree** to restrict scanning to the selected node's source range. Results are appended to a temporary disk store and displayed 500 at a time. **Previous** and **Next** change result pages. Selecting a result opens a bounded raw viewport around its source position.

Search is byte-oriented and case-sensitive. It searches escaped JSON source representation, so a query for a decoded newline does not match the two source bytes `\\n`. Result stores are deleted when replaced or when the window closes.

## Very large files

The design supports offsets beyond 10 GiB, but indexing time and sidecar size depend on structural density rather than only source bytes. A document containing billions of tiny scalar values creates more index data than a document dominated by a few giant strings.

Default bounds:

| Resource | Bound |
|---|---:|
| Source cache | 64 MiB |
| Index cache | 4 MiB |
| Source/index window | 1 MiB / 64 KiB |
| Sequential index buffer | 8 MiB |
| Nesting depth | 4,096 |
| Visible child page | 250 rows |
| Search result page | 500 rows |
| Pretty/raw preview | 64 KiB |
| Table sample | 50 rows × 32 cells |
| Search query | 1 MiB UTF-8 |

Keep enough free space for the `.bjx` index and temporary search results. The compact index uses variable-length scalar records, fixed patchable container records, and one 64-bit checkpoint per 64 nodes. On the deterministic 8 MiB wide-array fixture, this reduced the index from the original fixed-record prototype's 52.7 MB to approximately 15 MB.

## Source changes and privacy

BigJsonViewer does not upload documents, indexes, paths, searches, or diagnostics. Recent paths and indexes remain in local application data. There is no crash reporting or telemetry.

The app periodically resamples an open source. If content, length, modification time, deletion, or replacement is detected, the session becomes stale and further work is cancelled. Reopen the file to create a consistent session.

## Troubleshooting

- **Compressed input:** decompress it to a local seekable file.
- **UTF-16/UTF-32 detected:** transcode to UTF-8.
- **Index rebuilds every time:** confirm the source's modification timestamp is stable and local application data is writable.
- **Indexing stops at a byte offset:** the status contains the structural validation error and absolute byte position.
- **Slow network/removable storage:** copy locally when possible; random previews inherit storage latency.
- **Source changed:** reopen it. Existing results deliberately remain invalid rather than risk mixing file versions.
