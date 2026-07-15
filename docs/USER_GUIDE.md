# User guide

## Opening a document

Use **Open JSON**, choose a recent file, or drop a local file onto the window. BigJsonViewer samples the beginning and end of the file, records its length and modification time, detects its format, and either reopens a matching index or builds a new one.

The initial release indexes UTF-8 and UTF-8-with-BOM JSON/JSON Lines. UTF-16/UTF-32 files should be transcoded to UTF-8 first. Gzip, ZIP, and Zstandard signatures are detected, but compressed inputs must be decompressed because useful random navigation is impossible without a seekable decompressed source.

Indexes are stored under the platform local application-data directory, not beside the source. This supports read-only sources and avoids modifying shared folders. A source identity mismatch makes an old index unusable and triggers a rebuild.

## Browsing

The left pane is a flat, virtualized projection of the JSON hierarchy. Expanding a container inserts at most 250 children. **Load more** advances to the next page. Collapsing removes descendants from the visible-row collection.

Selecting a node shows a syntax-colored **Formatted** preview and an exact **Source** preview, both limited to 64 KiB of source. The formatted view decodes readable Unicode escapes, uses relaxed JSON character escaping, and expands string values that themselves contain valid JSON objects or arrays. Expanded embedded values carry a display-only annotation; use Source whenever exact JSON bytes are required. Large strings and containers are never copied in full merely for display. Arrays also produce a bounded, column-inferred sample of the first 50 rows and up to 16 columns. **Export CSV** infers up to 64 columns from the first 100 rows, then streams every array row without constructing the complete table in memory.

**Copy pointer** places the selected node's RFC 6901 JSON Pointer on the clipboard. Finding an array ordinal may walk that parent's compact child chain, so pointers for extremely late array items can take longer. **Export selection** copies the exact selected source range to a new file in 1 MiB chunks.

The workspace toolbar can locate a node by RFC 6901 JSON Pointer or absolute byte offset. The nearest indexed value containing an offset is selected. Click a breadcrumb above the preview to move back to an ancestor. The structure filter limits currently materialized rows; the header controls expand visible containers one level or collapse the tree to its root page.

## Insights and comparison

**Profile** scans the selected subtree's contiguous index records and reports node-type distribution plus frequent property names. Profiles examine at most 100,000 nodes and retain at most 2,048 distinct keys, keeping memory and UI latency bounded.

**Compare** opens or builds the index for a second document, aligns preorder index records, and compares structure, property names, and complete scalar source ranges using bounded buffers. The Insights tab reports totals and the first differences. An insertion near the beginning can shift later preorder IDs, so this is a fast structural comparison rather than a semantic object-key diff.

## Search

Search scans UTF-8 bytes directly and does not decode the complete document. Modes are:

- **Anywhere** — every byte match;
- **String values** — matches inside strings that are not property names;
- **Property names** — matches inside object keys;
- **Scalars / syntax** — matches outside strings.

Enable **Selected subtree** to restrict scanning to the selected node's source range. Results are appended to a temporary disk store and displayed 500 at a time. **Previous** and **Next** change result pages. Selecting a result opens a bounded raw viewport around its source position.

Search is byte-oriented and case-sensitive. It searches escaped JSON source representation, so a query for a decoded newline does not match the two source bytes `\\n`. Result stores are deleted when replaced or when the window closes.

The 20 most recent queries are stored per document and appear in the search-history menu and command palette. The app also restores up to 100 expanded nodes, the selected node, and the active detail tab when the same document is reopened.

## Keyboard shortcuts

| Shortcut | Action |
|---|---|
| `Ctrl+Shift+P` | Open the command palette |
| `Ctrl+O` | Open a document |
| `Ctrl+F` | Focus search |
| `Ctrl+G` | Focus pointer/offset navigation |
| `Ctrl+Shift+Right` | Expand visible nodes one level |
| `Ctrl+Shift+Left` | Collapse all |
| `Ctrl+I` | Profile the selected subtree |
| `Ctrl+D` | Compare another document |
| `Ctrl+Shift+E` | Export the selected array as CSV |

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
| Table sample | 50 rows × 16 inferred columns |
| CSV inference | 100 rows × 64 columns |
| Structure profile | 100,000 nodes / 2,048 tracked keys |
| Restored expanded state | 100 nodes per document |
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
