# Step-by-step implementation plan

Each milestone ends with measurements and a runnable build. Performance budgets are treated as acceptance criteria, not cleanup work.

## 0. Repository foundation — complete

1. Create the .NET 10 solution and Avalonia desktop application.
2. Separate core, storage, indexing, search, and UI projects.
3. Enable nullable analysis, warnings-as-errors, deterministic builds, compiled bindings, and Native AOT compatibility.
4. Add cross-platform CI, tests, dependency updates, and tagged Native AOT releases.

Exit condition: regular builds and tests pass on Windows, Linux, and macOS; release tags produce six archives.

## 1. Performance corpus and benchmark harness

- [x] Add a deterministic corpus generator rather than committing giant fixtures.
- [x] Generate representative documents: deeply nested objects, billion-style wide arrays at reduced test scale, JSON Lines, minified data, large whitespace runs, escaped strings, invalid UTF-8, truncation, and very large scalar tokens.
- [ ] Add BenchmarkDotNet projects for sequential throughput, positional reads, mapped-window access, structural scanning, UTF-8 decoding, and index encoding.
- [ ] Capture hardware, storage type, OS, cold/warm cache state, throughput, allocations, working set, and p95 latency.
- [ ] Establish initial budgets for 1 GB, 10 GB, and larger sparse/generated files.

Exit condition: benchmark results identify the preferred window size, buffer size, and initial structural scanning strategy on all three operating systems.

## 2. Source storage layer

1. Implement pooled sequential reading with configurable 4–32 MB buffers.
2. Implement a bounded random-access window cache using positional reads.
3. Prototype memory-mapped windows and compare them with positional reads.
4. Detect source identity using length, modification time, and sampled content hashes.
5. Detect BOM, probable encoding, JSON/JSON Lines mode, sparse files, and compressed inputs.
6. Detect modification or replacement while a file is open and transition the session safely to stale state.

Exit condition: stable memory usage while scanning files larger than RAM, with correct reads across every buffer/window boundary.

## 3. Structural scanner

1. Implement a scalar reference scanner for quotes, escapes, braces, brackets, commas, colons, and whitespace.
2. Maintain lexical state across buffer boundaries.
3. Add optimized span-based scanning using framework-vectorized primitives; use explicit SIMD only when benchmarks show a win.
4. Record container boundaries, property-name ranges, scalar ranges, parent relationships, child counts, and validation errors.
5. Handle giant tokens as source ranges without copying them into a contiguous managed buffer.
6. Add `Utf8JsonReader` validation for bounded tokens and compare error locations against the reference scanner.
7. Fuzz chunk boundaries and malformed inputs.

Exit condition: scanner correctness matches the reference implementation, memory remains bounded, and throughput approaches available storage bandwidth on representative data.

## 4. Persistent `.bjx` index

1. Prototype fixed-record, delta-encoded, and checkpointed representations.
2. Define a versioned header containing source identity, format version, feature flags, and build state.
3. Store pages append-only with checksums and committed high-water marks.
4. Support recovery or safe rebuild after cancellation, crash, disk-full, and source changes.
5. Use adaptive child checkpoints for enormous arrays/objects rather than a heavyweight record per value.
6. Implement a read-only paged index reader with a bounded cache.
7. Measure index size, build throughput, and random child-access latency.

Exit condition: reopening an indexed document is fast, interrupted builds never masquerade as complete, and index overhead meets the selected size budget.

## 5. Virtual tree MVP

1. Add file open, recent files, drag-and-drop, and session lifecycle.
2. Display indexing progress without blocking navigation already made possible by committed pages.
3. Implement the flat visible-row model with expand, collapse, and paged children.
4. Render key, type, compact value preview, child count, and loading/error state.
5. Add keyboard navigation, JSON Pointer breadcrumbs, copy pointer, and copy bounded value.
6. Add an LRU preview cache and cancel work for rows that leave the viewport.
7. Test million-row synthetic models without constructing a million controls.

Exit condition: scrolling and interaction remain responsive while indexing and preview decoding run concurrently.

## 6. Search MVP

1. Implement UTF-8 byte search with cancellable partitioned workers.
2. Correctly classify matches inside/outside strings and account for escapes.
3. Add property-name, string-value, exact scalar, type, and path-restricted modes.
4. Stream result batches through bounded channels into a disk-backed paged result store.
5. Add result previews and navigation without allocating one view model per match.
6. Cache reusable query results only within explicit disk and memory budgets.

Exit condition: first results appear quickly, cancellation is prompt, the UI stays responsive, and match count does not control heap usage.

## 7. Table and raw-source views

1. Detect arrays of similarly shaped objects using bounded sampling.
2. Add a virtualized table view with user-selectable columns.
3. Add a raw-source viewport around the selected range; never put the whole file into a text editor control.
4. Add bounded pretty-printing for a selected subtree and streamed export for large subtrees.
5. Add a chunked inspector for exceptionally large strings.

Exit condition: table, tree, and raw views share node identities and selection without duplicating document data.

## 8. Hardening and observability

1. Add structured diagnostics for throughput, cache hit rate, queue depth, allocations, and long UI frames.
2. Test network shares, removable drives, read-only locations, low disk space, permission changes, and slow storage.
3. Add explicit limits for nesting depth, preview length, cached pages, outstanding work, and retained results.
4. Fuzz scanner and index reader inputs; treat sidecars as untrusted data.
5. Run soak tests while repeatedly opening, searching, cancelling, and closing large files.
6. Profile Native AOT builds on every supported architecture.

Exit condition: corrupted or hostile inputs produce bounded, actionable failures without hangs or runaway allocation.

## 9. Product-quality releases

1. Add icons, platform metadata, signing, and notarization.
2. Produce Windows installer, macOS app bundle/DMG, and Linux AppImage or distro packages in addition to portable archives.
3. Generate checksums and a software bill of materials.
4. Add crash-reporting only as an explicit privacy-conscious opt-in.
5. Document index compatibility, privacy, troubleshooting, performance expectations, and unsupported cases.
6. Define semantic-versioning rules and a release checklist.

Exit condition: signed, reproducible release candidates install and update cleanly on supported systems.

## Initial performance targets

These targets should be revised after milestone 1 establishes real baselines:

- Idle UI remains responsive regardless of source size.
- Managed heap stays within a configurable budget and does not scale linearly with source size.
- Indexed node expansion has a warm-cache p95 below 50 ms.
- Search results are delivered in batches with first-result latency below one second when matches occur near the beginning of a local file.
- Cancellation is observed within 250 ms outside an uninterruptible operating-system read.
- No UI collection contains all nodes or all search matches.
