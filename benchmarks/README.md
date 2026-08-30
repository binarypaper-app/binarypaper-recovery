# Benchmarks

Two tools, answering two different questions. Neither is a conformance check: the vectors decide
what is correct, and these describe what a build does on a machine.

- **Boundary resources** (below) — can the largest capsule the product writes be recovered on an
  ordinary machine without exhausting it?
- **[Page yield](#page-yield)** — on real photographs, how much does each stage of the reading
  pipeline actually recover, and what does it cost?

# Boundary resource benchmarks

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
| *baseline (3 frames)* | Reed–Solomon | 2 | 1 | 128 | *26 MiB* | *0.3* | *exact* |

Every case recovers to **byte-exact** output. The largest supported backup restores in about five
seconds.

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

`results-windows-x64.json` is the only results file, and the gap is worth naming rather than
leaving as an absence: **there are no Linux or macOS numbers yet.**

The tool can now produce them — it could not before, because it asked .NET for a peak working set,
which is a Windows-only API that throws on Unix rather than returning anything. It now reads the
kernel's own high-water mark from `/proc/<pid>/status` on Linux, and falls back to sampling the
resident set every 50 ms elsewhere. Each results file records which method was used, in
`machine.peakMethod`, because a sampled maximum can miss a spike between samples and is a lower
bound, whereas a high-water mark is not. Compare `peakMethod` before comparing peaks across
platforms.

What is still missing is somewhere to run it. CI cannot: the corpus needs creator-side tooling that
does not exist in this repository, and the corpus itself is too large to commit. So the numbers have
to be produced on a real machine of each kind, or on a Linux container with the corpus mounted, and
the results file committed by hand.

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

**Corpora are not committed and results are not either.** Page photographs are large, and a
photograph of a screen tends to capture more than the page — a file path, a taskbar, whatever else
was on it. Point the tool at your own directory. The JSON it writes records counts and timings but
never the images or their contents.

Numbers vary enormously with capture quality, and that is the finding rather than a nuisance: on a
corpus where the sweep already reads the page, rectification adds nothing at roughly eight times
the cost; on one where the sweep falls short, it is the difference between recovering and not.
