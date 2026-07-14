# Corpus generator

`BigJsonViewer.CorpusGenerator` creates deterministic JSON and adversarial byte streams for benchmarks, correctness tests, and manual profiling. It writes incrementally through a pooled 1 MiB buffer, so generating a file does not require memory proportional to its size.

## Usage

```shell
dotnet run --project tools/BigJsonViewer.CorpusGenerator -- \
  --scenario wide-array \
  --size 10GiB \
  --seed 20260714 \
  --output E:/BigJsonData/wide-10gib.json
```

Run with `--help` to list every option. Existing outputs are protected unless `--force` is provided. Generation happens through an adjacent `.partial` file that is removed on cancellation or failure and renamed only after successful completion.

Sizes accept exact bytes or decimal and binary suffixes:

```text
500MB   2GB   1.5GB
512MiB  2GiB  1TiB
```

The target must be at least 512 bytes. All scenarios except `minified` produce exactly the requested byte count. `minified` finishes its current JSON record and can exceed the target by at most one small record.

## Scenarios

| Name | Purpose | Valid JSON |
|---|---|---:|
| `wide-array` | Large arrays of deterministic objects with mixed scalar fields | Yes |
| `deep-object` | Nested containers with a wide leaf array | Yes |
| `json-lines` | Independent object records separated by newlines | Multiple top-level values |
| `minified` | Dense JSON without insignificant whitespace | Yes |
| `large-string` | A single scalar token spanning most of the file | Yes |
| `whitespace` | Very large insignificant-whitespace region | Yes |
| `escaped-strings` | Quotes, backslashes, newlines, and Unicode escapes | Yes |
| `invalid-utf8` | An invalid UTF-8 byte inside a JSON string | No |
| `truncated` | End-of-file inside an unterminated string/container | No |
| `malformed` | A deterministic syntax error inside an otherwise structured file | No |

## Reproducibility

The same scenario, target size, seed, depth, marker, and marker interval produce identical corpus bytes. The generator uses its own fixed SplitMix64 implementation rather than `System.Random`, avoiding runtime-version-dependent random sequences.

Each successful generation writes `<output>.manifest.json` by default. The manifest records:

- scenario;
- requested and actual bytes;
- seed;
- marker text;
- total marker count and recorded byte offsets.

Pass `--no-manifest` when measuring only generation throughput. Manifest creation never scans the generated corpus again. To preserve bounded memory under pathological marker intervals, recorded offsets are capped at one million; `markerCount` and `markerOffsetsComplete` indicate whether the manifest contains every offset.

## Search markers

The default marker is:

```text
BIGJSONVIEWER_SEARCH_TARGET
```

Record-oriented scenarios insert it every 10,000 records, including record zero. Change the interval with `--marker-every` or the ASCII marker text with `--marker`. Marker offsets are captured while writing, so later search benchmarks have known expected results without a preparatory scan.

## Examples

Generate a 1 GiB JSON Lines corpus:

```shell
dotnet run --project tools/BigJsonViewer.CorpusGenerator -- \
  -s json-lines --size 1GiB -o E:/BigJsonData/events.jsonl
```

Generate a deeply nested 256 MiB document:

```shell
dotnet run --project tools/BigJsonViewer.CorpusGenerator -- \
  -s deep-object --depth 2048 --size 256MiB -o E:/BigJsonData/deep.json
```

Generate a deliberately truncated file:

```shell
dotnet run --project tools/BigJsonViewer.CorpusGenerator -- \
  -s truncated --size 100MB -o E:/BigJsonData/truncated.json
```
