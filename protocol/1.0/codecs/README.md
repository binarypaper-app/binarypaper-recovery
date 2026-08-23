# Vendored codec specifications

The two documents in this directory are the **normative erasure-code contracts** that
BinaryPaper Capsule Protocol 1.0 depends on. `SPEC.md` references them; they are not
optional reading if you are writing a recovery implementation.

| File | Defines | `erasure_alg` |
| --- | --- | --- |
| [`erasure16.md`](erasure16.md) | Systematic Cauchy Reed–Solomon over GF(2¹⁶) | `1` |
| [`ldpc-staircase.md`](ldpc-staircase.md) | Systematic LDPC-Staircase over GF(2) | `2` |

## Why they are copied here

These specifications come from two standalone, application-independent codec
projects. Those projects are not yet published, so the recovery kit carries the
normative text it needs rather than pointing at something you cannot read. A recovery
promise that depends on an unavailable document is not a recovery promise.

The files are **verbatim copies**, not adaptations. They are deliberately abstract —
shards and symbols in, shards and symbols out — and mention no capsule, frame, or
product concept. That separation is intentional: the codecs are general-purpose, and
`SPEC.md` alone defines how BinaryPaper binds to them (which parameters come from
which frame fields, how the seed is derived, and what a reader must conclude from a
decode result).

If the upstream projects are published later, a future kit release may reference them
by version and hash instead of copying them. That would not change any byte.

## Provenance

| Document | Upstream project | Upstream version |
| --- | --- | --- |
| `erasure16.md` | `erasure16-spec` | 0.1.0 |
| `ldpc-staircase.md` | `ldpc-staircase-spec` | 0.1.0 |

Both are licensed under the Apache License 2.0, the same license as this repository.
Their upstream copyright and attribution notices are preserved in
[`NOTICE`](NOTICE) in this directory — including the RFC 5170 attribution required
for the LDPC-Staircase profile.

## Conformance vector references

Both upstream documents refer to conformance vectors in a `vectors/` directory of
their own repositories. Those directories are not part of this kit. The equivalent
guarantee here is provided by this kit's own suite in [`/vectors`](../../../vectors),
which exercises both codecs through complete capsule recovery. Where an upstream
document says "the committed conformance vectors verify this", read it as a statement
about the upstream project, not as a pointer to a file you should expect to find here.
