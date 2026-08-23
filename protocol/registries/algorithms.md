# Algorithm Registry

Stable numeric identifiers used by the BinaryPaper capsule protocol. These values are
**wire constants**: once a protocol version that uses them is released, their meanings
are frozen forever.

## Governance

- A value listed as **assigned** is permanent. Its meaning never changes.
- A value listed as **reserved** is set aside and MUST NOT be emitted. A reader MUST
  reject it, exactly as it rejects any unknown value.
- A value that is neither assigned nor reserved is **unassigned**. A reader MUST reject
  it.
- There is no "ignore unknown value" behavior anywhere in this protocol. Every
  structure is fully specified, and an unrecognized identifier means the reader cannot
  faithfully recover the content, so it MUST fail rather than guess.
- New assignments are made by the BinaryPaper maintainers as part of a protocol version
  release. Assigning a value does not by itself make it usable — the release that
  assigns it also specifies its construction.

## `erasure_alg` — frame, offset 6, `u8`

Selects the erasure-recovery layer. Part of the capsule-identity bytes, so capsules
using different codecs never merge into one scan session.

| Value | Name | Status | Defined by |
| ---: | --- | --- | --- |
| 0 | — | reserved | never assigned; `0` is not a valid codec |
| 1 | Reed–Solomon GF(2¹⁶), systematic Cauchy | assigned, 1.0 | [`../1.0/codecs/erasure16.md`](../1.0/codecs/erasure16.md) |
| 2 | LDPC-Staircase over GF(2), systematic fixed-rate | assigned, 1.0 | [`../1.0/codecs/ldpc-staircase.md`](../1.0/codecs/ldpc-staircase.md) |
| 3–255 | — | unassigned | — |

**Note on value 0.** It is reserved rather than unassigned so that an all-zero header —
a common result of reading uninitialized or truncated data — can never be mistaken for
a valid frame.

**Historical note.** In experimental pre-1.0 wire versions an `erasure_alg` field
existed with an unrelated meaning (a recovery *scoping* choice, not a code family).
Those versions are not supported and their frames are rejected at the version check, so
the two meanings can never be confused on the wire.

## `aead_alg` — capsule preamble, `u8`

Selects the authenticated-encryption construction protecting the body.

| Value | Name | Status | Notes |
| ---: | --- | --- | --- |
| 0 | none (plaintext) | assigned, 1.0 | Body is unencrypted; integrity comes from `plaintext_digest`. Requires `kdf_alg = 0`. |
| 1 | AES-256-GCM | assigned, 1.0 | 32-byte key, 12-byte nonce, 16-byte tag appended, empty associated data. Requires `kdf_alg = 1`. |
| 2–255 | — | unassigned | — |

## `kdf_alg` — capsule preamble, `u8`

Selects the password-based key derivation function.

| Value | Name | Status | Notes |
| ---: | --- | --- | --- |
| 0 | none | assigned, 1.0 | Required when `aead_alg = 0`. All KDF parameter fields MUST be zero. |
| 1 | Argon2id | assigned, 1.0 | [RFC 9106](https://www.rfc-editor.org/rfc/rfc9106). Parameters carried in the preamble. Required when `aead_alg = 1`. |
| 2–255 | — | unassigned | — |

## `compression_alg` — capsule preamble, `u8`

Selects the compression applied to the whole payload package before protection.

| Value | Name | Status | Notes |
| ---: | --- | --- | --- |
| 0 | none (stored) | assigned, 1.0 | The body is the payload package directly. |
| 1 | LZMA | assigned, 1.0 | Legacy `.lzma` "alone" container, one solid stream over the whole package. |
| 2–255 | — | unassigned | — |

## Valid combinations

Not every pairing is legal. See
[SPEC.md §9.1](../1.0/SPEC.md#91-preamble-validation) for the normative rules and
[SPEC.md §12.4](../1.0/SPEC.md#124-algorithm-combinations) for what an official creator
emits.

| `aead_alg` | `kdf_alg` | `compression_alg` | Legal |
| ---: | ---: | ---: | --- |
| 0 | 0 | 0 or 1 | yes |
| 0 | 1 | any | **no** — plaintext requires `kdf_alg = 0` |
| 1 | 1 | 0 or 1 | yes |
| 1 | 0 | any | **no** — AES-GCM requires a derived key |

## Manifest `version` — payload package `binarypaper-manifest.bin`, `u8`

| Value | Status | Notes |
| ---: | --- | --- |
| 0 | reserved | never assigned |
| 1 | assigned, 1.0 | The layout in [SPEC.md §10.6](../1.0/SPEC.md#106-payload-package--zip-with-stored-entries) |
| 2–255 | unassigned | — |

## Structure magics

Not a numeric registry, but recorded here so the four four-byte tags are in one place.

| Magic | ASCII | Structure |
| --- | --- | --- |
| `42 50 51 52` | `BPQR` | Frame — one per QR code |
| `42 50 43 50` | `BPCP` | Capsule preamble — front of the stored payload |
| `42 50 4D 46` | `BPMF` | Payload manifest — inside the payload package |

A fourth magic, `PV*`-prefixed, appears only in experimental pre-1.0 structures. It is
not used by any supported version and is listed here only so that an implementer who
encounters it on an old prototype printout knows it is out of scope.
