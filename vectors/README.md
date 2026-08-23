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

## Coverage

The current suite covers frame structure and field rules, the CRC-32C check, session
identity and duplicate handling, insufficient-frame recovery, preamble structure and
algorithm-combination rules, body bounds, KDF parameter validation, both authentication
modes, and full recovery for stored, LZMA, plaintext, encrypted, Reed–Solomon and LDPC
capsules.

Still to come before the first release: image (PNG/JPEG) vectors, LZMA and ZIP package
negatives, and profile/resource refusals.
