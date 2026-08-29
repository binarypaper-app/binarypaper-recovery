# Conformance Vectors

Machine-checkable examples of what [the specification](../protocol/1.0/SPEC.md) means.

Every vector is a directory containing its input bytes and a `manifest.json` describing
exactly what an implementation must do with them. Nothing here is decorative: if a rule
matters, a vector pins it.

```text
vectors/
  MANIFEST.json                       every vector, hashed
  schema/vector-manifest.schema.json  the manifest contract
  1.0/positive/<id>/                  inputs that must recover exactly
  1.0/negative/<id>/                  inputs that must be rejected, at a named stage
  1.0/images/<id>/                    page images that must decode to exact frame bytes
```

## Authority

These vectors are **examples of the specification**, not the specification. Where a
vector and [SPEC.md](../protocol/1.0/SPEC.md) disagree, the specification wins and the
vector is a defect to be fixed. The reference CLI is informative and must pass them.

## Running them

```bash
dotnet run --project ../cli/src/BinaryPaper.Recovery.Cli -- verify-vectors .
```

Any implementation can run the suite: read `MANIFEST.json`, follow each manifest, and
compare. The manifests are deliberately language-neutral — `/` paths on every host,
every external file hashed, and unknown properties rejected so a typo fails loudly
instead of silently disabling a check.

## What a manifest says

```jsonc
{
  "schemaVersion": 1,
  "protocol": { "formatMajor": 1, "formatMinor": 0 },
  "id": "frame-digest-mismatch",
  "category": "negative-frame",     // family, for grouping
  "operation": "recover",           // or "inspect", which stops before authentication
  "inputs":  [ { "path": "frame-0.bpq", "length": 31, "sha256": "..." } ],
  "expected": {
    "result": "frame.digest-mismatch",   // stable failure category, or "success"
    "stage":  "frame"                    // where it must be rejected
  },
  "provenance": { "derivedFrom": "...", "mutation": "...", "crcRecomputed": false }
}
```

## Negative vectors and the CRC trap

Every negative vector is **derived** from a positive one by a named mutation, recorded
in `provenance`. That keeps the difference between "the input that works" and "the input
that must be rejected" down to exactly the thing under test.

The subtle part is `crcRecomputed`. A mutation intended to fail *after* the frame-digest
check **must** recompute the CRC-32C, or it silently tests the CRC check instead of the
rule it claims to test — and passes for the wrong reason. Each manifest records which it
did.

The same trap has a second form: a vector that is rejected for the right *reason* at the
wrong *stage*. That is a conformance failure too, because the stage is what tells a user
whether their pages are damaged, their password is wrong, or their backup was never
valid. Manifests pin the stage, and the suite runner checks it.

## Stage vocabulary

`frame` · `session` · `profile` · `recovery` · `preamble` · `authentication` ·
`compression` · `package` · `output`

These correspond to the numbered stages in
[SPEC.md §11](../protocol/1.0/SPEC.md#11-validation-order).

## Test data

Every fixture is **synthetic public test data**. Fixture passwords are labelled
`public-test-value` and are deliberately not examples of good passwords — they are short,
obvious, and published. Do not copy them anywhere real.

Text artifacts are byte artifacts, not platform-native text files. `.gitattributes` marks
them binary so their LF bytes survive checkout on every platform, and every manifest hash
covers the exact committed bytes.

## Image vectors

`operation: "decode-image"` vectors stop after QR decoding and check the **frame bytes
that came out of the pixels**. They are derived from the capsule vectors, so the codes
carry exactly the frame bytes already pinned elsewhere in this suite: an image vector that
passes proves the whole path from pixels to restored content, and a difference can only
have come from the image layer.

Frames are compared **by hash, not by count**. A decoder that finds the right number of
symbols but recodes their payloads through text passes a count check and fails here — and
`image-binary-payload-png` exists precisely to catch it. Its codes carry ciphertext, so
their payloads contain `0x00` and are not valid UTF-8 in any encoding. A reader with the
text-recoding bug fails that one vector while every text-friendly vector still passes,
which is what makes the failure diagnosable instead of mysterious.

The images are rendered deterministically from the frame bytes at fixed module size, quiet
zone, rotation and JPEG quality, so regenerating reproduces them byte-for-byte.

## Regenerating

Two generators, run in this order:

1. the capsule suite (positive and negative vectors), which rewrites `MANIFEST.json`;
2. the image suite, which **merges** its entries back into `MANIFEST.json`.

Running them the other way round drops the image entries from the aggregate manifest.

## Coverage

54 vectors: **7 positive**, **40 negative**, **5 image** and **2 image-negative**.

| Family | Count | What it pins |
| --- | --- | --- |
| positive | 7 | Full recovery for stored, LZMA, plaintext, encrypted, Reed–Solomon and LDPC capsules |
| `frame-*` | 16 | Frame structure, field rules, and the CRC-32C check |
| `package-*` | 10 | ZIP structure, manifest rules, entry paths, and the stored-only method |
| `preamble-*` | 5 | Preamble structure, algorithm combinations, body bounds, KDF parameters |
| `auth-*` | 3 | Both authentication modes, and a wrong password |
| `session-*` | 2 | Session identity and duplicate handling |
| `compression-*` | 2 | Malformed compressed input |
| `profile-*`, `resource-*`, `recovery-*` | 3 | Profile refusal, resource refusal, insufficient frames |
| image | 5 | PNG and JPEG decoding: a single page, a rotated page, a multi-code page, a binary payload |
| image-negative | 2 | An unreadable code beside recoverable repair symbols; mixed capsules across images |

Every rejection stage the specification defines has at least one vector. Coverage is not
the same as completeness — a negative vector for a rule nobody has thought to break yet is
still worth adding, and new families are welcome.

### What the suite deliberately does not pin

**Timing.** No vector asserts how long anything takes. A conformance suite that failed on
a slow machine would teach everyone to ignore it. Resource *limits* are pinned as
behaviour — `resource-declared-output-too-large` requires a refusal — but durations are
measured in the benchmarks, not asserted here.

**Image-detection yield.** An image vector pins the frame bytes a decoder must produce
once it has decoded a symbol, not how many symbols a given detector finds in a
photograph. Two conforming readers may legitimately differ on what they can see; they may
not differ on what the bytes mean.
