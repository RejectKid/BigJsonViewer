# BigJsonViewer

BigJsonViewer is a cross-platform desktop viewer designed for JSON files much larger than available memory. The application is being built around bounded-memory storage, persistent structural indexes, virtualized UI rows, and streaming search results.

## Current status

The repository contains the foundation build:

- .NET 10 and Avalonia 12 desktop shell
- Native AOT-compatible project configuration
- Separate core, storage, indexing, and search assemblies
- Initial random-access file abstraction and tests
- Windows, Linux, and macOS CI
- Tagged Native AOT release builds for x64 and Arm64

The indexing format and scanner are deliberately scheduled after the benchmark harness so their design is driven by measurements.

## Requirements

- [.NET SDK 10.0.300 or newer compatible feature band](https://dotnet.microsoft.com/download)
- Windows, macOS, or Linux supported by Avalonia

## Build and run

```shell
dotnet restore BigJsonViewer.slnx
dotnet build BigJsonViewer.slnx
dotnet test BigJsonViewer.slnx
dotnet run --project src/BigJsonViewer.App/BigJsonViewer.App.csproj
```

Generate a deterministic large-file corpus without committing the generated data:

```shell
dotnet run --project tools/BigJsonViewer.CorpusGenerator -- \
  --scenario wide-array \
  --size 10GiB \
  --output E:/BigJsonData/wide-10gib.json
```

See the [corpus generator guide](docs/CORPUS_GENERATOR.md) for scenarios, reproducibility guarantees, and adversarial inputs.

Create a Native AOT build for the current operating system by selecting its runtime identifier:

```shell
dotnet publish src/BigJsonViewer.App/BigJsonViewer.App.csproj \
  --configuration Release \
  --runtime win-x64 \
  --self-contained true \
  -p:PublishAot=true
```

## Releases

Push a semantic version tag to build and publish Native AOT archives:

```shell
git tag v0.1.0
git push origin v0.1.0
```

The release workflow produces archives for Windows, Linux, and macOS on x64 and Arm64.

## Architecture and roadmap

- [Architecture](docs/ARCHITECTURE.md)
- [Step-by-step implementation plan](docs/PLAN.md)
- [Corpus generator](docs/CORPUS_GENERATOR.md)
- [Performance benchmarks](docs/BENCHMARKS.md)
- [Initial performance budgets](docs/PERFORMANCE_BUDGETS.md)
- [Source storage](docs/STORAGE.md)

## Performance principles

- Never build a whole-document DOM.
- Keep memory bounded independently of source-file size.
- Use 64-bit offsets everywhere.
- Render only visible rows.
- Make indexing and search incremental and cancellable.
- Benchmark sequential reads, mappings, parsing, and index representations before committing to a format.
