# Vendored codec implementations

These are **verbatim copies** of two standalone, application-independent codec libraries,
redistributed here under the Apache License 2.0.

| Directory | Library | Upstream version | Implements |
| --- | --- | --- | --- |
| `Erasure16/` | `erasure16-dotnet` | 0.1.1 | `erasure_alg = 1`, systematic Cauchy Reed–Solomon over GF(2¹⁶) |
| `LdpcStaircase/` | `ldpc-staircase-dotnet` | 0.1.3 | `erasure_alg = 2`, systematic LDPC-Staircase over GF(2) |

The implementation and specification versions differ because they are separate projects
with separate version lines: these sources are `erasure16-dotnet` 0.1.1 and
`ldpc-staircase-dotnet` 0.1.3, which implement `erasure16-spec` 0.1.1 and
`ldpc-staircase-spec` 0.1.2 respectively.

## Why they are copied rather than referenced

The upstream projects are not published yet. A recovery promise that depends on a package
you cannot obtain is not a recovery promise, so the kit carries the source it needs. This
also satisfies a release requirement in its own right: the kit must build from source
without any private path or package feed.

If the upstream projects are published before a release, a future version may reference
them as pinned packages instead. That would not change any recovered byte — the
conformance vectors would catch it if it did.

## What was and was not changed

The `.cs` files are unmodified except that two files named `ProgressListener.cs` — one in
each library — were renamed to `Erasure16ProgressListener.cs` and `LdpcProgressListener.cs`
so they can coexist in one project. The file contents, namespaces, and type names are
untouched.

The code keeps its own `Erasure16` and `LdpcStaircase` namespaces and its
deliberately abstract vocabulary: shards and symbols in, shards and symbols out. It knows
nothing about capsules, frames, or BinaryPaper, and it should stay that way. The mapping
from capsule fields to codec parameters lives in `../BinaryPaper.Recovery/CapsuleRecovery.cs` and is specified
in [SPEC.md §8](../../../protocol/1.0/SPEC.md#8-erasure-recovery).

## Attribution

Upstream copyright and license notices are preserved in
[`NOTICE`](NOTICE), including the RFC 5170 attribution required for the LDPC-Staircase
profile. The normative specifications these implement are vendored separately under
[`protocol/1.0/codecs/`](../../../protocol/1.0/codecs/).
