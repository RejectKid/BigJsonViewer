# BigJsonViewer

BigJsonViewer is a cross-platform desktop viewer for JSON files that are much larger than available memory. It uses bounded file windows, a persistent compact structural index, a virtualized tree, and disk-paged search results instead of building a whole-document DOM.

## What works

- open, recent-file, and drag-and-drop workflows for UTF-8 JSON and JSON Lines;
- streaming structural validation and indexing across arbitrarily placed read boundaries;
- compact, checksummed `.bjx` indexes tied to sampled source identity;
- a flat virtualized tree with 250-child pages, expand/collapse, scalar previews, and JSON Pointer copy;
- pointer/byte-offset navigation, clickable breadcrumbs, visible-tree filtering, and restored workspace state;
- bounded 64 KiB syntax-colored/readable and exact-source previews, inferred array tables, streamed CSV, and subtree export;
- cancellable UTF-8 search with property-name, string-value, scalar/syntax, and selected-subtree modes;
- disk-backed search results with 500-result UI pages;
- bounded structure profiles, index-aware document comparison, search history, and a keyboard command palette;
- stale-source detection while a document is open;
- Native AOT-compatible builds for Windows, Linux, and macOS on x64 and Arm64.

The default source cache is 64 MiB in 1 MiB windows. Source offsets, node IDs, lengths, and result counts are 64-bit. Tests exercise reads beyond 10 GiB using sparse 12 GiB files.

## Run it

Requirements: .NET SDK 10.0.300 or a compatible newer feature band.

```shell
dotnet restore BigJsonViewer.slnx
dotnet run --project src/BigJsonViewer.App/BigJsonViewer.App.csproj --configuration Release
```

Choose **Open JSON**, select a local `.json`, `.jsonl`, or `.ndjson` file, and let the first index build finish. The compact index is retained beneath the operating system's local application-data directory and reused only while the source identity matches.

See the [user guide](docs/USER_GUIDE.md) for search modes, limits, index storage, and troubleshooting.

## Build and test

```shell
dotnet restore BigJsonViewer.slnx
dotnet format BigJsonViewer.slnx --no-restore --verify-no-changes
dotnet build BigJsonViewer.slnx --configuration Release --no-restore
dotnet test BigJsonViewer.slnx --configuration Release --no-build
```

Generate a deterministic large corpus without committing it:

```shell
dotnet run --project tools/BigJsonViewer.CorpusGenerator -- \
  --scenario wide-array \
  --size 10GiB \
  --output E:/BigJsonData/wide-10gib.json
```

Create a Native AOT build by selecting a runtime identifier:

```shell
dotnet publish src/BigJsonViewer.App/BigJsonViewer.App.csproj \
  --configuration Release \
  --runtime win-x64 \
  --self-contained true \
  -p:PublishAot=true
```

## Releases

Semantic-version tags build portable Native AOT archives for `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, and `osx-arm64`. Each GitHub release includes `SHA256SUMS.txt`.

```shell
git tag v0.1.0
git push origin v0.1.0
```

Portable builds are unsigned. Platform signing, notarization, and installer formats require release-owner certificates and are deliberately not simulated by the repository.

## Documentation

- [User guide](docs/USER_GUIDE.md)
- [Architecture](docs/ARCHITECTURE.md)
- [`.bjx` index format](docs/INDEX_FORMAT.md)
- [Step-by-step implementation plan](docs/PLAN.md)
- [Corpus generator](docs/CORPUS_GENERATOR.md)
- [Performance benchmarks](docs/BENCHMARKS.md)
- [Performance budgets](docs/PERFORMANCE_BUDGETS.md)
- [Source storage](docs/STORAGE.md)
- [Release checklist](docs/RELEASE.md)

## Performance invariants

- Never build a whole-document DOM.
- Keep managed memory bounded independently of source size.
- Use 64-bit source positions everywhere.
- Materialize only visible tree rows and the current result page.
- Represent giant tokens as source ranges.
- Treat source files and indexes as untrusted, mutable external state.
- Make indexing, search, preview, and export cancellable.
