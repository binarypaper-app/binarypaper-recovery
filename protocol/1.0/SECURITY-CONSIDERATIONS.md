# Security Considerations — Capsule Protocol 1.0

This document is **normative** where it uses BCP 14 keywords. It is a companion to
[SPEC.md](SPEC.md), not a summary of it.

## 1. The core assumption

**Every frame, page image, and file a recovery implementation is handed is untrusted
input.**

This is easy to get wrong, because the mental model of the product is reassuring: a
user photographs their own backup and gets their own file back. The implementation
model must be the opposite. A recovery tool is a parser for attacker-controlled binary
data that also happens to be useful to legitimate users. Treat it that way.

Concretely, an attacker who can hand you an image or a `.bpq` file controls:

- every header field, including all counts and lengths;
- the frame digest, which they can recompute after any change;
- the preamble, including the Argon2id cost parameters;
- the LZMA stream and its declared output size; and
- the ZIP structure, entry names, and entry count.

They do **not** control the AES-GCM tag or the plaintext digest without the key, which
is why [stage 8](SPEC.md#11-validation-order) is the boundary that matters.

## 2. CRC-32C is not authentication

`frame_digest` is a **CRC-32C**. It detects accidental damage, which is its job: burst
errors are the dominant paper-damage mode and CRC-32C is good at those.

It provides **no** integrity guarantee against a deliberate attacker. CRC-32C is not
keyed and not collision-resistant. Recomputing it after altering a header takes
microseconds.

Implementations MUST NOT treat a valid frame digest as evidence of anything except
"these bytes are internally consistent". In particular a valid CRC MUST NOT be used to
justify allocating on declared values.

## 3. Resource exhaustion

This is the most likely real attack, and the most likely accidental failure.

### 3.1 Validate before you allocate

A reader MUST validate a declared value against its rule **before** using it to size an
allocation, index a buffer, or bound a loop. A reader MUST NOT speculatively allocate
the wire maximum.

The dangerous products are `K × S` (the padded stored payload) and, for Reed–Solomon,
the `8R²` decoder term. Both are reachable with small header edits.

### 3.2 Checked arithmetic

All arithmetic on declared values MUST be overflow-checked or performed in a width that
cannot overflow. See [SPEC.md §11.2](SPEC.md#112-checked-arithmetic). An overflow that
wraps to a small number turns a bounds check into an exploit primitive: the check
passes, the allocation is small, and the subsequent write is not.

### 3.3 Profile before decoder state

[Stable Recovery Profile 1](SPEC.md#12-stable-recovery-profile-1) exists so that a
reader can refuse expensive-but-well-formed input **before** building symbol tables,
graphs, or residual matrices. The profile check belongs at stage 5, ahead of any
allocation proportional to `K`, `R`, or `S`.

Measuring the largest legitimate backup proves you support the official writer
envelope. It does not make a fabricated header inside that envelope safe, and it says
nothing about a header outside it.

### 3.4 KDF parameters

`kdf_memory_kib`, `kdf_iterations`, and `kdf_parallelism` come from the untrusted
preamble and are fed to Argon2id, whose entire purpose is to be expensive.

A reader MUST validate them before invoking the KDF. A capsule declaring a
multi-gigabyte memory cost is a denial-of-service attempt, not a stronger backup. A
reader SHOULD accept costs at or below the profile values and SHOULD require an
explicit operator override above them.

### 3.5 Decompression

LZMA can expand enormously. A reader MUST NOT decompress unauthenticated bytes at all
(stage 8 precedes stage 9), which removes the attacker-supplied-bomb case for anyone
who does not hold the key.

For authenticated content, a reader SHOULD preflight the declared output size against
available disk and its resource policy, and SHOULD stream rather than materialize the
whole decompressed package where practical.

A reader MUST NOT impose an arbitrary low global cap on decompressed size. A
legitimate, highly compressible backup can have a small capsule and a large recovered
package, and refusing it would silently make a valid backup unrecoverable — the exact
failure this protocol exists to prevent. Bound by policy and available disk, not by a
guess.

### 3.6 Images

Image decoding is a large attack surface with a long history of memory-safety bugs.

A reader that accepts images MUST:

- determine file type by **content**, not by file extension;
- reject dimensions or total pixel counts above a documented default **before** full
  decode;
- bound the **time** spent decoding one image, with a documented default;
- process a directory of images incrementally rather than loading all of them; and
- release decoded pixel buffers promptly.

A per-image decode failure MUST NOT discard frames already recovered from other images.

#### Why time, and not only size

Byte and pixel caps bound how much data a reader accepts. They do not bound how much
work it does with it, and for image decoding the two come apart sharply.

Detection cost grows faster than linearly in pixels, because the number of candidate
symbol positions grows with area. Any reader that retries a candidate — at several
sampling densities, blur levels, or thresholds — multiplies that cost, and it does so
**most** on images where nothing decodes, because a retry ladder that stops at the first
success pays its full price only where there is no success to be had. The expensive input
is therefore an ordinary photograph of something that is not a page, not a rare crafted
one.

Nor does the encoded size predict the work: page images are mostly white and compress
accordingly, so a file of a few megabytes can legitimately expand into the largest and
most expensive image a reader accepts.

A reader MAY choose any default it can justify for its platform — a phone and an
overnight forensic run are not the same problem — and MAY allow the bound to be disabled
by explicit configuration. What it MUST NOT do is spend unbounded time on one image.

#### Stopping early

When the bound is reached, a reader MUST return the QR payloads it has already decoded
and MUST report that the image was not fully processed. It MUST NOT present a
partially-processed image as a fully-read one.

This is not in tension with the "no partial success" rule in the capsule pipeline. Each
payload returned is a complete QR symbol, and every one of them is still validated as a
frame — structure, field rules, and CRC-32C — before it can influence a session. An image
yielding fewer frames than it contains is the ordinary case the erasure layer exists for,
and the recovery threshold is evaluated across the whole scan rather than per image.

A reader SHOULD report progress while working an image. A bound the user cannot
distinguish from a hang does not help the user.

## 4. Authentication is the boundary

**No unauthenticated byte may be published as restored content, and no unauthenticated
byte may reach a parser that allocates on declared values.**

Everything after stage 8 — LZMA, ZIP, the manifest, entry names — operates on content
that the AES-GCM tag or the plaintext digest has already vouched for. That ordering is
what makes those parsers tractable.

### 4.1 Wrong password and tampering are indistinguishable

A wrong password and a modified ciphertext or tag both surface as an AES-GCM
authentication failure. They are cryptographically indistinguishable, and an
implementation MUST NOT claim to tell them apart. Reporting "wrong password" for what
is actually tampering, or vice versa, is a false diagnostic that misleads a user at
exactly the wrong moment.

A single authentication-failed outcome covering both is correct and honest.

### 4.2 Plaintext mode is integrity, not confidentiality

`aead_alg = 0` protects against accidental corruption. It protects against nothing
else. Anyone holding the pages can read the content. An implementation SHOULD make
that clear when it restores a plaintext capsule, and MUST NOT present it as protected.

## 5. Output safety

A reader MUST:

- write recovered content into a destination the user chose;
- refuse to overwrite existing content unless the user explicitly opted in;
- reject any entry path that is absolute, contains a `.` or `..` segment, or is
  otherwise outside the destination (see [SPEC.md §10.6](SPEC.md#106-payload-package--zip-with-stored-entries));
- reject symbolic links and other non-regular entries rather than creating them;
- treat the manifest `display_name` as untrusted text and never use it directly as a
  path; and
- not publish partial output as a successful result.

On failure or cancellation, a reader MUST remove or clearly mark partial output. A
half-written directory that looks like a successful recovery is worse than a clean
failure, because the user may discard the paper.

For a file-tree payload, saving the authenticated ZIP as-is is the safest default.
Extraction is where path escapes happen; if you extract, do it with the checks above.

## 6. Password handling

A password MUST be read from a hidden interactive prompt or from protected standard
input.

Documentation MUST NOT recommend passing a password as a command-line argument. Command
lines are visible to other processes and are recorded in shell history.

A password MUST NOT appear in:

- a log at any level;
- an error message;
- a machine-readable report;
- a temporary file; or
- a crash dump, where this is within the implementation's control.

An implementation SHOULD keep validated frames in memory after an authentication
failure so the user can retry without rescanning
([SPEC.md §11.1](SPEC.md#111-accepted-frames-survive-a-failed-password)). This is a
usability requirement with a security consequence: a tool that discards the session on
a typo trains users to write their passwords down.

## 7. Timing

Recovery is not a constant-time operation and cannot be. Decode time depends on how
many symbols were lost, which is inherent to erasure coding.

Within that, an implementation SHOULD use a constant-time comparison for the AES-GCM
tag and the plaintext digest, and SHOULD NOT branch on secret-dependent data in ways
that leak more than the shape of the input already does.

The realistic threat here is low: an attacker who can time your recovery already holds
your pages. Do not trade correctness for timing hardening.

## 8. Metadata exposure

[SPEC.md §13](SPEC.md#13-what-is-visible-before-decryption) states exactly what is
readable without the password. Summarized: the capsule's existence, shape, size class,
and cryptographic parameters are visible; all user content and naming is not.

Implementations MUST NOT widen that set. In particular an `inspect`-style command MUST
report only pre-authentication frame and session metadata, and MUST NOT attempt to
surface anything from inside the body.

Diagnostic output MUST NOT include an image file path or scan source in restored
content metadata. Where a recovered file came from is the operator's business, not part
of the payload.

## 9. Supply chain

A release of this kit publishes checksums, an SBOM, and build provenance so that a user
can verify a binary **before executing it**. An implementation distributed without
those cannot be verified by the person who most needs to verify it — someone recovering
a backup years later, possibly offline.

Verification instructions MUST work before the binary is run.

## 10. What is deliberately not defended against

Stated plainly so nobody assumes otherwise:

- **An attacker who already controls the machine.** If they can modify the recovery
  tool, nothing in the format helps.
- **A weak user password.** Argon2id raises the cost of guessing; it does not fix a
  four-character password. The KDF parameters are recorded in the clear precisely so a
  future writer can raise them.
- **Physical access to plaintext-mode pages.** That is what encrypted mode is for.
- **Traffic analysis of the pages themselves.** Anyone holding them learns the capsule
  exists and roughly how large it is.
