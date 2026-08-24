# `binarypaper` — reference recovery tool

The reference implementation of [Capsule Protocol 1.0](../protocol/1.0/SPEC.md).

**This tool is informative, not normative.** It must pass the
[conformance vectors](../vectors), but where it and the specification disagree, the
specification is right and this tool is the thing that changes. That rule exists so a
shared bug can never quietly become the format.

## Build and run

```bash
dotnet test cli/BinaryPaper.Recovery.slnx
dotnet run --project cli/src/BinaryPaper.Recovery.Cli -- --help
```

Requires the .NET 10 SDK. No private feed, no private path, no network access at build
or run time.

## Commands

```text
binarypaper inspect <input>... [--json]
binarypaper recover <input>... --output <directory> [options]
binarypaper verify-vectors <suite-directory> [--json]
binarypaper --version
```

**`inspect`** reports what is readable *before* decryption: the capsule id, erasure
code, symbol counts, and how many codes you have. Nothing it prints comes from inside
the capsule — file names and contents stay sealed. It is what to run when you want to
know whether you have scanned enough.

**`recover`** runs the full pipeline and writes the restored content to a new directory.

**`verify-vectors`** runs a conformance suite and reports pass/fail per vector. This is
how "passes suite X" becomes a claim anyone can check, including someone who does not
trust this binary.

### Inputs

A `.bpq` frame file, or a directory containing them (searched recursively). Repeated
inputs and directory ordering are deterministic: the same inputs always produce the same
result on every platform.

PNG and JPEG page images are the next milestone. **PDF is deliberately out of scope** —
see the [repository README](../README.md#scope).

### Passwords

Read from a hidden interactive prompt, or from standard input with `--password-stdin`.

There is deliberately **no `--password` flag**. Command lines are visible to other
processes on most systems and are recorded in shell history, and a recovery tool that
made that easy would be teaching the wrong habit at the worst moment.

## Exit codes

Stable enough to branch on. Human-readable messages may improve freely; these and the
`--json` shape are the contract.

| Code | Meaning |
| ---: | --- |
| 0 | success |
| 2 | usage error |
| 10 | no BinaryPaper frames found in the input |
| 11 | input could not be decoded |
| 20 | invalid, unsupported, or out-of-profile capsule |
| 21 | capsule or session conflict |
| 22 | not enough codes, or the decode did not complete |
| 30 | refused by resource policy |
| 40 | authentication failed |
| 41 | plaintext integrity check failed |
| 50 | compression or payload package invalid |
| 51 | unsafe or conflicting output destination |
| 60 | local I/O failure |

`--json` output carries the same information as a stable `result` string, so scripts do
not have to parse prose.

## Project layout

```text
cli/
  src/BinaryPaper.Recovery/         the recovery library: one file per pipeline stage
  src/BinaryPaper.Recovery.Codecs/  vendored upstream erasure codecs, kept verbatim
  src/BinaryPaper.Recovery.Cli/     argument parsing and output formatting only
  test/BinaryPaper.Recovery.Tests/  bounds and arithmetic tests
```

The library is platform-neutral; the console project is a thin shell around it. Anything
that decides whether input is valid lives in the library, so it is reachable from tests
and from `verify-vectors` rather than only from a terminal.

### Why the codecs are a separate project

`BinaryPaper.Recovery.Codecs` holds verbatim copies of two standalone codec libraries
whose upstream repositories are not published yet. Keeping them in their own assembly
makes the boundary explicit and means this repository's stricter compiler settings never
force an edit to a file that must stay byte-identical to its upstream. See
[the codec README](src/BinaryPaper.Recovery.Codecs/README.md).

## Design notes worth knowing before reading the code

**Validation order is the architecture.** The pipeline in
[SPEC.md §11](../protocol/1.0/SPEC.md#11-validation-order) is not advice; it is the
structure of `CapsuleRecovery.Recover`. Stage 5 (profile preflight) runs before any
decoder state exists, and stage 8 (authentication) runs before any parser that allocates
on declared values ever sees a byte.

**Nothing allocates on an unvalidated declared value.** Every length and count arriving
from the wire is bounds-checked first. Where a sum could overflow, it is computed in a
wider type or restructured to avoid the addition entirely — `body_len` is a `u64`, so
even `header + body_len` in 64-bit unsigned arithmetic can wrap.

**Failures name a category and a stage.** A rejection at the right stage for the wrong
reason, or the right reason at the wrong stage, is treated as a defect. The stage is what
tells a user whether their pages are damaged, their password is wrong, or the backup was
never valid — three very different next actions.

**Wrong password and tampering share one error.** They are cryptographically
indistinguishable and the tool does not pretend otherwise.

**Output is atomic.** Content is staged in a sibling `.partial-*` directory and moved into
place only once every byte is written, so a failed recovery never leaves something that
looks like a successful one. A user who sees output may throw the paper away.
