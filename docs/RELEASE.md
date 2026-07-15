# Release checklist

## Pull-request preview

Every pull request publishes runnable Native AOT artifacts for Windows x64, Linux x64, and macOS Arm64. These are unsigned engineering previews retained for seven days.

Before merging a release candidate:

1. Require all three build/test/format jobs.
2. Require all three Native AOT preview jobs.
3. Require the Windows/Linux/macOS performance jobs when storage, scanning, indexing, or search code changed.
4. Open representative JSON, JSON Lines, malformed, giant-token, and wide-array corpora.
5. Verify reopen, expand/collapse, result paging, cancellation, stale-source handling, pointer copy, and subtree export.
6. Inspect managed allocations, source/index cache diagnostics, sidecar size, and peak working set.

## Tagged release

Tags use semantic versions such as `v0.1.0`. The release workflow builds six self-contained Native AOT archives and publishes `SHA256SUMS.txt`.

```shell
git tag -s v0.1.0 -m "BigJsonViewer 0.1.0"
git push origin v0.1.0
```

Versioning rules:

- **major:** intentional user-facing or `.bjx` compatibility policy break;
- **minor:** backward-compatible viewer/search features;
- **patch:** fixes and performance improvements without user workflow changes.

`.bjx` is explicitly rebuildable and versioned independently inside its header. An index-layout change does not by itself require a product major version.

## Signing and distribution

Portable archives are usable without repository-held signing secrets. Windows Authenticode certificates, Apple Developer ID/notarization credentials, and any package-store identities belong to the release owner and must be configured as protected environment secrets before signed installers can be produced.

Do not add telemetry or crash uploads as part of packaging. Any future reporting must be an explicit, documented opt-in with a preview of the transmitted data.
