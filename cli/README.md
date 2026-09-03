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

Or use a release archive, which needs no SDK — it is one self-contained executable. On
Linux and macOS, unpack it and make it executable first: a zip carries no executable bit,
so `chmod +x binarypaper` is a necessary step, not a workaround.

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

PNG or JPEG page images, raw `.bpq` frame files, or a directory containing them (searched
recursively). Repeated inputs and directory ordering are deterministic: the same inputs
always produce the same result on every platform.

**File type is decided by content, not by extension.** A `.bpq` that actually holds a PNG
is read as a PNG.

A page image that decodes nothing is reported but never stops the run: losing the frames
from twenty good pages because the twenty-first is unreadable would be the wrong behaviour
for a recovery tool.

**PDF is deliberately out of scope** — see the [repository README](../README.md#scope).

### How a page is read

Two passes, and most runs only need the first.

**The sweep.** Every page image is read whole, at two levels of softening and two ways of deciding
black from white. On a scan, and on most photographs, this reads the page and the run ends here.

**The closer look.** If the codes collected still are not enough to recover the capsule, every page
is read again — this time locating each candidate symbol, straightening it as though seen head-on,
retrying it across sampling densities and softening levels, and using symbols that decode to
predict where their neighbours must be. That last part recovers symbols whose own corner markers
were never found, which is what a photograph taken at an angle tends to lose.

The split matters because the second pass is roughly eight times the cost of the first and earns
nothing when the first already read the page. It rescues symbols the detector located but could not
read, so its value tracks how much the detector is struggling rather than how hard the page looks.

**Every pass runs one page at a time, each in its own short-lived child process.** QR payloads
are turned into bytes by a native decoder, and a native decoder handed a damaged image can corrupt
its own memory; when that happens the process is killed outright, with no error to catch. Reading
each page in isolation means such a failure costs that page rather than every page already read. A
page whose child dies is retried once in a fresh process — the failure is not deterministic, and a
new process starts with clean memory — and if it dies again it is reported and skipped so the run
can finish. Since a capsule needs only its source count of codes spread across all pages, one
skipped page is usually free.

### Resource bounds on page images

Every image is untrusted input, so three limits bound the work one page may cause:

| Limit | Default | Flag |
| --- | --- | --- |
| Encoded file size | 128 MB | — |
| Decoded pixels | 200 million (≈1200 dpi A4, with headroom) | `--max-image-pixels` |
| Time spent on one page | 120 seconds | `--max-image-seconds` |

The time bound is the one that needs explaining. Reading a photographed page means
rectifying each located symbol and retrying it across sampling densities, blur levels and
thresholds, stopping at the first clean decode — so the pipeline costs least on pages it
can read and most on pages it cannot. A photograph of something that is not a backup page
is therefore the expensive input, not a rare crafted one, and neither the byte cap nor the
pixel cap bounds it: the most expensive legitimate page we measured is a 1.4 MB file.

For scale, every page size the kit accepts reads out in **5 to 20 seconds** on ordinary
hardware, so the default leaves generous room. When the bound is reached, the page reports
the codes it had already decoded and says it stopped early — those codes are real, and a
capsule's recovery threshold is met across the whole scan rather than per page. Pass
`--max-image-seconds 0` to disable the bound entirely.

While a page is being worked, progress is written to stderr, so a slow page is visibly
alive rather than indistinguishable from a hang.

### Passwords

Read from a hidden interactive prompt, or from standard input with `--password-stdin`.
Standard input is one UTF-8 line (an optional UTF-8 BOM is accepted), independent of
the machine's console code page. Invalid UTF-8 is a usage error. NFC normalization
then follows the capsule specification, so composed and decomposed passwords agree.

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
  src/BinaryPaper.Recovery/            the recovery library: one file per pipeline stage
  src/BinaryPaper.Recovery/Images/     PNG/JPEG decode and multi-QR detection
  src/BinaryPaper.Recovery.Codecs/     vendored upstream erasure codecs, kept verbatim
  src/BinaryPaper.Recovery.Cli/        argument parsing and output formatting only
  test/BinaryPaper.Recovery.Tests/     bounds and arithmetic tests
  test/BinaryPaper.Recovery.ImageVectors/  generates the image fixtures; not shipped
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

**QR payloads are read as bytes, never as text.** A frame payload is arbitrary binary.
`Barcode.Text` applies a character-set interpretation and mangles it, so the payload is
taken from `Barcode.Bytes` and `Text` is not consulted even as a fallback. A frame
corrupted that way would fail its CRC-32C with nothing to suggest the decoder, rather
than the paper, was at fault.

**Only a clean decode is admitted.** The decoder will report a symbol whose Reed-Solomon
stage failed, and its bytes look plausible. Those are dropped rather than passed on: a
frame the decoder guessed at can only fail its CRC-32C further down, where it reads like
damaged paper instead of what it is.

**A photograph is the input this is built for, not a bonus.** The realistic disaster case
is a phone photograph of a page, not a flatbed scan, and a full-size BinaryPaper page
carries version-40 symbols — 177 modules a side. At that size a decoder has to hold its
sampling grid across the perspective and lens curvature of a hand-held shot, so one pass
over the whole page is not enough on its own. Every located symbol is warped back to a
square and retried across sampling densities, blur levels and binarizers; and because a
page is a lattice of equal-sized symbols, each one that decodes is used to predict where
its neighbours are, which lifts out symbols whose own finder patterns were never found —
clipped by the frame edge, or washed out by glare. On twelve photographs of a 48-symbol
page, one whole-page pass yielded 20 frames and the full pipeline yielded 45.

**The QR decoder carries native binaries.** `ZXingCpp` is the zxing-cpp project's own
.NET binding, and it ships native assets for `win-x64`, `win-arm64`, `linux-x64`,
`linux-arm64`, `osx-x64` and `osx-arm64` — the six platforms this kit publishes. A
platform outside that matrix has to build zxing-cpp for itself. That cost is deliberate:
the managed alternative read nothing at all from the photographed pages above.
`StbImageSharp`, which decodes the pixels, remains pure managed with no dependencies.

**Output is atomic.** Content is staged in a sibling `.partial-*` directory and moved into
place only once every byte is written, so a failed recovery never leaves something that
looks like a successful one. A user who sees output may throw the paper away.
