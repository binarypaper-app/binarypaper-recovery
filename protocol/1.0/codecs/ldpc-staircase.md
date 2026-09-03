# ldpc-staircase — format & math specification

> **Editorial note — vendored document.**
> This file is a verbatim copy of the `ldpc-staircase-spec` specification, upstream version
> 0.1.2, redistributed here under the Apache License 2.0. This banner is the only
> addition; nothing below it has been altered.
>
> References below to a `vectors/` directory point at the **upstream project's** own
> conformance vectors, which are not part of this kit. The equivalent guarantee here is
> provided by [this kit's suite](../../../vectors), which exercises this codec through
> complete capsule recovery. See [README.md](README.md) in this directory for how this
> document binds to the capsule protocol.

`ldpc-staircase` is a **systematic, fixed-rate LDPC-Staircase erasure code over
GF(2)** following the RFC 5170 section 6 core, constrained to the profile defined
here.
Given `K` equal-length source symbols it produces `R` repair symbols by sparse XOR,
such that a decoder can usually reconstruct the source from `K + D` received symbols,
where `D` is a measured margin rather than a guarantee.

This document is the normative contract. Any implementation that follows it produces
**byte-identical** repair symbols, builds an **identical** parity check matrix, and
recovers identically; the committed conformance vectors in [`vectors/`](vectors/)
verify this. The library is deliberately abstract — symbol bytes in, symbol bytes
out — and independent of any transport, storage format, or application.

## 1. Profile

The implemented profile is:

- the RFC 5170 section 6 LDPC-Staircase core;
- systematic source symbols;
- one encoding symbol per unit of transport (`G = 1`);
- fixed `K` and fixed `R`, both decided before encoding;
- source and repair symbols of the same byte length `S`;
- a caller-supplied deterministic graph seed;
- degree-one peeling to a fixed point; and
- at most **one** solve of the complete unresolved residual system.

Deliberately excluded, and out of scope for this version: LDPC-Triangle, multiple
symbols per packet (`G > 1`), rateless or post-factum repair generation,
Raptor/RaptorQ precode and intermediate-symbol machinery, permanent or predetermined
inactivation, and repeated elimination-to-peeling cycles. Adding any of them is a
profile change requiring new vectors and a fresh IPR review.

## 2. Symbols and parameters

- A *symbol* is a byte array. Within one operation all symbols have the **same
  length** `S`, which MUST be **greater than 0**. There is no alignment or evenness
  requirement: the code operates over GF(2), byte-wise.
- `K` = source symbol count, `K >= 1`.
- `R` = repair symbol count, `R >= 0`.
- `N = K + R` = encoding symbol count.
- Symbol index (ESI) `0..K-1` are the source symbols; `K..K+R-1` are the repair
  symbols. Repair symbol `p` in `0..R-1` has ESI `K + p`.
- `N1` = the number of `1`s per source column in the left part of the parity check
  matrix. Default profile value: **7**. Requires `N1 >= 1` always, and additionally
  `N1 <= R` when `R >= 1`. `N1` is unused when `R = 0`, but a value below 1 is
  meaningless rather than a no-op and MUST be rejected.
- `seed` = the PRNG seed, a 32-bit unsigned integer in `[1, 0x7FFFFFFE]`.

`(K, R, N1, seed)` fully determines the code. Two implementations given the same
four values MUST build the same matrix and produce the same repair symbols.

Implementation bound: `N1 * K` MUST be representable as a signed 32-bit integer
(it indexes the construction pool of section 4.2).

`R = 0` is the explicit no-repair bypass: the matrix is empty, encoding is a no-op,
and decoding succeeds only when all `K` source symbols are present.

## 3. Pseudo-random number generator

Matrix construction consumes a Park–Miller "minimal standard" multiplicative
congruential generator, as required by RFC 5170 section 5.7:

```
state <- seed                            // 1 <= seed <= 2147483646
next():                                  // advance and return the raw state
    state <- (16807 * state) mod 2147483647
    return state                         // in [1, 2147483646]
```

`state` MUST be advanced with exact integer arithmetic (`16807 * state` needs 47
bits, so 64-bit intermediates or Schrage's method).

**Validation criterion (RFC 5170 section 5.7):** with `seed = 1`, the 10,000th value
returned by `next()` MUST be `1043618065`.

Scaling to a bounded index, for `maxv >= 1`:

```
rand(maxv):
    return floor( (double)maxv * (double)next() / 2147483647.0 )   // in [0, maxv-1]
```

The scaling MUST be computed in **IEEE 754 binary64**, in exactly this order:
multiply `maxv` by the new raw state, then divide by `2147483647.0`, then truncate
toward zero. Both operations are correctly rounded on every conforming runtime, so
the result is reproducible across .NET and the JVM. This is the formula RFC 5170
specifies, and it is **not** interchangeable with exact 64-bit integer arithmetic:
`maxv * state` can reach 2⁶² and is rounded to 53 bits of mantissa, so the two
formulations disagree on the rare value that lands within ~2.4 × 10⁻⁷ of an integer
boundary. Implementations MUST use the floating-point form above.

## 4. Parity check matrix

`H` is an `R x N` binary matrix. Row `i` (`0 <= i < R`) is one parity equation; column
`c` (`0 <= c < N`) is the encoding symbol with ESI `c`. Columns `0..K-1` are the
*left* (source) part, columns `K..N-1` the *right* (repair) part. `H[i][c] = 1` means
symbol `c` participates in equation `i`. Every equation constrains its symbols to XOR
to zero:

```
for each row i:   XOR over c with H[i][c] = 1 of symbol[c]  =  0
```

`H` is built in exactly three ordered phases, all sharing one PRNG stream started at
`seed`. The order is normative: it fixes the sequence of PRNG draws, and therefore
the graph.

`degree(i)` below means the number of `1`s in row `i` **of the left part only**
(columns `0..K-1`). Phases 4.2 and 4.3 run against an otherwise empty matrix, so
this is simply the row's degree at that moment; the staircase of 4.4 is added
afterwards.

### 4.1 Notation

- `has(i, c)` — true if `H[i][c]` is already `1`.
- `set(i, c)` — set `H[i][c] = 1`. Never called for an entry that is already set.
- `rand(maxv)` — section 3.

### 4.2 Left part: `N1` ones per source column

The pool `u` holds `N1 * K` row indices cycling over `0..R-1`, so draws are spread
evenly over the equations. `t` is the boundary between used (`< t`) and available
(`>= t`) pool slots.

```
u[h] <- h mod R              for h = N1*K - 1 down to 0
t    <- 0

for j = 0 .. K-1:                        // for each source column, in order
    for h = 0 .. N1-1:                   // place N1 ones in this column
        i <- smallest index in [t, N1*K) with not has(u[i], j), or N1*K if none
        if i < N1*K:
            repeat
                i <- t + rand(N1*K - t)
            until not has(u[i], j)
            set(u[i], j)
            u[i] <- u[t]                 // swap-remove the consumed slot
            t <- t + 1
        else:
            repeat
                i <- rand(R)
            until not has(i, j)
            set(i, j)
```

The forward scan before the draw guarantees the first `repeat` terminates. The
`else` branch terminates because `N1 <= R`, so column `j` can never already occupy
all `R` rows.

### 4.3 Left part: raise low-degree rows

Rows with fewer than two `1`s in the left part are pathological for the decoder;
this pass repairs them. Note the two `if`s are sequential, not exclusive: a row of
degree 0 is raised to 1 by the first and to 2 by the second.

```
for i = 0 .. R-1:
    if degree(i) = 0:
        j <- rand(K)
        set(i, j)
    if degree(i) = 1 and K >= 2:
        repeat
            j <- rand(K)
        until not has(i, j)
        set(i, j)
```

The `K >= 2` guard is a degenerate-case rule added by this specification: RFC 5170
assumes a second distinct column always exists. With `K = 1` rows are raised to
degree 1 only. It has no effect at any realistic parameter set.

### 4.4 Right part: the staircase

```
set(0, K)                                // first repair symbol: one entry
for i = 1 .. R-1:
    set(i, K + i)                        // this repair symbol
    set(i, K + i - 1)                    // the previous repair symbol
```

The staircase consumes no PRNG draws.

## 5. Encode

Source symbols are unchanged (the code is systematic). Repair symbols MUST be
produced in ascending ESI order, because each one depends on its predecessor:

```
repair[0] = XOR over j in row 0's left part of source[j]
repair[p] = repair[p-1] XOR ( XOR over j in row p's left part of source[j] )   for p >= 1
```

This is exactly row `p`'s equation solved for its highest-ESI variable: row `p`
contains repair `p` (column `K + p`), repair `p - 1` (column `K + p - 1`, absent for
`p = 0`), and its left-part source symbols.

Output: `R` repair symbols, each `S` bytes.

## 6. Decode

Input: the `N` symbol slots and a present-mask marking which hold valid bytes. Any
mix of source and repair symbols may be present. Decoding recovers every missing
symbol the received set determines, and MUST report each one it recovered.

Completion is judged on the **source** symbols alone (section 6.3): a decode is complete
when all `K` of them are present, whatever became of the repair symbols. But recovering a
missing repair symbol is not optional work a decoder may skip. Repair symbols are
variables of the same equations, they fall out of the same solve at no extra cost, and the
committed decode vectors record the total recovered count including them —
`dec-all-repair-missing` loses only repair symbols and still requires a recovered count of
`R`, so a decoder that stopped as soon as the source was complete would fail it.

**At least one symbol MUST be present.** The symbol length `S` is not carried in the
present-mask; it is read from a received symbol, so an entirely empty received set
leaves `S` undefined and cannot describe a decode at all. An empty received set is a
caller error rather than an incomplete decode, and an implementation MUST reject it
instead of reporting a result. All present symbols MUST have the same length.

Decoding runs in two stages and MUST NOT iterate between them.

### 6.1 Stage 1 — degree-one peeling to a fixed point

For each row `i` maintain `unknown(i)`, the number of its variables not yet known,
and `accum(i)`, the XOR of its variables that are known. While some row has
`unknown(i) = 1`, its single unknown variable equals `accum(i)`; assign it, mark it
known, and fold it into every other row that contains it. Repeat until no row has
`unknown(i) = 1`.

Peeling alone terminates with all source symbols recovered in the overwhelming
majority of real recovery sessions.

### 6.2 Stage 2 — one complete residual solve

If any source symbol is still unknown, the decoder MUST attempt exactly one solve of
the complete residual system, then stop. Let `U` be the set of still-unknown
variables and `M` the set of rows with `unknown(i) >= 2` (after the fixed point no
row has `unknown(i) = 1`, and rows with `unknown(i) = 0` carry no information about
`U`). The system is

```
for each row i in M:   XOR over v in U ∩ row i of symbol[v]  =  accum(i)
```

Reduce the `|M| x |U|` coefficient matrix over GF(2) to **reduced row echelon form**,
applying every row operation to the symbol right-hand sides as well. A variable is
then recovered exactly when it is a pivot variable whose RREF row contains no other
variable; its value is that row's right-hand side. Any variable that remains is not
determined by the received set, and decoding of that symbol fails.

This yields the maximal set of variables the received symbols determine, so no
further peeling pass can add anything — which is why the profile forbids one.

An implementation MAY refuse to start stage 2 when `|U|` exceeds a caller-supplied
bound, and report the decode as failed. This lets a caller enforce a resource limit;
it does not change the byte-level contract for any decode that does run.

### 6.3 Result

Decoding is **complete** when all `K` source symbols are present at the end. Recovery
is probabilistic: unlike an MDS code there is no `K`-of-`N` guarantee, and a session
holding `K` or more symbols may still fail. Callers MUST report completion from the
decoder's result, never from a received-symbol count.

## 7. Conformance vectors

[`vectors/MANIFEST.json`](vectors/MANIFEST.json) indexes all cases. Five kinds are
used for conformance validation:

- **prng** — `seed`, an ordinal, and the expected raw `next()` value, plus sampled
  `rand(maxv)` draws.
- **matrix** — `K`, `R`, `N1`, `seed` → the canonical serialization of `H`
  (section 7.1).
- **encode** — `K`, `R`, `N1`, `seed`, `S` + `K` source symbols → expected `R` repair
  symbols.
- **decode** — the indices missing from the `N` symbols → the expected completion
  flag, the stage that was required (`NONE` / `PEELING` / `RESIDUAL_SOLVE`), the
  number of symbols recovered, and the source indices expected to remain missing.
  Cases cover peeling-only, residual-solve, and expected-failure recoveries. The
  missing list is given inline as `missing`, or, for cases that lose more symbols
  than a readable manifest can carry, as a `missingBlob` reference (section 7.2).
  A case carries exactly one of the two.

Symbol data is stored as raw `*.bin` files: one blob per part, holding that part's
symbols concatenated in ascending ESI order at the case's fixed `S`. `MANIFEST.json`
references every blob with its byte length and SHA-256 digest. Every implementation
MUST reproduce all vectors byte-for-byte.

The cases span both ends of the profile's range. Most are small enough to check by
hand, which is what makes them precise about the algorithms. At least one matrix,
encode, and decode case is also carried at the top of the calibrated envelope
(`K = 16000`, `R = 8192`), where the matrix build draws six figures of PRNG values
and the residual solve spans thousands of unknowns. An implementation can agree on
every small case and still diverge there, so conformance requires both.

### 7.1 Canonical matrix serialization

`H` serializes as big-endian unsigned 32-bit integers:

```
u32  R                       // row count
u32  N                       // column count
for i = 0 .. R-1:
    u32  d                   // number of ones in row i
    u32  c  x d              // their column indices, strictly ascending
```

### 7.2 Missing-index blobs

A decode case whose `missing` list is impractical to inline references it as
`missingBlob` instead. The blob is the bare index sequence, with no header:

```
u32  i  x (length / 4)       // missing symbol indices, strictly ascending
```

Indices are big-endian, strictly ascending, and each is below `N`. The count is the
blob's byte length divided by four. A reader MUST accept both forms and treat them
as equivalent.

## 8. Out of scope

No I/O, compression, encryption, framing, container, or page-layout concerns. In
particular the deterministic source/repair **interleaving** across physical pages
belongs to a consuming transport or container format, not to this codec: the library
operates purely on in-memory symbol byte arrays and their ESIs.

Choosing `seed`, choosing `N1`, measuring the recovery margin `D`, and deriving a
resource limit are likewise application decisions made by the consumer of this library.
