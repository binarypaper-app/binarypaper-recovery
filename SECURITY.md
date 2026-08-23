# Security Policy

## Reporting a vulnerability

**Preferred channel: GitHub Private Vulnerability Reporting.** Use the "Report a
vulnerability" button under this repository's Security tab. It creates a private
advisory visible only to the maintainers, and it is the fastest route to a fix.

If you cannot use GitHub, email **security@binarypaper.app**.

Please do not open a public issue for a suspected vulnerability, and please do not
include a working exploit in a public pull request.

### What helps

- the affected component: specification text, a conformance vector, or the CLI;
- the version, release tag, or commit;
- a minimal input that reproduces it — a frame file or page image is ideal;
- what you expected and what actually happened; and
- whether you believe it affects recovered data, host resources, or both.

Please send only synthetic test data. Do not send a real backup, and never send a
password.

### What to expect

We aim to acknowledge a report within 7 days and to give an initial assessment within
30 days. There is no bounty program.

## Threat model in one paragraph

Every frame, image, and file handed to a recovery implementation is **untrusted**. A
correct CRC-32C proves nothing about intent — a valid-looking header with hostile
counts and lengths is cheap to fabricate. The specification therefore separates what
the wire can *represent* from what an official creator is permitted to *emit*
(Stable Recovery Profile 1) from what a given operator's machine is willing to
*spend*. Implementations validate the declared shape and perform checked arithmetic
before allocating anything, and never publish unauthenticated bytes as restored
content. The details are normative and live in
[`protocol/1.0/SECURITY-CONSIDERATIONS.md`](protocol/1.0/SECURITY-CONSIDERATIONS.md).

## What counts as a vulnerability here

Reports in these categories are in scope:

- **Unbounded resource use from declared values** — a crafted frame, preamble, LZMA
  stream, ZIP entry, or image that causes allocation, disk use, or CPU time
  disproportionate to the input, or that ignores the configured resource policy.
- **Unauthenticated output** — any path where payload bytes reach a parser that
  allocates on declared values, or reach the user as restored content, before the
  AES-GCM tag or plaintext digest has been verified.
- **Path escape** — a payload package that writes outside the chosen output
  directory, or that overwrites an existing file without an explicit user choice.
- **Password exposure** — a password reaching a log, an error message, a JSON report,
  a process argument, an environment variable, or a temporary file.
- **Silent corruption** — an input accepted as a successful recovery whose output
  does not match the original bytes.
- **A specification defect** that makes one of the above possible in a
  specification-conforming implementation. These are the most valuable reports.

Out of scope: findings that require an attacker who already controls the machine, and
the deliberate absence of features listed as out of scope in the README (PDF input,
camera capture, capsule creation).

## Fixes and releases

A CLI security fix produces a **new patch release**. Published release tags and assets
are immutable and are never replaced or deleted; a superseded release is marked as
such in [CHANGELOG.md](CHANGELOG.md), not removed.

A protocol-level security defect is handled under the compatibility policy in the
specification. Tightening a rule so that input which was *already invalid* is now
explicitly rejected is an erratum. Anything that changes whether valid input is
accepted, or changes what bytes are recovered, requires a new protocol version.
