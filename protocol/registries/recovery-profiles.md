# Recovery Profile Registry

A **recovery profile** names a bounded set of capsule shapes. It sits between what the
wire can represent and what a particular machine is willing to spend:

| Layer | Question it answers | Where defined |
| --- | --- | --- |
| Wire-valid | Is this structurally representable? | [SPEC.md §5.2](../1.0/SPEC.md#52-frame-rejection) |
| **Recovery profile** | Could an official creator have produced this? | this registry |
| Local resource policy | Is this machine willing to spend that? | the operator |

Profiles exist so a reader can refuse expensive-but-well-formed input cheaply, before
allocating decoder state. A profile is a **bound**, not a prediction: your
implementation's real cost will differ, but the profile tells you the envelope you must
handle and the envelope beyond which you may safely refuse.

## Governance

- A released profile is **frozen**. Its bounds never change.
- Raising a bound produces a **new profile number**, because a reader that accepts only
  profile *n* must keep behaving identically.
- A profile is always tied to the wire versions it applies to.
- A reader MUST NOT reject input merely because a *cheaper* codec choice would also
  have been possible. Codec selection is writer behavior; the profile bounds cost, not
  taste. See [SPEC.md §12.6](../1.0/SPEC.md#126-what-is-deliberately-not-bounded-here).

## Registry

| Profile | Applies to | Status |
| --- | --- | --- |
| **Stable Recovery Profile 1** | capsule wire 1.0 | assigned |

## Stable Recovery Profile 1

Normative definition: [SPEC.md §12](../1.0/SPEC.md#12-stable-recovery-profile-1). This
page is the registry entry and summary; where the two differ, SPEC.md governs.

### Frame parameters

```text
K >= 1
R >= 0
K + R <= 65536
S even, S >= 2
```

### Restore-cost bound

```text
RESTORE_BUDGET = 84 934 656 bytes   (81 MiB)

erasure_alg = 1:
  peak_rs(K, R, S) = (2K + R)·S + 8R² + 4RK

erasure_alg = 2:
  peak_ldpc(K, R, S) = 2·(K + R)·S
                     + ceil(R · 8192 / 8)
                     + 8·(7K + 2R)
                     + 320·(K + R)
                     + 4 194 304
```

A shape is in profile for its declared codec when that codec's `peak` is at most
`RESTORE_BUDGET`.

The budget derives from the weakest device BinaryPaper supports as a restorer. It is a
**one-way door**: it may be raised in a future profile, never lowered, because sheets
already printed must keep restoring.

### Codec-specific bounds

| Codec | Additional bounds |
| --- | --- |
| `erasure_alg = 1` | none beyond the frame parameters and `peak_rs` |
| `erasure_alg = 2` | `1 <= K <= 16000`, `7 <= R <= 8192` |

`R >= 7` because the staircase profile needs at least `N1 = 7` repair symbols to be
well-formed. The residual solve is refused above 8192 unknowns, which keeps
`peak_ldpc` linear in `R` — the property that lets LDPC fit where Reed–Solomon's `8R²`
term cannot.

### KDF profile

```text
kdf_memory_kib   65536      (64 MiB)
kdf_iterations   3
kdf_parallelism  1
kdf_output_len   32
salt_len         >= 16
nonce_len        12
```

Readers SHOULD accept costs at or below these values and SHOULD require an explicit
operator override above them.

### Algorithm combinations

Only the pairings marked legal in
[`algorithms.md`](algorithms.md#valid-combinations), with `compression_alg` either 0 or 1.

### Not bounded by this profile

- **`symbol_len` upper bound.** A printed backup satisfies a QR capacity constraint,
  but the error-correction level is not recoverable from a frame, and a reader given
  raw frame files has no print context. The restore-cost bound already constrains the
  dangerous direction.
- **Codec choice.** See the governance note above.
- **Decompressed output size.** Bounded by operator policy and available disk, never by
  a fixed low cap — a legitimate highly compressible backup produces a large package
  from a small capsule.

## Behavior outside a known profile

A reader SHOULD refuse out-of-profile input with a distinct **resource/profile** error
rather than a parse error, so an operator can tell "this is not a valid capsule" from
"this capsule is larger than I am configured to handle".

A reader MAY offer an explicit override that accepts out-of-profile shapes. If it does,
the override MUST be opt-in per invocation and MUST NOT be the default.

An unknown future profile identifier, if one ever appears on the wire, is fatal — the
same rule as every other unknown identifier.
