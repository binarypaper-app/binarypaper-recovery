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

- Repository governance: license, notice, security policy, contribution terms, and
  trademark terms.
- Recovery from photographed pages, not only scanned ones. Every located symbol is
  rectified and retried across sampling densities, blur levels and binarizers, and a
  symbol that decodes is used to predict its lattice neighbours, so symbols whose own
  finder patterns were never detected are still lifted out of the page.

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
