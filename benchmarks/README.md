# Benchmarks

Two tools, answering two different questions. Neither is a conformance check: the vectors decide
what is correct, and these describe what a build does on a machine.

- **Boundary resources** (below) — can the largest capsule the product writes be recovered on an
  ordinary machine without exhausting it?
- **[Page yield](#page-yield)** — on real photographs, how much does each stage of the reading
  pipeline actually recover, and what does it cost?

# Boundary resource benchmarks

## Release gate: recipe corpus v2

The current release gate uses [`recipes.json`](recipes.json) and the public
`BinaryPaper.Recovery.Corpus` test generator. It needs only this repository and the
normal .NET dependencies. The generator accepts synthetic recipes, not user files,
and is never included in the recovery executable.

```powershell
dotnet run --project benchmarks/BinaryPaper.Recovery.Corpus -c Release -- benchmarks/recipes.json artifacts/corpus
dotnet publish cli/src/BinaryPaper.Recovery.Cli -c Release -r win-x64 --self-contained true -o artifacts/cli
dotnet run --project benchmarks/BinaryPaper.Recovery.Benchmarks -c Release -- artifacts/corpus artifacts/cli/binarypaper.exe --output artifacts/results-win-x64.json
```

Use `linux-x64` or `osx-arm64` and `binarypaper` without `.exe` on those platforms.
Choose a new corpus directory on each generation. The manifest pins every frame
container and expected output; the harness verifies all output files and hashes.

The numeric corners were selected from 292,698 accepted boundary candidates using
production count arithmetic and codec selection: every even symbol length from 32
through 2928 and integer redundancy percentage from 0 through 100. They cover source
count, repair count, total count, symbol size, stored capacity, and both codec memory
admission bounds. Several dimensions share a shape. The stored-capacity corner is
42,467,328 bytes before packaging overhead, substantially beyond the older anchor cases.

Unlike the historical measurements below, the new corpus removes source symbols:
876 at the Reed–Solomon memory corner, and 6000 in the dedicated LDPC case. The
generator checks that each LDPC erasure pattern is recoverable and records its actual
decode stage and residual width. Additional cases cover encrypted recovery at the
largest stored capacity using the profile's Argon2id cost, a 128 MiB decompressed
output file, and 4096 entries with UTF-8 paths. Passwords are labelled public fixture values
and enter the CLI through stdin; reports never include them.

128 MiB and 4096 entries are explicit measurement points, not protocol ceilings:
decompressed size and entry count have no universal finite profile maximum. The
recipe manifest states the exact coverage instead of claiming exhaustive output sizes.
Image conformance and network-disabled PNG/JPEG drills remain separate gates.

The release workflow regenerates and measures this corpus on Windows x64, Linux x64,
and macOS arm64. It verifies that all three generated manifests are byte-identical,
retains their results, and includes them in the checksum manifest and build attestation.
Each report pins the corpus and measured CLI hashes. Shared runner numbers describe
that runner, not a minimum supported hardware specification.

## Historical anchor measurements (all frames present)

The original five-case corpus and tables below are retained as historical evidence.
They supplied every frame, so they measure ingestion and intact recovery; they do not
measure erasure reconstruction or establish the full supported writer envelope. That
old generator required creator-side tooling. Use the public v2 generator above for
new measurements.

Small conformance vectors answer *"are the bytes and the rejection rules right?"*. These answer a
different question: *"can an implementation recover everything the product actually writes, on an
ordinary machine, without running out of memory?"*

Both matter. They are kept apart because they are different evidence with different lifetimes — the
vectors are frozen with a protocol release, these numbers describe a build on a machine.

## What is measured

Five shapes at the edge of what the production planner will accept, each independently maximising a
different cost dimension:

| Case | Why it exists |
| --- | --- |
| `anchor-guaranteed-size` | the guaranteed-size real plan |
| `anchor-floor-35pct` | stable floor at 35% repair |
| `anchor-floor-50pct` | stable floor at 50% repair |
| `max-source-symbols` | `K` at the calibrated decoder ceiling |
| `max-repair-symbols` | `R` at the LDPC decoder ceiling |

These are **anchors, not the whole envelope**. A calibrated printing profile can admit larger stored
payloads than the published size guarantee, so this corpus is a floor on coverage rather than a
complete enumeration.

Every shape is validated against the real production restore-memory seatbelt before it is generated,
so the corpus is by construction the envelope a creator will emit — not a guess at it.

## Results

`results-windows-x64.json` carries the machine, runtime, and per-case numbers. Latest run,
2026-08-30:

| Case | Codec | K | R | S | Peak RSS | Seconds | Output |
| --- | --- | ---: | ---: | ---: | ---: | ---: | --- |
| `anchor-guaranteed-size` | LDPC | 15924 | 7962 | 314 | 98 MiB | 1.3 | exact |
| `anchor-floor-35pct` | LDPC | 16000 | 5600 | 358 | 92 MiB | 1.1 | exact |
| `anchor-floor-50pct` | LDPC | 16000 | 8000 | 358 | 98 MiB | 1.2 | exact |
| `max-source-symbols` | Reed–Solomon | 16000 | 7 | 128 | 73 MiB | 0.8 | exact |
| `max-repair-symbols` | LDPC | 2000 | 8192 | 128 | 52 MiB | 0.6 | exact |

Both peak and elapsed have come down since the 2026-08-24 run (107 MiB / 3.0 s on the largest
shape). Same shapes, same machine, newer build — but a benchmark is not a controlled experiment,
so treat that as "not worse" rather than as a measured improvement.

`results-linux-x64.json`, 2026-08-30, Ubuntu 24.04 on .NET 10.0.6:

| Case | Codec | K | R | S | Peak RSS | Seconds | Output |
| --- | --- | ---: | ---: | ---: | ---: | ---: | --- |
| `anchor-guaranteed-size` | LDPC | 15924 | 7962 | 314 | 97 MiB | 0.6 | exact |
| `anchor-floor-35pct` | LDPC | 16000 | 5600 | 358 | 108 MiB | 0.5 | exact |
| `anchor-floor-50pct` | LDPC | 16000 | 8000 | 358 | 82 MiB | 0.6 | exact |
| `max-source-symbols` | Reed–Solomon | 16000 | 7 | 128 | 61 MiB | 0.3 | exact |
| `max-repair-symbols` | LDPC | 2000 | 8192 | 128 | 56 MiB | 0.2 | exact |

Every historical shape recovers byte-exact on Linux too, from a self-contained binary.

Two honesty notes about that table. It was measured **in a container** on this Windows machine, so
it is a real Linux kernel, a real glibc build, and a real .NET runtime, but not bare metal — treat
it as evidence the platform works rather than as a hardware benchmark. And the peaks do not order
themselves the same way as on Windows: `anchor-floor-35pct` is the hungriest shape here and the
second-lightest there. That is the garbage collector making different choices with different amounts
of headroom, not a shape behaving differently, and it is the reason these files record a machine
rather than a number.

`results-osx-arm64.json`, 2026-08-31, macOS 26.5.2 on arm64, .NET 10.0.11:

| Case | Codec | K | R | S | Peak RSS | Seconds | Output |
| --- | --- | ---: | ---: | ---: | ---: | ---: | --- |
| `anchor-guaranteed-size` | LDPC | 15924 | 7962 | 314 | 101 MiB | 2.5 | exact |
| `anchor-floor-35pct` | LDPC | 16000 | 5600 | 358 | 88 MiB | 2.1 | exact |
| `anchor-floor-50pct` | LDPC | 16000 | 8000 | 358 | 109 MiB | 2.2 | exact |
| `max-source-symbols` | Reed–Solomon | 16000 | 7 | 128 | 75 MiB | 0.5 | exact |
| `max-repair-symbols` | LDPC | 2000 | 8192 | 128 | 59 MiB | 0.3 | exact |

**Read these peaks as lower bounds.** `peakMethod` is `sampled-resident-set`: macOS offers no
high-water mark the way Windows and Linux do, so the number is the largest resident set seen at a
50 ms sampling interval, and a shorter spike between samples would be missed. The elapsed times are
from a three-processor runner rather than a sixteen-processor desktop, which is most of why they are
slower.

What the three tables establish together is the thing that matters: every shape recovers
**byte-exact** on every platform, from the same source, with no platform-specific handling.
| *baseline (3 frames)* | Reed–Solomon | 2 | 1 | 128 | *26 MiB* | *0.3* | *exact* |

Every historical case recovers to **byte-exact** output. These timings apply to those
intact anchor cases only.

## Reading the numbers honestly

**The baseline row is the important one.** A capsule of three frames and three hundred bytes still
costs 26 MiB, because that is the .NET runtime's own floor for a self-contained binary. Subtract it
before reasoning about any other row: the largest shape costs roughly 80 MiB *of actual recovery
work*, not 108.

**Do not compare these against the seatbelt prediction.** The corpus manifest records a
`predictedPeakRestoreBytes` for each shape, and this tool's peak is three to four times larger. That
is expected and is not a defect in either place:

- the prediction models a **constrained mobile restorer** — a different runtime, a different garbage
  collector, and a deliberately conservative fixed budget;
- this is a desktop .NET process whose GC grows its heap opportunistically when RAM is plentiful.

Peak resident set is "what the process was allowed to hold", not "what it needed". Treating the
mobile budget as a target for a desktop tool would mean optimising against a number that was never
about this program.

**These numbers are evidence, not protocol.** They describe one build on one machine. Nothing in the
specification depends on them, and an implementation is not non-conforming for being slower or
hungrier.

## A fix this measurement produced

The first run peaked at 119 MiB on the largest shape. The session was retaining every frame twice —
once as the extracted symbol, once as the whole raw frame kept only to recognise an exact duplicate.

Frames are now identified by a SHA-256 digest instead, which is 32 bytes rather than up to 65 KB per
frame. SHA-256 and not the frame's own CRC-32C, because this comparison decides whether a
same-index frame is a harmless rescan or a conflict, and an attacker able to find a CRC-32 collision
could make a differing frame pass as a duplicate.

That is the whole point of measuring at the boundary: at three frames the duplication is invisible,
and at twenty-four thousand it is nine megabytes.

## Other platforms

All three published desktop platforms are measured.

The tool could not have produced anything outside Windows until recently: it asked .NET for a peak
working set, which is a Windows-only API that throws on Unix rather than returning a value. It now
reads the kernel's own high-water mark from `/proc/<pid>/status` on Linux, and falls back to
sampling the resident set every 50 ms elsewhere. Each results file records which method was used, in
`machine.peakMethod`, because a sampled maximum can miss a spike between samples and is a lower
bound, whereas a high-water mark is not. **Compare `peakMethod` before comparing peaks across
platforms** — otherwise you are comparing two different quantities.

The historical workflow could not produce these numbers here: its corpus needed creator-side tooling that does
not exist here, and is too large to commit. They are produced elsewhere and the results file is
committed by hand.

The Linux run was done by staging the corpus, the published CLI and the published benchmark inside a
`mcr.microsoft.com/dotnet/runtime-deps:10.0` container — staged *inside*, because reading the corpus
across a bind mount was slow enough to look like a hang. The macOS run was done on a hosted runner,
driven by a workflow in the private creator repository, which is where the corpus generator lives:
it publishes the CLI for `osx-arm64`, measures, and uploads the results file.

## Running it

```bash
# 1. Generate the corpus (needs the creator-side tooling; produces ~40 MB)
#    write-boundary-corpus --output-dir <corpus>
# 2. Publish the CLI for your platform
dotnet publish cli/src/BinaryPaper.Recovery.Cli -c Release -r win-x64 -o <bin>
# 3. Measure
dotnet run --project benchmarks/BinaryPaper.Recovery.Benchmarks -- <corpus> <bin>/binarypaper --output results.json
```

The harness runs the CLI as a **separate process** and samples its working set from outside.
In-process counters would measure the harness as much as the subject, and a runtime that simply has
not collected yet looks identical to one that cannot.

Ordinary shared CI is not a stable memory benchmark. Record the machine with the results, which
`results.json` does automatically.

# Page yield

The conformance vectors are clean synthetic renderings by design, so they cannot answer whether the
reading pipeline earns its cost on a real photograph. This measures four ways of reading the same
images and reports how many distinct codes each recovers:

| Strategy | What it is |
| --- | --- |
| `plain` | the image handed to the decoder as it arrived, one read |
| `plain-both-binarizers` | the same, union of both binarizers |
| `whole-page-sweep` | the reader's first pass: two blur levels x two binarizers |
| `full-pipeline` | the sweep plus rectify-and-retry with lattice prediction |

The gap between the last two is what rectification contributed. It rescues symbols the detector
located but could not read, so its value tracks how much the detector is struggling rather than how
hard the page looks — which is why the reader escalates to it only when the sweep has not produced
enough, rather than always running it.

This tool exists because that gap was once inferred rather than measured. The endpoints were
compared while two things changed at once, and the expensive stage was credited with a gain that
belonged to the cheap one. A claim that a stage earns its place should be re-runnable.

## Running it

```bash
dotnet run --project benchmarks/BinaryPaper.Recovery.PageYield -- <image-directory> --json results.json
```

A corpus is committed: [`page-corpus/`](page-corpus/README.md) holds four photographs of a screen
showing a 35-symbol page, cropped to the page area. They are the only non-synthetic images in the
repository, and the only ones that could have caught a decoder which reads a rendering perfectly and
a photograph not at all — which is a failure that actually happened.

```bash
dotnet run --project benchmarks/BinaryPaper.Recovery.PageYield -- benchmarks/page-corpus
```

Point it at your own directory instead if you have one. Results are not committed: the JSON records
counts and timings for one build on one machine, and a stale file of those is worse than none.

Numbers vary enormously with capture quality, and that is the finding rather than a nuisance: on a
corpus where the sweep already reads the page, rectification adds nothing at roughly eight times
the cost; on one where the sweep falls short, it is the difference between recovering and not.
