# Crash investigation with bounded Actions storage

Automatic CI records the last image-fuzz seed, zero-based iteration, ordered corpus
fingerprint, input SHA-256, runtime, commit, and runner identity. It saves the exact
synthetic input when it is at most 512 KiB. A successful test removes that checkpoint.
On failure the replay metadata is printed to the job log and summary before any
artifact upload, so a storage outage does not erase the reproduction recipe.

## Storage limits

All artifact uploads go through `.github/actions/store-artifact`. It checks the
uncompressed file set before copying it, then inventories retained repository
artifacts through every API page. Missing inventory refuses an upload. Nothing is
deleted automatically to make room, and no billing or visibility setting is changed.

| Upload | Maximum input size | Retention |
| --- | --- | --- |
| Automatic crash packet | 1 MiB | 1 day |
| Boundary evidence | 1 MiB per OS | 1 day |
| Requested dump | 64 MiB on one OS | 1 day |
| Release/rehearsal assets | 256 MiB | 1 day |

Each upload contains at most 64 files. The repository ceiling is 384 MiB, including
one MiB of wrapper headroom per pending artifact. CI and Release share a concurrency
group; CI reserves 71 MiB for three compact packets plus one requested dump, boundary
jobs reserve 6 MiB for their matrix, and the final release upload reserves 257 MiB.
These deliberately conservative reservations can refuse an upload before storage
is full. Keep all future upload paths under this action and concurrency group.
GitHub may replace an older pending run when another run enters the concurrency
group; it does not cancel the active run. For Release, start a fresh dispatch or
rerun all jobs: evidence from an earlier attempt is deliberately not mixed in.

At continuous 384 MiB occupancy, 31 days is 279 GiB-hours. This is a preventive
bound for these workflows, not a reset of already accrued usage or a guarantee
about GitHub's delayed meter, other repositories, or Packages. Test failures remain
failures when evidence cannot upload. Missing required release evidence fails the
release gate. Download rehearsal assets within a day; published release assets are
the durable distribution copies and are separate from Actions retention.

## Reproduce before collecting a dump

Check out the recorded commit on the recorded OS and SDK. Either replay all
mutations up to the recorded iteration (best for GC/timing-dependent bugs):

```powershell
$env:BP_FUZZ_ITERATIONS = '<zero-based iteration plus one>'
dotnet test cli/test/BinaryPaper.Recovery.FuzzTests -c Release --filter 'FullyQualifiedName~ImageDecodingSurvivesArbitraryMutation'
```

Or verify the downloaded input hash and exercise just that input:

```powershell
Get-FileHash ./image-fuzz-checkpoint.json.input.bin -Algorithm SHA256
$env:BP_FUZZ_REPLAY_INPUT = (Resolve-Path ./image-fuzz-checkpoint.json.input.bin).Path
dotnet test cli/test/BinaryPaper.Recovery.FuzzTests -c Release --filter 'FullyQualifiedName~ImageDecodingSurvivesArbitraryMutation'
```

The exact-input path bypasses mutation generation. Clear `BP_FUZZ_REPLAY_INPUT`
before returning to the full fuzz run. A matching input does not guarantee a native
crash reproduces under different scheduling, garbage collection, or native binaries.

## Optional memory diagnostics

CI's **Run workflow** form accepts `dump_type` (`none`, `mini`, `full`), one
`dump_platform`, and `fuzz_iterations` (1–10000). Scheduled and push runs never collect
full dumps. Mini dumps are the first choice for stacks and loaded modules. A requested
dump is uploaded only after a failure and only if the whole packet fits 64 MiB and
the repository reservation fits. Full dumps often exceed that cap; their refusal
does not discard the compact replay packet or its log metadata.

For unrestricted memory analysis, reproduce locally and keep the dump on your disk:

```powershell
dotnet test cli/test/BinaryPaper.Recovery.FuzzTests -c Release --blame-crash --blame-crash-dump-type full --results-directory artifacts/local-crash --filter 'FullyQualifiedName~ImageDecodingSurvivesArbitraryMutation'
```

Do not upload that directory through a generic Actions upload step. Review this
policy explicitly if a particular investigation requires a larger retained dump.
