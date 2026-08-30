# Changelog

All notable changes to the BinaryPaper Recovery Kit are recorded here.

The kit version follows [Semantic Versioning](https://semver.org/) and is **independent
of the capsule wire version** it describes. A patch release may fix a CLI or editorial
defect without changing wire behavior; a minor release may add tests or tooling while
still describing the same wire version. Every release states which capsule wire
version it recovers.

Release tags and assets are immutable. A superseded release is marked here; it is
never replaced or deleted.

## [Unreleased]

Preparing the first release, `recovery-kit-v1.0.0`, describing **capsule wire
version 1.0** (`format_major = 1`, `format_minor = 0`).

### Added

- **The specification.** `protocol/1.0/` defines capsule wire version 1.0 byte by byte,
  with the required reader behaviour, the validation order, the stable failure categories,
  and the threat model in `SECURITY-CONSIDERATIONS.md`. `protocol/registries/` pins the
  algorithm and recovery-profile identifiers.
- **The conformance suite.** 54 machine-checkable vectors — 7 positive, 40 negative, 5
  image and 2 image-negative — each with a manifest, all hash-pinned from a single
  aggregate `MANIFEST.json`.
- **The reference recovery tool.** `binarypaper inspect`, `recover` and `verify-vectors`,
  reading PNG/JPEG page images and raw `.bpq` frames, entirely offline.
- **Release machinery.** Self-contained archives for six platforms, a source archive, an
  SPDX SBOM, `SHA256SUMS`, a publication audit over the tree and the full history, a vector
  integrity check, and an offline recovery drill run against the artifact being published.
- Repository governance: license, notice, security policy, contribution terms, and
  trademark terms.
- Recovery from photographed pages, not only scanned ones. Every located symbol is
  rectified and retried across sampling densities, blur levels and binarizers, and a
  symbol that decodes is used to predict its lattice neighbours, so symbols whose own
  finder patterns were never detected are still lifted out of the page.
- **A time bound on page-image decoding**, defaulting to 120 seconds and configurable with
  `--max-image-seconds` (`0` disables it). The retry ladder above stops at the first clean
  decode, so it costs least on pages that read and most on pages that do not — which makes
  an ordinary photograph of something that is not a page the expensive input. Neither the
  byte cap nor the pixel cap bounds that: the most expensive legitimate page measured is a
  1.4 MB file. When the bound is reached, the page reports the codes it had already
  decoded and says it stopped early.
- **Progress reporting while a page is worked**, so a slow page is visibly alive.
- **Page images are read in two passes.** The cheap whole-page sweep runs over everything first,
  and the expensive rectify-and-retry stage runs only if the codes collected are not yet enough to
  recover the capsule. On a corpus of twelve photographs of a 48-symbol page the sweep alone
  recovered all 45 readable symbols, and rectification added none at eight times the cost.
- **The second pass reads each page in its own child process.** A native decoder handed a damaged
  image can corrupt its memory and take the process down with no catchable error, which would
  otherwise discard the frames already recovered from every other page. A page whose child dies is
  retried once in a fresh process, then reported and skipped.
- An image above the pixel cap is now refused **from its header**, without being decoded, as the
  specification requires. Encoded size does not predict decoded size — a 439 KB file can declare
  400 megapixels — so deciding after the decode meant the allocation the cap exists to prevent had
  already happened. Decoding is also inside the per-page time budget now, rather than before it.
- `THIRD-PARTY-NOTICES` in every release archive, assembled from `third-party/` and from
  the .NET runtime pack the archive was published against. The archives are a single
  self-contained binary, so the terms of everything inside it travel with it.

### Changed

- The QR decoder is now `ZXingCpp` (the zxing-cpp project's .NET binding), replacing
  `ZXing.Net`. `ZXing.Net` builds its sampling grid from a single alignment pattern and
  read **no** version-40 symbol from a photographed page — not even after the symbol was
  perspective-corrected into a clean square. On twelve photographs of a 48-symbol page it
  recovered 0 frames where the kit now recovers 45. `ZXing.Net` remains in the repository
  only as the image-vector *generator*, which is test tooling and is not shipped.
- **The kit is no longer a single build that runs on every platform .NET runs on.**
  `ZXingCpp` ships native assets for `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`,
  `osx-x64` and `osx-arm64`. Those are the six platforms the kit publishes; anywhere else
  you must build zxing-cpp yourself, or work from the specification.

Nothing has been released yet. Until `recovery-kit-v1.0.0` is tagged, no compatibility
promise is in force and anything in this repository may change.
