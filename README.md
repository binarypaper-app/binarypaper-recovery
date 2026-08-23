# BinaryPaper Recovery Kit

**Open recovery specification and reference recovery tool for the BinaryPaper capsule
protocol.**

A BinaryPaper backup is a set of QR codes printed on paper. This repository contains
everything needed to get the original bytes back out of those pages — the byte-level
specification, machine-checkable conformance vectors, and a working recovery tool —
under the Apache License 2.0.

> **Status: unreleased.** This repository is being prepared and is not yet public. No
> release has been tagged, and no compatibility promise is in force until one is. See
> [CHANGELOG.md](CHANGELOG.md).

## What this is for

If BinaryPaper the product were to disappear tomorrow, your printed backups would
still be recoverable. That claim is only worth something if it can be checked, so the
recovery half of the system is published in full:

1. every byte needed to recover a stable BinaryPaper capsule;
2. positive and negative conformance vectors that pin the exact accept/reject
   behavior;
3. recovery from scanned or photographed pages with no account, no server, no
   entitlement, and no BinaryPaper application; and
4. source you can build, audit, fork, and redistribute.

## What this is not

**BinaryPaper is not open source.** This repository is the recovery surface only. The
creator applications, calibration research, page-layout optimization, account and
entitlement systems, and backend services are not published here and are not covered
by this license.

Publishing this kit proves the *format* is reviewable and that recovery does not
depend on us. It does not prove anything about the contents of closed BinaryPaper
application binaries.

The reference tool reads **page images (PNG/JPEG) and raw frame files**. It does not
read PDFs, does not drive a live camera, and does not create backups. See
[Scope](#scope).

## Layout

```text
protocol/1.0/      the normative specification for capsule wire version 1.0
protocol/registries/  stable algorithm and recovery-profile identifiers
vectors/           conformance vectors with a machine-readable manifest per case
cli/               the reference recovery implementation (.NET)
benchmarks/        resource-measurement corpus and results
release/           release manifest schema
```

## Scope

| In scope | Out of scope |
| --- | --- |
| Recovering stable capsule wire 1.0 | Creating backups |
| PNG and JPEG page images | PDF input |
| Directories of images; multiple QR codes per image | Live camera capture |
| Raw `.bpq` frame files (diagnostics, conformance) | Calibration and print-layout choice |
| Offline operation, always | Accounts, entitlements, telemetry, update checks |

Experimental pre-1.0 capsules are **not** supported. Prototype printouts made before
wire 1.0 was frozen carry no recovery promise.

## Conformance model

Three kinds of evidence, in descending authority:

1. **The specification** defines meaning. It is normative.
2. **The vectors** are machine-checkable examples of that meaning. A released
   version's vectors are frozen with it.
3. **The reference CLI** is informative. It MUST pass the vectors, but it is not the
   byte oracle — if the tool and the specification disagree, the specification wins
   and the tool is fixed.

If the specification, a vector, and the CLI disagree, a release stops until the
disagreement is resolved in the draft. We do not bless implementation behavior by
shipping it.

## Compatibility claims

Anyone may truthfully state that their implementation passes a named version of this
conformance suite, or that it is compatible with a named capsule protocol version.
There is no certification program and no "certified" mark. See
[TRADEMARKS.md](TRADEMARKS.md) for what you may and may not imply.

## Security

Every QR payload, image, and file this tool reads is untrusted input. See
[SECURITY.md](SECURITY.md) for the reporting channel and
`protocol/1.0/SECURITY-CONSIDERATIONS.md` for the threat model and required reader
behavior.

## License

Apache License 2.0 — see [LICENSE](LICENSE) and [NOTICE](NOTICE).

Apache-2.0 permits commercial use, modification, and redistribution, and includes a
contributor patent grant limited to claims necessarily infringed by the contribution.
It does not grant trademark rights and does not license any third party's patents.
