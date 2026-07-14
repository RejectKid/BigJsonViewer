# Initial performance budgets

These provisional budgets protect the large-file architecture from accidental linear-memory designs and obvious throughput regressions. They are engineering acceptance criteria, not promises for every storage device. Revisit them when production storage and indexing implementations replace the benchmark kernels.

## Reference profiles

| Profile | Corpus | Purpose |
|---|---:|---|
| Pull request | 256 MiB generated wide array | Verify every benchmark and exporter on Windows, Linux, and macOS |
| Baseline | 1 GiB generated wide array | Compare operating systems, APIs, and parameter choices |
| Large | 10 GiB generated wide array | Confirm memory remains bounded and measurements scale with bytes, not file size |
| Extended | 100 GiB sparse/generated input | Local-only validation on a machine with sufficient storage |

GitHub-hosted runners use shared, ephemeral infrastructure. Their absolute timings are retained as evidence but never used as hard pass/fail thresholds. Budget enforcement belongs on documented, dedicated hardware once that runner exists.

## Budgets

| Area | Initial budget |
|---|---|
| Sequential storage | At least 250 MiB/s warm-cache throughput on reference local SSD hardware |
| Structural scan | At least 500 MiB/s and zero managed allocation per operation for the hot loop |
| Random access | Warm-cache p95 below 2 ms for 64 KiB and below 10 ms for 1 MiB windows |
| Memory | Peak working set below 512 MiB for storage benchmarks at both 1 GiB and 10 GiB corpus sizes |
| Heap scaling | Managed heap must not increase in proportion to corpus size; 10 GiB stays within 10% or 32 MiB of the 1 GiB run, whichever is greater |
| Index encoding | Zero managed allocation per operation; delta-varint averages at most 2 bytes per generated offset versus 8 bytes for fixed-width offsets |
| Cancellation | Production reads and scans observe cancellation within 250 ms outside an active operating-system read |

Throughput is calculated from logical bytes processed and BenchmarkDotNet mean time. Tail latency comes from the exported p95 column. Allocation comes from `MemoryDiagnoser`; peak working set and managed-heap snapshots are written under `process-metrics` at benchmark cleanup.

## Initial implementation choices

The first measured Windows run showed that no single storage API wins at every access size. The starting production choices are therefore:

- pooled `FileStream`/positional reads with an 8 MiB sequential buffer;
- positional reads for the bounded random-access cache, starting with 1 MiB windows;
- no memory mapping in the default path, while retaining it as a measured alternative for tiny windows;
- the scalar structural scanner as the correctness and first production baseline;
- delta-varint index offsets, with periodic checkpoints added during persistent-index work.

These are defaults, not permanent conclusions. A candidate replaces one only when the same corpus, cache state, and runner demonstrate a meaningful improvement without breaking the memory or p95 budgets.

## Baseline procedure

1. Run the **Performance benchmarks** workflow with the 1 GiB, Short, and all options.
2. Download all three artifacts and retain their `environment.json`, `environment.md`, `results`, and `process-metrics` directories together.
3. Repeat with 10 GiB when runner free space permits, or run that profile on dedicated hardware.
4. Compare only reports with compatible cache state, corpus, runtime, and storage class.
5. Record any accepted default change in this document and link the supporting workflow run or dedicated-hardware report.
