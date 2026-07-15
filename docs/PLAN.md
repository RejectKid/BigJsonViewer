# Step-by-step implementation plan

Each milestone ends with measurements and a runnable build. Performance budgets are treated as acceptance criteria, not cleanup work.

## 0. Repository foundation — complete

1. Create the .NET 10 solution and Avalonia desktop application.
2. Separate core, storage, indexing, search, and UI projects.
3. Enable nullable analysis, warnings-as-errors, deterministic builds, compiled bindings, and Native AOT compatibility.
4. Add cross-platform CI, tests, dependency updates, and tagged Native AOT releases.

Exit condition: regular builds and tests pass on Windows, Linux, and macOS; release tags produce six archives.

## 1. Performance corpus and benchmark harness — complete

- [x] Add a deterministic corpus generator rather than committing giant fixtures.
- [x] Generate representative documents: deeply nested objects, billion-style wide arrays at reduced test scale, JSON Lines, minified data, large whitespace runs, escaped strings, invalid UTF-8, truncation, and very large scalar tokens.
- [x] Add BenchmarkDotNet projects for sequential throughput, positional reads, mapped-window access, structural scanning, UTF-8 decoding, and index encoding.
- [x] Capture hardware, storage type, OS, cold/warm cache state, throughput, allocations, working set, and p95 latency.
- [x] Establish initial budgets for 1 GB, 10 GB, and larger sparse/generated files.

Exit condition: benchmark results identify the preferred window size, buffer size, and initial structural scanning strategy on all three operating systems.

## 2. Source storage layer — usable implementation complete

- [x] Implement pooled sequential reading with configurable 4–32 MB buffers.
- [x] Implement a bounded random-access window cache using positional reads.
- [x] Prototype bounded memory-mapped views and compare them with positional reads.
- [x] Detect source identity using length, modification time, and sampled content hashes.
- [x] Detect BOM, probable encoding, JSON/JSON Lines mode, sparse files, and compressed inputs.
- [x] Detect modification or replacement while a file is open and transition the session safely to stale state.

Exit condition: stable memory usage while scanning files larger than RAM, with correct reads across every buffer/window boundary.

## 3. Structural scanner — usable implementation complete

- [x] Keep the benchmarked scalar/reference scanner and implement the production span-streaming structural state machine.
- [x] Maintain quotes, escapes, grammar, primitive-number/literal, and container state across every buffer boundary.
- [x] Record container, property-name, scalar, parent, child-count, first-child, and subtree ranges.
- [x] Represent giant tokens as 64-bit source ranges without contiguous copies.
- [x] Report actionable absolute byte offsets for structural failures.
- [x] Test malformed input and tokens crossing the 4 MiB production boundary.
- [ ] Add explicit SIMD only if full indexing benchmarks show a repeatable cross-platform win over the current storage-bound scan.

Exit condition: scanner correctness matches the reference implementation, memory remains bounded, and throughput approaches available storage bandwidth on representative data.

## 4. Persistent `.bjx` index — usable version 3 complete

- [x] Compare fixed and delta-varint representations and ship compact scalar records with 64-node checkpoints.
- [x] Define a checksummed versioned header containing source identity, format, node count, checkpoints, and build state.
- [x] Use checksummed fixed records only for patchable containers and compact varints for scalar nodes.
- [x] Publish atomically only after a second source-identity sample; clean temporary state after cancellation/failure.
- [x] Jump between direct children using pre-order subtree ends rather than heavyweight sibling records.
- [x] Implement a read-only reader over a bounded 4 MiB page cache.
- [x] Measure the dense 658,402-node fixture: about 15 MiB versus 52.7 MiB for the original fixed prototype.

Exit condition: reopening an indexed document is fast, interrupted builds never masquerade as complete, and index overhead meets the selected size budget.

## 5. Virtual tree MVP — complete

- [x] Add file open, recent files, drag-and-drop, atomic indexing progress, and session lifecycle.
- [x] Implement the flat visible-row model with expand, collapse, and 250-child pages.
- [x] Render key/index, type, compact value preview, child count, loading, and error state.
- [x] Retain native keyboard list navigation and add JSON Pointer copy.
- [x] Use the bounded source cache for previews and cancel superseded row work.
- [x] Interaction-test a 658,402-node synthetic index without constructing all controls.

Exit condition: scrolling and interaction remain responsive while indexing and preview decoding run concurrently.

## 6. Search MVP — complete

- [x] Implement cancellable sequential UTF-8 search that follows storage bandwidth without whole-file decoding.
- [x] Carry lexical escape/container state and classify property names, string values, and outside-string matches.
- [x] Restrict searches to the selected subtree's exact source range.
- [x] Stream bounded progress batches into a temporary disk-backed result store.
- [x] Page 500 result view models at a time and navigate to bounded raw previews.
- [x] Delete result stores when replaced or closed; do not retain implicit query caches.

Exit condition: first results appear quickly, cancellation is prompt, the UI stays responsive, and match count does not control heap usage.

## 7. Table and raw-source views — usable implementation complete

- [x] Sample at most 50 array rows and 32 object cells without constructing a whole table model.
- [x] Add a raw-source viewport around selections and search matches.
- [x] Add bounded pretty-printing with raw fallback.
- [x] Stream exact subtree export in 1 MiB chunks.
- [x] Inspect exceptionally large strings through bounded source previews.

Exit condition: table, tree, and raw views share node identities and selection without duplicating document data.

## 8. Hardening and observability — release baseline complete

- [x] Surface source/index cache hit/miss diagnostics and coarse indexing/search progress.
- [x] Store indexes outside source directories so read-only sources are supported.
- [x] Enforce explicit depth, preview, cache, child-page, result-page, table, and query limits.
- [x] Validate headers, checkpoints, records, parent chains, truncation, cancellation, and corrupt sidecars.
- [x] Exercise repeated open, expand, search, cancel, and close workflows in the rendered Windows app.
- [x] Publish Native AOT PR previews on Windows, Linux, and macOS.
- [ ] Expand hardware lab coverage for removable/network/low-disk scenarios as those environments become available.

Exit condition: corrupted or hostile inputs produce bounded, actionable failures without hangs or runaway allocation.

## 9. Product-quality releases — portable release complete

- [x] Produce portable Native AOT archives for six OS/architecture targets.
- [x] Generate release SHA-256 checksums.
- [x] Keep telemetry/crash uploads absent by default.
- [x] Document index compatibility, privacy, troubleshooting, performance expectations, and unsupported inputs.
- [x] Define semantic-versioning rules and a release checklist.
- [ ] Add signed installers, macOS notarization, AppImage/distro packages, and SBOM attestation when release-owner credentials and distribution identities are available.

Exit condition: signed, reproducible release candidates install and update cleanly on supported systems.

## Initial performance targets

These targets should be revised after milestone 1 establishes real baselines:

- Idle UI remains responsive regardless of source size.
- Managed heap stays within a configurable budget and does not scale linearly with source size.
- Indexed node expansion has a warm-cache p95 below 50 ms.
- Search results are delivered in batches with first-result latency below one second when matches occur near the beginning of a local file.
- Cancellation is observed within 250 ms outside an uninterruptible operating-system read.
- No UI collection contains all nodes or all search matches.
