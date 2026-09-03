# erasure16 — format & math specification

> **Editorial note — vendored document.**
> This file is a verbatim copy of the `erasure16-spec` specification, upstream version
> 0.1.1, redistributed here under the Apache License 2.0. This banner is the only
> addition; nothing below it has been altered.
>
> References below to a `vectors/` directory point at the **upstream project's** own
> conformance vectors, which are not part of this kit. The equivalent guarantee here is
> provided by [this kit's suite](../../../vectors), which exercises this codec through
> complete capsule recovery. See [README.md](README.md) in this directory for how this
> document binds to the capsule protocol.

`erasure16` is a **systematic Reed–Solomon erasure code over GF(2¹⁶)** using a
**Cauchy** generator matrix. Given `K` equal-length data shards it produces `R`
parity shards such that the original `K` data shards can be reconstructed from
**any `K` of the `K + R` shards**.

This document is the normative contract. Any implementation that follows it
produces **byte-identical** parity shards and reconstructs identically; the
committed conformance vectors in [`vectors/`](vectors/) verify this. The library is
deliberately abstract — shard bytes in, shard bytes out — and independent of any
transport, storage format, or application.

## 1. Galois field GF(2¹⁶)

- Elements are integers `0..65535`; bit *i* is the coefficient of *xⁱ* (so element
  `2` = *x*).
- Reduction polynomial: **`0x1100B`** = *x¹⁶ + x¹² + x³ + x + 1* (primitive).
- Add / subtract = bitwise XOR.
- Multiply = polynomial multiplication modulo the reduction polynomial. The product
  depends **only** on the polynomial above, so any correct implementation
  (log/antilog tables, carryless multiply, …) yields identical products.

Reference exp/log tables (generator α = `2`):

```
exp = uint16[65535]          // exp[i] = α^i
log = uint16[65536]          // log[exp[i]] = i
x = 1
for i in 0..65534:
    exp[i] = x
    log[x] = i
    x = x << 1
    if (x AND 0x10000) != 0:
        x = x XOR 0x1100B    // 17-bit reduction; result is 16-bit
log[0] = 0                   // undefined sentinel; never read
```

- `mul(a, b)` = `0` if `a == 0` or `b == 0`, else `exp[(log[a] + log[b]) mod 65535]`
- `inv(a)` = `exp[(65535 - log[a]) mod 65535]` for `a != 0`
- `div(a, b)` = `mul(a, inv(b))`

`0x1100B` is primitive, so the `exp` table has full period 65535 (all of `1..65535`
appear exactly once); implementations SHOULD assert this on table build.

## 2. Shards and word mapping

- A *shard* is a byte array. Within one operation all shards have the **same
  length**, which MUST be **even and greater than 0**.
- A shard is read as 16-bit field elements in **big-endian**:
  `word[w] = (shard[2w] << 8) | shard[2w+1]`.
- Coding is applied independently per word index across shards.

## 3. Code parameters

- `K` = dataShardCount, `K >= 1`
- `R` = parityShardCount, `R >= 0`
- `K + R <= 65536`
- Total `N = K + R`. Shards `0..K-1` are data; `K..K+R-1` are parity.

## 4. Systematic Cauchy generator matrix

The `N x K` generator matrix `G` over GF(2¹⁶):

- Rows `0..K-1`: the `K x K` identity (data shards pass through unchanged).
- Parity row `p` (`p` in `0..R-1`, i.e. matrix row `K + p`):
  `G[K+p][j] = inv( X_p XOR Y_j )` for `j` in `0..K-1`, where
  `X_p = p` (`p` in `0..R-1`) and `Y_j = R + j` (`j` in `0..K-1`).
  `X` and `Y` are disjoint integer ranges, so `X_p XOR Y_j != 0`.

Every `K x K` submatrix formed by any `K` distinct rows of `G` is invertible
(Cauchy MDS property), which is what guarantees recovery from any `K` of `N` shards.

## 5. Encode

Data shards are copied unchanged. For each word index `w` and parity row `p`:

```
parity_p[w] = XOR over j in 0..K-1 of mul( G[K+p][j], data_j[w] )
```

Output: `R` parity shards (same length as the data shards).

## 6. Reconstruct

Given at least `K` present shards (any mix of data/parity) and the set of present
shard indices:

1. If all `K` data shards are present, reconstruction is trivial (systematic).
2. Otherwise select any `K` present shards. Let `M` be the `K x K` matrix of their
   corresponding generator rows. Compute `M⁻¹` by Gaussian elimination over
   GF(2¹⁶). Then `data = M⁻¹ × (selected shard words)`, applied per word index.
   Recompute any still-missing parity shards via §5.

A session with fewer than `K` present shards is unrecoverable.

## 7. split / join helpers

- `split(data, K, shardSize)`: `shardSize` even and `> 0`; allocate `K` zeroed
  shards of `shardSize`; copy `data` (require `data.length <= K * shardSize`); slice
  into `K` data shards.
- `join(dataShards, outputLength)`: concatenate the `K` data shards in order; return
  the first `outputLength` bytes.

## 8. Conformance vectors

[`vectors/MANIFEST.json`](vectors/MANIFEST.json) indexes all cases. Three kinds:

- **field** — sample `(a, b) -> mul(a,b)` and `a -> inv(a)` checks.
- **encode** — `K`, `R`, `shardLen` + `K` data shards → expected `R` parity shards.
- **reconstruct** — `K`, `R`, `shardLen`, the `N` shards, and the subset that is
  present (`>= K` of them) → expected fully reconstructed shard set.

Shard data is stored as raw `*.bin` files; `MANIFEST.json` references them with byte
lengths and SHA-256 digests. Every implementation MUST reproduce all vectors
byte-for-byte. The vectors are generated by `erasure16-dotnet` and verified by every
implementation.

The cases span two scales. Most are small enough to check by hand, which is what makes
them precise about the algorithms. One is a transport-scale configuration
(`K = 6410`, `R = 1744`), where the generator matrix has 11.2 million entries and the
field elements it indexes span most of GF(2^16), against 256 entries and a handful of
elements at `K = 32`. An implementation can agree on every small case and still diverge
there, so conformance requires both.

### 8.1 Compact case layout

A case whose shard count makes the per-shard layout impractical uses a compact
layout instead. It carries the same information, and a reader MUST accept both.

- **encode** — `dataBlob` and `parityBlob` replace `dataShards` and `parityShards`.
  Each blob holds that part's shards concatenated in ascending index order at the
  case's fixed `shardLen`; the shard count follows from the blob's byte length. A
  case carries exactly one of the two forms.
- **reconstruct** — `missing` replaces `present`. It lists the absent shard indices,
  strictly ascending; every index not listed is present. `expectedDataBlob` and
  `expectedParityBlob` replace `expectedShards`. A case carries exactly one of the
  two forms.

## 9. Out of scope

No I/O, compression, encryption, framing, or container concerns. The library
operates purely on in-memory shard byte arrays.
