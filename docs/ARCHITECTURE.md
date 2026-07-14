# Architecture

## Dependency direction

```text
BigJsonViewer.App
  ├── BigJsonViewer.Indexing ──┐
  ├── BigJsonViewer.Search ────┼── BigJsonViewer.Storage
  └── BigJsonViewer.Core <─────┴───────────────┘
```

`Core` owns durable concepts such as source ranges and indexed-node descriptions. `Storage` owns sequential and random file access. `Indexing` builds and reads structural sidecars. `Search` produces paged matches. `App` converts those services into a virtualized visible-row model.

The engine assemblies must remain independent of Avalonia so they can be benchmarked and tested without starting a UI.

## Data flow

```text
source JSON
   ├── sequential reader → structural scanner → append-only .bjx index
   └── random-access windows ───────────────────────────────┐
                                                            ↓
memory-mapped/paged index → expanded-node model → visible rows → Avalonia UI
                                                            ↑
source/index scan → bounded result channel → paged results ─┘
```

## Non-negotiable invariants

1. Source and index offsets are signed 64-bit values.
2. Source size does not determine managed heap usage.
3. No operation publishes one UI event per node or search match.
4. Background operations accept cancellation and report coarse-grained progress.
5. A source file is treated as immutable while open; identity changes invalidate its index.
6. Huge strings are represented as ranges and decoded only in bounded previews.
7. Index files are versioned, checksummed in pages, and recoverable after interrupted construction.

## File access

Complete forward passes use large pooled buffers and sequential file hints. Random previews and navigation use fixed-size windows. Both approaches will be benchmarked per operating system; the implementation may choose between buffered positional reads and memory-mapped views based on access pattern.

## UI model

The JSON hierarchy is projected into a flat list of currently visible rows. Expanding a container inserts a page of its children; collapsing it removes visible descendants. Avalonia virtualizes that flat list, so control count follows viewport size rather than document size.

## AOT policy

All production projects declare AOT compatibility. XAML and bindings are compiled. Runtime type scanning, dynamic XAML, reflection-based dependency registration, and non-trimmable packages require explicit design review.

