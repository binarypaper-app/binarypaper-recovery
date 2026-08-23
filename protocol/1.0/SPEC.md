# BinaryPaper Capsule Protocol 1.0

**Recovery specification.**

Wire version `format_major = 1`, `format_minor = 0`.

## Status

This document is the normative contract for recovering a BinaryPaper capsule at wire
version 1.0. An implementation that follows it recovers byte-identical output from the
same input, and rejects the same inputs, as any other conforming implementation.

Once a version of this document is published under a release tag, its meaning is
**frozen**. See [Compatibility and versioning](#14-compatibility-and-versioning).

> **Unreleased.** No release has been tagged yet. Until one is, this text may change.

## Conventions

The key words **MUST**, **MUST NOT**, **REQUIRED**, **SHALL**, **SHALL NOT**,
**SHOULD**, **SHOULD NOT**, **RECOMMENDED**, **MAY**, and **OPTIONAL** are to be
interpreted as described in BCP 14 ([RFC 2119](https://www.rfc-editor.org/rfc/rfc2119),
[RFC 8174](https://www.rfc-editor.org/rfc/rfc8174)) when, and only when, they appear
in all capitals.

Text that is not a normative statement is either **informative** (background that
helps you implement correctly) or **writer behavior** (what an official creator does,
which a reader may rely on only through
[Stable Recovery Profile 1](#12-stable-recovery-profile-1)).

Unless stated otherwise:

- all integers are **unsigned** and **big-endian**;
- all text is **UTF-8**;
- all length fields are **byte counts**;
- `0x00` is false and `0x01` is true; and
- byte offsets are zero-based.

## 1. Scope

This specification covers **recovery**: turning printed or scanned BinaryPaper pages,
or the raw frame payloads they carry, back into the exact original bytes.

In scope:

- the byte layout of every structure a reader must parse;
- the order in which a reader must validate them;
- how frames are grouped into a capsule and when a capsule is complete;
- the cryptographic, compression, and packaging constructions;
- what a conforming reader must reject, and at which stage; and
- the resource discipline a reader must apply to untrusted input.

Not in scope:

- creating capsules;
- choosing print geometry, paper, or error-correction level;
- calibrating a printer or scanner;
- PDF rendering or live camera capture; and
- any account, entitlement, or network behavior.

Writer rules appear only where they are needed to define interoperable bytes or the
reliability contract.

## 2. Terminology

| Term | Meaning |
| --- | --- |
| **Capsule** | One complete backup: everything needed to restore one payload. |
| **Frame** | The `BPQR` byte envelope carried by one QR code. One QR, one frame. |
| **Symbol** | The erasure-coded unit inside a frame. Source symbols carry the payload; repair symbols carry redundancy. |
| **Source symbol** | Symbol with `symbol_index < source_symbol_count`. |
| **Repair symbol** | Symbol with `symbol_index >= source_symbol_count`. |
| **Stored payload** | The capsule preamble followed by the body, zero-padded to `K × S`. |
| **Body** | The protected payload bytes: encrypted, or plaintext with a digest. |
| **Payload package** | The ZIP container holding the user's content and its manifest. |
| **Scan session** | The in-memory set of validated frames collected while recovering one capsule. |
| **Reader** | Any implementation that recovers a capsule. |
| **Creator** / **writer** | Any implementation that produces a capsule. |

Throughout, `K` is `source_symbol_count`, `R` is `repair_symbol_count`, and `S` is
`symbol_len`.

## 3. How a capsule is built (informative)

Understanding recovery is easier if you know what the writer did. This section is
**informative**; the normative recovery path is [section 4](#4-recovery-pipeline).

1. Build a ZIP **payload package** from the user's file, text note, or folder,
   including the required manifest entry. ZIP entries are **stored**, never
   per-entry-compressed.
2. Compress the whole package as one solid LZMA stream. If that does not shrink it,
   keep it uncompressed. Record which in `compression_alg`.
3. Form the **body**: either AES-256-GCM ciphertext with its tag appended, or the
   plaintext bytes plus a SHA-256 digest recorded in the preamble.
4. Prepend the **capsule preamble** to the body — this is the stored payload.
5. Zero-pad the stored payload to `K × S` and split it into `K` source symbols.
6. Generate `R` repair symbols with the selected erasure code.
7. Wrap each of the `K + R` symbols in a `BPQR` frame with its CRC-32C.
8. Lay the frames out on pages and print.

The compression decision is made by measurement, not by file type, so a capsule is
never larger than storing the payload raw.

## 4. Recovery pipeline

A reader MUST implement this pipeline. Stage order is normative and is stated
precisely in [section 11](#11-validation-order).

1. Obtain frame bytes — by decoding QR codes from page images, or by reading raw frame
   files.
2. Validate each frame's structure, fields, and CRC-32C. Discard those that fail.
3. Group surviving frames into scan sessions by capsule identity.
4. Ignore exact duplicates; record conflicts for differing duplicates.
5. Check the declared shape against the reader's accepted profile and resource policy.
6. Recover the `K` source symbols using the erasure code named by `erasure_alg`.
7. Concatenate source symbols in ascending `symbol_index` order to re-form the padded
   stored payload.
8. Parse the capsule preamble at offset 0 and extract the body using `body_len`.
9. Authenticate the body — AES-GCM tag, or SHA-256 plaintext digest.
10. If `compression_alg = 1`, LZMA-decompress the authenticated body.
11. Parse the payload package and restore the file, text note, or folder.

## 5. Frame

Each QR code carries exactly one frame, in **QR Byte Mode**, with no additional
encoding, framing, or text transformation. This protocol does not use QR structured
append.

```text
offset  field                 size  notes
------  --------------------  ----  --------------------------------------------
     0  magic                    4  ASCII "BPQR" = 42 50 51 52
     4  format_major            u8  MUST be 1
     5  format_minor            u8  MUST be 0
     6  erasure_alg             u8  1 = Reed-Solomon GF(2^16); 2 = LDPC-Staircase
     7  capsule_id               4  opaque capsule identifier
    11  source_symbol_count    u16  K
    13  repair_symbol_count    u16  R
    15  symbol_len             u16  S, bytes per symbol payload
    17  symbol_index           u16  0 .. (K + R - 1)
    19  symbol_payload           S  the symbol bytes
  19+S  frame_digest             4  CRC-32C over offsets 0 .. 18+S, big-endian
```

The header is **19 bytes** and the fixed per-frame overhead is **23 bytes**
(header + digest). Therefore:

```text
frame_length = 23 + symbol_len
```

A reader MUST reject a frame whose total length is not exactly `23 + symbol_len`.

The version and `erasure_alg` sit immediately after the magic so that a reader can
confirm the magic, read the version and codec, and only then interpret the remaining
bytes. There is no separate descriptor structure; the capsule-identity fields are
inline in every frame.

`erasure_alg` selects the recovery layer and is otherwise independent of the framing.
The frame is byte-identical between the two codecs except for this one byte.

### 5.1 Symbol roles

```text
0 <= symbol_index < K                    source symbol
K <= symbol_index < K + R                repair symbol
```

Within one scan session a frame's unique identity is its `symbol_index`.

### 5.2 Frame rejection

A reader MUST reject a frame when any of the following holds:

- `magic` is not `BPQR`;
- `format_major` is not `1` or `format_minor` is not `0`;
- `erasure_alg` is not `1` or `2`;
- `symbol_len` is `0`;
- `symbol_len` is odd;
- `K < 1`;
- `K + R > 65536`;
- `symbol_index >= K + R`;
- the total length is not `23 + symbol_len`; or
- `frame_digest` does not match the computed CRC-32C.

A rejected frame MUST NOT influence any scan session. It MUST NOT be counted toward
progress and MUST NOT contribute a symbol.

`symbol_len` is even because the Reed–Solomon layer reads symbols as big-endian 16-bit
words. The LDPC layer is byte-oriented and does not need it, but carries the same rule
so the frame is codec-independent.

## 6. Frame digest

`frame_digest` is a 4-byte **CRC-32C** (Castagnoli) computed over every preceding byte
of the frame — offset 0 through `18 + symbol_len` inclusive, that is `magic` through
the final byte of `symbol_payload`.

```text
polynomial     0x1EDC6F41   (reflected: 0x82F63B78)
initial value  0xFFFFFFFF
reflect in     yes
reflect out    yes
final XOR      0xFFFFFFFF
```

This is the standard iSCSI/SCTP/ext4 variant. The resulting 32-bit value is stored
**big-endian**.

The frame digest is a pre-filter that rejects damaged frames before reconstruction. It
is **not** the integrity guarantee for recovered content: the whole reconstructed
payload is independently authenticated in [section 9](#9-authentication). CRC-32C
additionally guarantees detection of burst errors, the dominant paper-damage mode.

> **Security note.** A correct CRC-32C proves the frame was not accidentally damaged.
> It proves nothing about who produced it. Recomputing a CRC after altering a header is
> trivial. See [SECURITY-CONSIDERATIONS.md](SECURITY-CONSIDERATIONS.md).

## 7. Capsule identity and scan sessions

### 7.1 Identity bytes

The fields that identify a capsule are the seven header fields between the magic and
`symbol_index`, in wire order:

```text
format_major | format_minor | erasure_alg | capsule_id | K | R | symbol_len
```

That is offsets 4 through 18 inclusive — **15 bytes**. All frames of one capsule MUST
carry byte-identical capsule-identity bytes.

Because `format_major` and `format_minor` are part of the identity, a frame of a
different wire version can never join a session — even if every other field matches.
Because `erasure_alg` is part of the identity, a Reed–Solomon frame and an LDPC frame
never merge into one session.

`capsule_id` is an opaque 4-byte value. A reader MUST treat it as an identifier only.
It carries no user data and MUST NOT be displayed as though it were meaningful content.
Writers generate it with a cryptographically secure random source.

### 7.2 Session rules

1. A frame MUST be fully validated ([section 5.2](#52-frame-rejection)) before it is
   admitted to a session.
2. The first admitted frame establishes the session's capsule-identity bytes.
3. A later frame joins that session only if its capsule-identity bytes are identical.
4. A frame with a different `capsule_id` belongs to a **different** capsule.
5. A frame with the same `capsule_id` but different capsule-identity bytes is a
   **conflict**. The reader MUST record the conflict and MUST NOT merge the frame.
6. A frame whose `symbol_index` is already present, with identical frame bytes, is an
   **exact duplicate** and MUST be ignored.
7. A frame whose `symbol_index` is already present, with different frame bytes, is a
   **conflicting duplicate**. The reader MUST record a conflict and MUST NOT silently
   choose one payload over the other.

A session with any recorded conflict MUST NOT be reconstructed.

### 7.3 Multiple capsules in one input

A reader given input containing frames from more than one capsule MUST NOT merge them.
It MUST either recover a capsule the user identified, or report the distinct capsules
it found. Silently recovering "the biggest one" is non-conforming.

## 8. Erasure recovery

### 8.1 Common structure

For both codecs:

```text
stored_payload  = preamble || body
padded          = stored_payload || 0x00 ... up to K * S bytes
source symbol i = padded[i*S .. (i+1)*S)
```

Recovery reverses this: obtain all `K` source symbols, concatenate them in ascending
`symbol_index` order, and parse the preamble at offset 0. Bytes after
`preamble + body` up to `K × S` are zero padding and MUST be discarded.

`K × S` MUST be computed in at least 64-bit arithmetic. With the field ceilings alone
it can reach `65535 × 65534`, which overflows a signed 32-bit accumulator.

When `R = 0`, no repair symbols exist and every source frame is required. This is a
valid capsule, not an error.

### 8.2 `erasure_alg = 1` — Reed–Solomon over GF(2¹⁶)

Normative construction: [`codecs/erasure16.md`](codecs/erasure16.md).

Binding to this protocol:

| Codec parameter | Source |
| --- | --- |
| `K` (data shards) | frame `source_symbol_count` |
| `R` (parity shards) | frame `repair_symbol_count` |
| shard length | frame `symbol_len` |
| shard bytes | frame `symbol_payload` |
| shard index | frame `symbol_index` |

The code is **MDS**: any `K` of the `K + R` symbols reconstruct the capsule. A session
is therefore complete when it holds `K` valid unique frames with no recorded conflict.
The whole capsule is one code — there are no blocks.

A reader MAY short-circuit the codec entirely when `R = 0` and concatenate the source
symbols directly.

### 8.3 `erasure_alg = 2` — LDPC-Staircase over GF(2)

Normative construction: [`codecs/ldpc-staircase.md`](codecs/ldpc-staircase.md).

Binding to this protocol:

| Codec parameter | Source |
| --- | --- |
| `K` (source symbols) | frame `source_symbol_count` |
| `R` (repair symbols) | frame `repair_symbol_count` |
| symbol length | frame `symbol_len` |
| `N1` (ones per source column) | **fixed at 7** |
| `seed` | derived from `capsule_id`, below |

#### Seed derivation (normative)

The graph seed is derived deterministically from `capsule_id`, so every decoder
rebuilds an identical parity-check matrix from the frame header alone. **No seed field
exists on the wire.**

Let `c` be `capsule_id` interpreted as a big-endian unsigned 32-bit integer:

```text
seed = (c mod 0x7FFFFFFE) + 1          0x7FFFFFFE = 2147483646
```

`seed` is therefore in `[1, 0x7FFFFFFE]`, exactly the range the codec's PRNG requires.
The reduction is pure integer arithmetic and is identical on every platform.

#### Completion (normative)

**This code is not MDS.** Holding `K` or more valid frames is **necessary but not
sufficient**.

1. A session MAY attempt decoding once it holds at least `K` valid unique frames and
   has no recorded conflict.
2. Rebuild the code from `(K, R, N1 = 7, seed)`, allocate the `K + R` symbol slots,
   mark the present ones, and run the decode: degree-one peeling to a fixed point,
   then **at most one** complete residual GF(2) solve.
3. The session is **complete** when, and only when, the decoder reports that all `K`
   source symbols are present.
4. A reader MUST take completion from the decoder result. A reader MUST NOT infer
   completion from a received-symbol count.
5. A reader MAY refuse to begin the residual solve when the unresolved system exceeds
   a configured bound, and report the decode as incomplete rather than exhaust memory.
   This does not change the byte-level result of any decode that does run.

In practice a session completes at roughly `K + D` received symbols, where `D` is a
small margin that grows with correlated loss. `D` is informative and MUST NOT be used
as a completion test.

#### Progress reporting

For both codecs, a reasonable progress indicator is:

```text
min(valid_unique_frames, K) / K
```

For `erasure_alg = 1` this is an exact promise: reaching `K` means recovery succeeds.
For `erasure_alg = 2` it is a **lower bound only**. A reader SHOULD NOT present LDPC
progress in a way that promises completion at `K`, and MUST be able to ask for more
frames after reaching `K`.

### 8.4 Page interleaving (writer behavior)

Because LDPC-Staircase is not MDS, *which* symbols are lost matters: losing a whole
sheet removes a contiguous run of symbol indices and concentrates damage on
graph-adjacent symbols. Writers of `erasure_alg = 2` capsules therefore place frames
with a deterministic interleaving permutation so that one lost page becomes a
maximally spread-out symbol loss.

Let `F` be frames per page and `P = ceil((K + R) / F)` the page count. Number all
`P × F` physical slots in reading order. Then:

```text
for page in 0 .. P-1:
    for slot_on_page in 0 .. F-1:
        e = page + slot_on_page * P
        if e < K + R:
            slot (page * F + slot_on_page) holds symbol_index = e
        else:
            slot (page * F + slot_on_page) is blank
```

Blank trailing cells MUST NOT be compacted away by a writer, because that would shift
the permutation.

**This rule is writer-side only.** Every frame carries its own `symbol_index`, so a
reader collects symbols by index regardless of where they were printed and needs no
knowledge of the permutation. It is documented here because it is part of the
reliability contract a compatible writer must honor, not because a reader acts on it.

## 9. Capsule preamble

The preamble carries the fields needed once, after reassembly. It is written in the
clear at the front of the stored payload and is erasure-protected like everything else.

```text
field             size          notes
----------------  ------------  ----------------------------------------
magic                        4  ASCII "BPCP" = 42 50 43 50
aead_alg                    u8  0 = none (plaintext), 1 = AES-256-GCM
kdf_alg                     u8  0 = none, 1 = Argon2id
compression_alg             u8  0 = none (stored), 1 = LZMA
kdf_memory_kib             u32
kdf_iterations             u32
kdf_parallelism            u32
kdf_output_len             u16
salt_len                    u8
nonce_len                   u8
body_len                   u64  length of the body that follows
salt                 salt_len
nonce               nonce_len
plaintext_digest            32  present only when aead_alg = 0
body                 body_len
```

The preamble is self-delimiting: the fixed header plus `salt_len`, `nonce_len`, and
the `aead_alg`-keyed presence of `plaintext_digest` fully determine where `body`
begins, and `body_len` gives its length.

Let `preamble_header_len` be the number of bytes before `body`.

### 9.1 Preamble validation

A reader MUST reject the capsule when any of the following holds:

- `magic` is not `BPCP`;
- `body_len` is `0`;
- `compression_alg` is not `0` or `1`;
- `aead_alg` is not `0` or `1`;
- `preamble_header_len + body_len > K × S`; or
- the algorithm combination rules below are violated.

`preamble_header_len + body_len` MUST be computed in at least 64-bit arithmetic.
`body_len` is a `u64` and is attacker-chosen until this check passes.

**When `aead_alg = 1` (encrypted):**

- `kdf_alg` MUST be `1`;
- `kdf_memory_kib`, `kdf_iterations`, `kdf_parallelism`, and `kdf_output_len` MUST all
  be greater than `0`;
- `salt_len` MUST be at least `16`;
- `nonce_len` MUST be exactly `12`; and
- `plaintext_digest` MUST be absent.

**When `aead_alg = 0` (plaintext):**

- `kdf_alg` MUST be `0`;
- `kdf_memory_kib`, `kdf_iterations`, `kdf_parallelism`, and `kdf_output_len` MUST all
  be `0`;
- `salt_len` MUST be `0`;
- `nonce_len` MUST be `0`; and
- `plaintext_digest` MUST be present and MUST equal `SHA-256(body)`.

A reader MUST validate KDF parameters **before** invoking the KDF. Argon2id memory
cost is attacker-chosen until this check passes.

## 10. Cryptography, compression, and packaging

### 10.1 Password normalization

A user password MUST be normalized to **Unicode NFC** and then encoded as **UTF-8**
before being passed to the KDF.

This is normative and observable: a password typed in a decomposed form recovers the
same capsule as the same password typed in a composed form. A reader that skips
normalization will fail to open capsules that a conforming reader opens.

### 10.2 Key derivation — Argon2id

`kdf_alg = 1` is **Argon2id** as specified in
[RFC 9106](https://www.rfc-editor.org/rfc/rfc9106).

Inputs:

| Argon2id input | Value |
| --- | --- |
| password | NFC-normalized, UTF-8-encoded user password |
| salt | preamble `salt` (`salt_len` bytes) |
| memory cost | preamble `kdf_memory_kib`, in KiB |
| iterations | preamble `kdf_iterations` |
| parallelism | preamble `kdf_parallelism` |
| tag length | preamble `kdf_output_len` |
| secret / associated data | empty |

The output is the AES key. Parameters live in the preamble so that a future writer
profile can raise them without changing the frame layout; a reader takes them from the
capsule, subject to [Stable Recovery Profile 1](#12-stable-recovery-profile-1).

### 10.3 AEAD — AES-256-GCM

`aead_alg = 1` is **AES-256-GCM** as specified in
[NIST SP 800-38D](https://csrc.nist.gov/publications/detail/sp/800-38d/final).

- key length: **32 bytes** (so `kdf_output_len` is 32 in practice);
- nonce length: **12 bytes**;
- tag length: **16 bytes**;
- the tag is **appended** to the ciphertext, so `body = ciphertext || tag`; and
- **associated data is empty.**

Associated data is empty by design. Every field that influences recovery feeds either
key derivation (`salt`, KDF parameters) or the reconstructed ciphertext (`K`, `R`, `S`,
`body_len`). The tag already covers both, so tampering with any of them yields a wrong
key or wrong bytes and the tag fails.

`body_len` includes the 16-byte tag. A reader MUST reject the capsule when
`aead_alg = 1` and `body_len < 16`, because the body cannot even contain the tag.

A `body_len` of exactly `16` is structurally valid — it authenticates as an empty
plaintext — but the empty payload package that results cannot be a valid ZIP archive,
so such a capsule fails at stage 10 rather than stage 8. Readers MUST NOT special-case
it earlier; reporting a package failure as an authentication failure would be a false
diagnostic.

### 10.4 Plaintext mode

`aead_alg = 0` means the body is the payload bytes with no encryption. The preamble's
`plaintext_digest` MUST equal `SHA-256(body)` and MUST be verified before the body is
parsed or decompressed.

Plaintext mode provides **integrity, not confidentiality**. Anyone holding the pages
can read the content.

### 10.5 Compression — LZMA

`compression_alg = 1` means the body (after decryption, if encrypted) is a single
**LZMA** stream in the legacy `.lzma` "alone" container, covering the whole payload
package as one solid stream.

`compression_alg = 0` means the body is the payload package directly.

The whole package is compressed as one stream rather than per ZIP entry, which both
improves the ratio and lets context be shared across files. A writer sets
`compression_alg = 1` only when LZMA actually shrinks the package, so a capsule is
never larger than storing the payload raw.

A reader MUST NOT decompress unauthenticated bytes. See
[section 11](#11-validation-order).

### 10.6 Payload package — ZIP with stored entries

The payload package is a standard ZIP archive. A reader MUST reject it unless all of
the following hold:

- every entry uses compression method **0 (stored)**; there is no per-entry
  compression;
- an entry named exactly `binarypaper-manifest.bin` is present, exactly once;
- no ZIP encryption is used;
- every entry path is valid UTF-8;
- every entry path is **relative**: it MUST NOT begin with `/`, MUST NOT begin with a
  drive letter followed by `:`, and MUST NOT begin with `\\`;
- no path segment is `.` or `..`;
- no entry path is empty or consists only of separators;
- no two entries resolve to the same path; and
- no entry is a symbolic link or other non-regular entry.

A reader MUST treat `\` in an entry path as **invalid**, not as a separator to be
converted. Silently rewriting separators makes two distinct declared paths collide and
turns a rejection into a duplicate.

A reader MUST compare entry paths for duplication **after** validation and **before**
writing anything. Two entries that resolve to the same path are a rejection, not a
last-writer-wins overwrite.

These are security requirements, not stylistic ones. See
[SECURITY-CONSIDERATIONS.md](SECURITY-CONSIDERATIONS.md).

#### `binarypaper-manifest.bin`

```text
field             size              notes
----------------  ----------------  ----------------------------------
magic                            4  ASCII "BPMF" = 42 50 4D 46
version                         u8  MUST be 1
is_text_note                    u8  0x00 = files, 0x01 = text note
display_name_len               u16
display_name      display_name_len  UTF-8
```

A reader MUST reject the manifest when `magic` is not `BPMF`, `version` is not `1`,
`is_text_note` is neither `0x00` nor `0x01`, `display_name_len` exceeds the remaining
entry bytes, or `display_name` is not valid UTF-8.

`display_name` is used for user-facing naming. A reader MUST treat it as untrusted
text: it MUST NOT be used directly as a filesystem path, and any path separator or
traversal sequence in it MUST be neutralized before it influences an output location.

#### Payload kind

The manifest's `is_text_note` flag plus the entry count determine what was stored:

| `is_text_note` | Non-manifest entries | Kind |
| --- | --- | --- |
| `0x01` | exactly 1 | text note — the entry is UTF-8 text |
| `0x00` | exactly 1 | single file |
| `0x00` | 2 or more | file tree |

A reader MUST reject a package with zero non-manifest entries, and MUST reject
`is_text_note = 0x01` with more than one non-manifest entry.

`is_text_note` exists because it is the only kind distinction not derivable from the
ZIP itself.

## 11. Validation order

This ordering is **normative**. A reader MUST NOT perform work belonging to a later
stage before the earlier stages have passed for the bytes it is acting on.

| # | Stage | Gate |
| ---: | --- | --- |
| 1 | Frame structure | length ≥ 23; `magic`; version; `erasure_alg` |
| 2 | Frame fields | `S` even and nonzero; `K ≥ 1`; `K + R ≤ 65536`; `symbol_index < K + R`; length `= 23 + S` |
| 3 | Frame digest | CRC-32C matches |
| 4 | Session identity | grouping, conflicts, duplicates |
| 5 | Profile and resource preflight | declared shape within accepted profile and local policy |
| 6 | Erasure recovery | reconstruct source symbols |
| 7 | Preamble | magic, algorithm combination, length bounds, KDF parameters |
| 8 | **Authentication** | AES-GCM tag, or plaintext digest |
| 9 | Decompression | only authenticated bytes; size preflight |
| 10 | Payload package | ZIP structure, manifest, entry paths |
| 11 | Output | publish restored content |

Stages 1–3 gate whether a frame exists at all. Stage 5 happens **before** any decoder
state, symbol table, or residual matrix is allocated.

**Stage 8 is the security boundary.** No unauthenticated byte may reach a parser that
allocates on declared values, and no unauthenticated byte may be published as restored
content.

A reader MUST NOT report an earlier-stage failure as a later-stage failure. In
particular, a wrong password and a modified AES-GCM tag are both stage-8
authentication failures and MAY share a single error: the cryptography cannot
distinguish them, and claiming otherwise would be a false diagnostic.

### 11.1 Accepted frames survive a failed password

Password failure is a stage-8 outcome for one attempt, not a reason to discard stages
1–6. A reader holding accepted frames in a live session MUST keep them available so
the user can retry the password without rescanning.

### 11.2 Checked arithmetic

Every length, count, offset, and product computed from declared header fields MUST be
evaluated with overflow-checked or widened arithmetic, and MUST be validated **before**
it is used to size an allocation, index a buffer, or bound a loop.

- `K + R` MUST be computed in at least 32-bit arithmetic. Both operands are `u16`, so
  the sum can reach 131070.
- `K × S` MUST be computed in at least 64-bit arithmetic.
- `preamble_header_len + body_len` MUST be computed in at least 64-bit arithmetic.
- `salt_len`, `nonce_len`, `display_name_len`, and every other declared length MUST be
  bounds-checked against the remaining input before the corresponding bytes are read.

A reader MUST NOT allocate a buffer whose size derives from a declared value that has
not yet passed its validation rule. There is no legitimate reason to speculatively
allocate the wire maximum.

## 12. Stable Recovery Profile 1

The wire can *represent* shapes that no official creator ever *emits*. Hostile input
can choose expensive but structurally valid parameters. This section defines the
parameter envelope an official stable BinaryPaper creator is permitted to produce, so
a reader can refuse the rest cheaply and early.

Three distinct notions:

1. **Wire-valid** — structurally representable ([section 5.2](#52-frame-rejection)).
2. **Profile 1** — shapes an official stable creator may emit (this section).
3. **Local resource policy** — what a given operator's machine will spend
   (operator-configurable; not defined here).

A reader MUST NOT attempt to allocate the wire maximum. A reader SHOULD refuse input
outside Profile 1 with a distinct resource/profile error rather than a parse error,
and MAY offer an explicit operator override.

### 12.1 Frame parameters

```text
K  >= 1
R  >= 0                        R = 0 is valid (no redundancy)
K + R <= 65536
S  even, S >= 2
```

### 12.2 Restore-cost bound

An official creator accepts a plan only if the selected codec's predicted peak restore
working set fits a fixed budget, so that every backup it prints is restorable on the
weakest device BinaryPaper supports.

```text
RESTORE_BUDGET = 84 934 656 bytes        (81 MiB)
```

For `erasure_alg = 1` (Reed–Solomon), evaluated at the worst case that all `R` losses
are source symbols:

```text
peak_rs(K, R, S) = (2K + R)·S + 8R² + 4RK
```

For `erasure_alg = 2` (LDPC-Staircase):

```text
peak_ldpc(K, R, S) = 2·(K + R)·S
                   + ceil(R · 8192 / 8)          residual coefficient bit-matrix
                   + 8·(7K + 2R)                 sparse adjacency entries (N1 = 7)
                   + 320·(K + R)                 per-symbol structural envelope
                   + 4 194 304                   process floor
```

A shape is in Profile 1 for its declared codec when that codec's `peak` is at most
`RESTORE_BUDGET`, together with the codec-specific bounds below.

These formulas are **bounds used for admission**, not predictions of any particular
implementation's actual usage. Your implementation's real cost may differ; the point of
publishing them is that they define the envelope you must be able to handle, and the
envelope beyond which you may safely refuse.

### 12.3 Codec-specific bounds

`erasure_alg = 1` (Reed–Solomon): bounded by [12.1](#121-frame-parameters) and
`peak_rs <= RESTORE_BUDGET`.

`erasure_alg = 2` (LDPC-Staircase), additionally:

```text
1 <= K <= 16000
7 <= R <= 8192
```

`R >= 7` because the staircase profile requires at least `N1` repair symbols to be
well-formed. `K <= 16000` and `R <= 8192` are the measured decoder envelope. The
residual solve is refused above **8192** unknowns, which keeps `peak_ldpc` linear in
`R` — the property that lets LDPC fit where Reed–Solomon's `8R²` term cannot.

### 12.4 Algorithm combinations

An official stable creator emits only:

| `erasure_alg` | `aead_alg` | `kdf_alg` | `compression_alg` |
| --- | --- | --- | --- |
| 1 or 2 | 0 | 0 | 0 or 1 |
| 1 or 2 | 1 | 1 | 0 or 1 |

with the preamble field constraints of [section 9.1](#91-preamble-validation).

### 12.5 KDF profile

The current writer-approved Argon2id parameters are:

```text
kdf_memory_kib   65536        (64 MiB)
kdf_iterations   3
kdf_parallelism  1
kdf_output_len   32
salt_len         >= 16
nonce_len        12
```

A reader MUST validate these against its resource policy before invoking Argon2id.
A reader SHOULD accept parameters at or below this cost, and SHOULD refuse
substantially higher cost unless the operator overrides — a capsule declaring a
multi-gigabyte memory cost is a denial-of-service attempt, not a stronger backup.

### 12.6 What is deliberately *not* bounded here

**`symbol_len` has no profile-specific upper bound beyond [12.1](#121-frame-parameters)
and the restore-cost bound.**

A printed backup additionally satisfies a QR capacity constraint — `23 + S` must fit
the QR symbol version and error-correction level chosen for that print — but the
error-correction level is **not recoverable from a frame**, and a reader handed raw
frame files has no print context at all. A reader therefore MUST NOT reject a frame on
the basis of an assumed QR capacity. The restore-cost bound already constrains the
dangerous direction, which is large `S` combined with large `K`.

Similarly, **codec selection is writer behavior, not a reader test.** A creator picks
Reed–Solomon when it fits the budget and LDPC otherwise. A reader MUST NOT reject an
LDPC capsule merely because Reed–Solomon would also have fit; that combination is
cheaper to recover, not more dangerous.

### 12.7 Unknown future values

- An unknown `format_major`/`format_minor` is **fatal** — reject the frame.
- An unknown `erasure_alg` is **fatal** — reject the frame.
- An unknown `aead_alg`, `kdf_alg`, or `compression_alg` is **fatal** — reject the
  capsule.
- An unknown manifest `version` is **fatal** — reject the package.

There is no "ignore unknown field" behavior anywhere in this protocol. Every structure
is fully specified, and an unrecognized value means the reader cannot faithfully
recover the content.

## 13. What is visible before decryption

This matters for privacy, so it is stated exactly.

**Visible to anyone holding the pages, without the password:**

- that the pages are a BinaryPaper capsule (the `BPQR` magic);
- the wire version and `erasure_alg`;
- `capsule_id`, `K`, `R`, `symbol_len`, and each `symbol_index`;
- therefore the approximate stored size, `K × S`;
- from the preamble: `aead_alg`, `kdf_alg`, `compression_alg`, the KDF parameters,
  `salt`, `nonce`, and `body_len`; and
- whether the capsule is encrypted at all.

**Not visible without the password, in encrypted mode:**

- the file name, text-note content, or folder structure;
- the number of files and their individual sizes;
- the `display_name` from the manifest;
- any file content; and
- any per-entry metadata.

All user-identifying content lives inside the payload package, which is inside the
authenticated body. In encrypted mode the ZIP central directory itself is encrypted,
so entry names are not exposed.

In **plaintext** mode (`aead_alg = 0`) everything is readable by anyone with the pages.

A reader MUST NOT display or log anything from the payload package before stage 8
authentication succeeds.

## 14. Compatibility and versioning

### 14.1 Released versions are immutable

Once a version of this specification is published under a release tag, the meaning of
its bytes is frozen. A conforming implementation written against `1.0` today must keep
working against `1.0` indefinitely.

### 14.2 What requires a new version

A change requires a **new protocol version** if it:

- alters whether a given input is accepted or rejected;
- changes the bytes recovered from a given input;
- reinterprets an existing identifier; or
- changes a mandatory recovery algorithm.

The single exception: explicitly rejecting input that was **already invalid** under
the published rules is a security clarification, not a new version.

### 14.3 What may change as an erratum

Editorial changes that clarify prose without changing accepted bytes or required
outcomes are recorded in [ERRATA.md](ERRATA.md) for the affected version. An erratum
never changes an implementation's required behavior — if it would, it is a version
change instead.

Ambiguous cases stop for review rather than being decided by whichever implementation
shipped first.

### 14.4 Kit version vs wire version

This specification describes capsule wire version **1.0**. The recovery kit that ships
it has its own semantic version (`recovery-kit-vX.Y.Z`) which moves independently: a
kit patch release may fix a tool bug or an erratum while still describing wire 1.0.
Always check which wire version a kit release states it recovers.

### 14.5 Pre-1.0 capsules

Wire versions with `format_major = 0` were **experimental** and are not supported.
There is no compatibility promise for them, and a conforming reader rejects them at
stage 1. This is deliberate: those bytes were produced by prototypes, before the
format was frozen.

## 15. Conformance

An implementation conforms to this specification when it:

1. recovers the exact expected output for every positive vector in the kit's suite;
2. rejects every negative vector with the expected stable error at the expected stage;
3. never allocates on an unvalidated declared value; and
4. never publishes unauthenticated bytes.

Accepted vectors are machine-checkable examples of this document's meaning. Where a
vector and this document disagree, **this document is authoritative** and the vector is
a defect to be fixed before release. The reference CLI is informative: it must pass the
vectors, but its behavior does not define the protocol.

See [`/vectors`](../../vectors) for the suite and its manifest schema.

## 16. References

**Normative**

- [`codecs/erasure16.md`](codecs/erasure16.md) — Reed–Solomon GF(2¹⁶) construction.
- [`codecs/ldpc-staircase.md`](codecs/ldpc-staircase.md) — LDPC-Staircase construction.
- [RFC 2119](https://www.rfc-editor.org/rfc/rfc2119) /
  [RFC 8174](https://www.rfc-editor.org/rfc/rfc8174) — BCP 14 key words.
- [RFC 9106](https://www.rfc-editor.org/rfc/rfc9106) — Argon2.
- [NIST SP 800-38D](https://csrc.nist.gov/publications/detail/sp/800-38d/final) —
  AES-GCM.
- [RFC 3720 Appendix B.4](https://www.rfc-editor.org/rfc/rfc3720) — CRC-32C.
- [Unicode Annex #15](https://unicode.org/reports/tr15/) — NFC normalization.
- ISO/IEC 18004 — QR Code symbology (Byte Mode).

**Informative**

- [RFC 5170](https://www.rfc-editor.org/rfc/rfc5170.html) — the LDPC-Staircase family
  the `erasure_alg = 2` profile follows.
- [SECURITY-CONSIDERATIONS.md](SECURITY-CONSIDERATIONS.md) — threat model and required
  resource behavior.
- [`../registries/algorithms.md`](../registries/algorithms.md) — algorithm identifier
  registry.
- [`../registries/recovery-profiles.md`](../registries/recovery-profiles.md) — recovery
  profile registry.
