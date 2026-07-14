# Performance benchmarks

The benchmark harness measures the decisions that directly affect large-file performance. It uses BenchmarkDotNet so process isolation, warmup, iteration counts, runtime details, statistics, and allocation measurements are recorded consistently.

## Benchmark groups

| Class | Comparison |
|---|---|
| `SequentialReadBenchmarks` | sequential `FileStream`, positional `RandomAccess`, and memory-mapped stream reads |
| `RandomAccessBenchmarks` | positional, memory-mapped, warm window-cache, and cache-thrashing reads at deterministic offsets |
| `StructuralScanBenchmarks` | scalar JSON structural scanning versus `SearchValues<byte>` candidate scanning |
| `Utf8DecodeBenchmarks` | strict UTF-8 validation/counting versus decoding into a reusable buffer |
| `IndexEncodingBenchmarks` | fixed-width 64-bit offsets versus delta-varint encoding |

The structural scanner and index encoders live in a dependency-free benchmark-kernel project. Unit tests verify scanner equivalence across every chunk boundary and fixed/varint round trips before performance results are considered.

## Quick validation

List available benchmarks:

```shell
dotnet run --project benchmarks/BigJsonViewer.Benchmarks \
  --configuration Release -- --list flat
```

Run every setup, parameter combination, and cleanup once using small fixtures:

```powershell
$env:BIGJSONVIEWER_BENCHMARK_SIZE = '2MiB'
$env:BIGJSONVIEWER_BENCHMARK_MEMORY_SIZE = '1MiB'
dotnet run --project benchmarks/BigJsonViewer.Benchmarks `
  --configuration Release -- --job Dry --filter '*'
```

Dry-job numbers are not performance results. They only prove that the complete harness executes.

## Normal runs

Without configuration, each benchmark class generates a temporary deterministic 64 MiB wide-array corpus. File benchmarks read the complete corpus. Scanner and UTF-8 benchmarks load at most a 16 MiB prefix.

```shell
dotnet run --project benchmarks/BigJsonViewer.Benchmarks \
  --configuration Release -- --filter '*StructuralScanBenchmarks*'
```

BenchmarkDotNet writes CSV, HTML, JSON, and GitHub-flavored Markdown reports beneath `BenchmarkDotNet.Artifacts/results`. The directory is ignored by Git.

Every run also writes `environment.json` and `environment.md` beside the results. Benchmark child processes capture peak working set and managed heap under `process-metrics`. Reports include a logical `MiB/s` column in addition to mean, p95, and allocation statistics.

## Cross-platform workflow

The **Performance benchmarks** GitHub Actions workflow runs a small Short profile when benchmark code changes. It can also be started manually with a 1 GiB or 10 GiB corpus, a Short or Medium job, and storage/scanning/indexing filters. Each Windows, Linux, and macOS job uploads a self-contained report artifact for 14 days.

The harness also accepts `--group all`, `--group storage`, `--group scanning`, or `--group indexing`. Group expansion happens inside the .NET process so wildcard filters behave identically across shells and operating systems.

Hosted-runner results verify portability and expose large differences between candidate approaches. They do not enforce absolute timing thresholds because runner hardware and background load are not stable. See [initial performance budgets](PERFORMANCE_BUDGETS.md) for the reference profiles and acceptance criteria.

## Large storage runs

Generate the corpus outside the repository:

```powershell
dotnet run --project tools/BigJsonViewer.CorpusGenerator `
  --configuration Release -- `
  --scenario wide-array `
  --size 10GiB `
  --output E:/BigJsonData/wide-10gib.json
```

Point the benchmark harness at it:

```powershell
$env:BIGJSONVIEWER_BENCHMARK_FILE = 'E:/BigJsonData/wide-10gib.json'
$env:BIGJSONVIEWER_BENCHMARK_MEMORY_SIZE = '64MiB'
dotnet run --project benchmarks/BigJsonViewer.Benchmarks `
  --configuration Release -- `
  --filter '*SequentialReadBenchmarks*' '*RandomAccessBenchmarks*'
```

Environment variables:

| Variable | Default | Meaning |
|---|---:|---|
| `BIGJSONVIEWER_BENCHMARK_FILE` | generated fixture | Existing corpus used by file and memory benchmarks |
| `BIGJSONVIEWER_BENCHMARK_SIZE` | `64MiB` | Size of an automatically generated fixture |
| `BIGJSONVIEWER_BENCHMARK_MEMORY_SIZE` | `16MiB` | Maximum prefix loaded by in-memory benchmarks |
| `BIGJSONVIEWER_BENCHMARK_CACHE_STATE` | `not-provided` | Declared cold/warm cache condition recorded with results |
| `BIGJSONVIEWER_BENCHMARK_STORAGE` | `not-provided` | Storage device/interface label recorded with results |
| `BIGJSONVIEWER_BENCHMARK_COMMIT` | `GITHUB_SHA` or `not-provided` | Source commit associated with the run |
| `BIGJSONVIEWER_BENCHMARK_WORKTREE` | `not-provided` | Declared clean/dirty working-tree state |
| `BIGJSONVIEWER_BENCHMARK_POWER_MODE` | `not-provided` | Power configuration associated with the run |
| `BIGJSONVIEWER_BENCHMARK_BACKGROUND_ACTIVITY` | `not-provided` | Significant or uncontrolled background load |

The configured file must be at least as large as the selected random-access window. The in-memory limit cannot exceed the .NET array-length limit.

## Capturing meaningful results

Record the following alongside each committed or shared report:

- commit SHA and working-tree status;
- operating system and .NET runtime;
- CPU model and available memory;
- storage device, interface, filesystem, and free space;
- corpus scenario and size;
- whether the filesystem cache was warm or cold;
- power mode and significant background activity;
- exact BenchmarkDotNet command and environment variables.

Do not compare a cold first read with a warm repeated read. BenchmarkDotNet repetitions normally exercise the operating-system cache. Cold-storage measurements need a separately documented cache-reset procedure appropriate to the operating system; restarting only the benchmark process does not clear the filesystem cache.

## Result interpretation

- Use throughput and p95 together; a high mean with unstable tail latency is unsuitable for interactive navigation.
- Treat allocations in benchmark setup as irrelevant, but investigate allocations reported per operation.
- Compare index bytes written as well as encoding time. Delta encoding that saves little space may not justify extra decode work.
- Run storage benchmarks on every supported operating system because mapping and readahead behavior differ.
- Do not choose a production implementation from dry jobs or a single machine.
