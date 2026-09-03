# Errata — Capsule Protocol 1.0

Corrections to [SPEC.md](SPEC.md) for capsule wire version 1.0.

## Scope of this file

An **erratum** clarifies prose without changing behavior. It records where the text
was ambiguous, wrong about itself, or misleading, and states what it should have said.

An erratum **never**:

- changes whether an input is accepted or rejected;
- changes the bytes recovered from an input;
- reinterprets an identifier; or
- changes a mandatory recovery algorithm.

Any of those requires a **new protocol version**, not an entry here. The single
exception is explicitly rejecting input that was already invalid under the published
rules, which is a security clarification and is recorded here as such.

If you believe you have found a defect, see
[CONTRIBUTING.md](../../CONTRIBUTING.md#reporting-a-specification-defect). Ambiguities
are resolved by maintainers, not by whichever implementation shipped first.

## Format of an entry

```text
### E-1.0-NNN — short title

Reported:   YYYY-MM-DD
Applies to: the release tags affected
Class:      clarification | security clarification
Section:    the section of SPEC.md

**Text as published.** What the document said.

**Problem.** Why it was ambiguous, wrong, or misleading.

**Corrected reading.** What it means, normatively.

**Implementation impact.** What a conforming implementation must do differently, if
anything. Usually "none — conforming implementations already behave this way."
```

## Entries

*None.*

No erratum has been recorded against Capsule Protocol 1.0. The text was frozen at the
`recovery-kit-v1.0.0` tag; from that point a defect is recorded above rather than being
corrected in [SPEC.md](SPEC.md) directly.
