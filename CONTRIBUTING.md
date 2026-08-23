# Contributing

Thanks for looking. This repository has an unusual shape: most of it is a
**specification** and a **frozen conformance suite**, not ordinary application code.
What that means for contributions is spelled out below.

## Licensing and sign-off

Contributions are accepted under the [Apache License 2.0](LICENSE).

Every commit must carry a Developer Certificate of Origin sign-off:

```bash
git commit -s -m "Your message"
```

That adds a `Signed-off-by:` line and certifies you have the right to submit the work
under this license (see [developercertificate.org](https://developercertificate.org/)).
There is no CLA.

## What can and cannot change

| Change | Allowed |
| --- | --- |
| CLI bug fix, refactor, performance work, better diagnostics | Yes, normal pull request |
| New tests, new tooling, documentation clarity | Yes |
| Editorial fix to specification prose that does not change behavior | Yes, as an erratum |
| A **new** conformance vector for behavior the spec already defines | Yes, with maintainer review |
| Changing an accepted byte, hash, or expected outcome in a **released** vector suite | No |
| Changing what a released protocol version means | No |

A released protocol version is immutable. If you believe a released version is wrong,
open an issue — the fix is an erratum or a new version, decided by maintainers, never
an edit to shipped bytes.

**The CLI is not the specification.** A pull request that changes CLI behavior to
disagree with the specification will be asked to change the specification first, or to
fix the CLI instead. This is deliberate: if implementation behavior could silently
become the contract, a shared bug becomes the format.

## Before you open a pull request

Run the suite:

```bash
dotnet test cli/BinaryPaper.Recovery.slnx
```

and, for anything touching recovery behavior, the vectors:

```bash
dotnet run --project cli/src/BinaryPaper.Recovery.Cli -- verify-vectors vectors/
```

Both must pass. A change that makes a vector fail is either a bug in your change or a
specification defect — say which one you think it is in the pull request.

## Reporting a specification defect

These are the most valuable reports and they are not bugs in the ordinary sense. Open
an issue describing:

- the exact section and sentence;
- two conforming implementations that could reasonably disagree, or one input whose
  handling the text does not determine; and
- what you think the intended behavior is.

Ambiguities are resolved by maintainers and recorded in `ERRATA.md` for the affected
version.

## Security

Do not open a public issue or pull request for a suspected vulnerability. Follow
[SECURITY.md](SECURITY.md).

## Test data

Every fixture in this repository is synthetic public test data. Do not contribute a
real backup, a real personal file, or a password you use anywhere. Fixture passwords
are labelled as test values and are deliberately not examples of good passwords.

## Naming

Please do not add BinaryPaper branding, logos, or endorsement language to a
contribution. See [TRADEMARKS.md](TRADEMARKS.md).
