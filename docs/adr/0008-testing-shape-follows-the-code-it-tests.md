# ADR 0008 — The testing shape follows the code it tests, not one house rule

- **Status:** Accepted
- **Date:** 2026-08-01
- **Relates to:** `.claude/skills/insolvia-testing` owns *how* to write a test
  here and must not be restated in this file; issue #40 owns the staging E2E
  suite's phasing; [ADR 0001](0001-client-stays-dumb-trust-boundary.md) is why
  a trust boundary sits between the two halves this ADR treats differently;
  [`docs/reference/architecture.md`](../reference/architecture.md) owns the
  required-check contract these suites run under.

## Decision

**There is no single testing shape for this repo. Each area gets the shape
that matches where its bugs actually come from, and this ADR is the record of
which is which — so that the difference reads as deliberate rather than as
drift.**

| Area | Shape | Because |
|---|---|---|
| `services/api`, `services/mailer` | **Pyramid** — mostly unit, over a real domain layer | `core/` is a genuine thick domain: validation, HMAC minting, MIME assembly, claim verification. It is pure, fast, and where the logic errors live. |
| `apps/insolvia_app`, `packages/insolvia_design_system` | **Trophy** — integration-weighted, over static analysis | A component's bugs are in wiring, not arithmetic. `tsc --strict` and ESLint already own the class of error a unit test would catch here. |
| `packages/insolvia_api_client` | **Contract pin** — one seam, tested exhaustively | It has almost no logic. Its entire job is to agree with `services/api` about paths, fields and status codes. |
| `e2e/` | **Deliberately scarce** | Slow, environment-dependent, and a flake here blocks production promotion via `verified-commit`. |

Two rules cut across all four, both taken from
[Fowler's pyramid](https://martinfowler.com/articles/practical-test-pyramid.html)
because they are shape-independent:

- **Push tests down.** Verify a behaviour at the cheapest level that can
  actually observe it.
- **A high-level failure with no low-level failure is a missing test.** When
  the staging E2E catches something the unit suites did not, the fix is two
  commits: the bug, and the lower test that should have caught it.

**Static analysis counts as the base layer**, not as a separate concern. `mypy
--strict` over `services/*/src`, `tsc` with `erasableSyntaxOnly`, ESLint, ruff
and `terraform validate` are load-bearing test coverage. This is the one part
of the trophy this repo adopts everywhere, including in the pyramid half.

**A fourth category exists that neither model names: executable architecture.**
`tests/test_architecture.py` in both Python services AST-scans imports to
enforce the `core / api / adapters / entrypoints` layering. These are not unit
tests of behaviour — they are a decision made unforgeable. **Prefer this
category whenever a rule would otherwise live only in prose.** The same
instinct produced `auth-callback.test.tsx`, which pins a route *file's location*
to the callback URL Terraform registers, and the EAS guard in `app-pr.yml`.

**E2E gets one job: prove the seams that no other layer can reach.** Not
feature coverage. Per issue #40 it must gate `verified-commit` and must never
become a required PR check — a merge gate that depends on a deployed
environment puts staging's availability on every PR's critical path.

## Context

The repo has roughly 355 tests across six units and four runners, and the
per-area conventions genuinely differ: the app leans on `renderRouter` and
`userEvent` against real component trees, the Python services lean on pure
functions with in-memory fakes, and the api-client is one file of stubbed
`fetch` calls asserting literal JSON.

That is the right answer arrived at informally, and the problem with an
informal right answer is that it is indistinguishable from an accident. The
next person to touch this — reasonably — either flattens it toward one house
style, or reads the app's thin unit layer as a coverage gap and starts filling
it with tests that `tsc` already makes impossible to fail.

The industry framing helps less than it looks. The
[pyramid](https://martinfowler.com/articles/practical-test-pyramid.html)
optimises for speed and failure isolation; the
[trophy](https://kentcdodds.com/blog/the-testing-trophy-and-testing-classifications)
optimises for confidence per unit of effort, on the principle that *the more
your tests resemble the way your software is used, the more confidence they can
give you*. Both are right about different code. The
[current consensus](https://www.baytechconsulting.com/blog/test-pyramid-vs-testing-trophy-whats-the-difference)
is that the shape is a tool rather than a commandment, and the useful question
is where the bugs come from. This repo happens to contain both kinds of code,
behind one trust boundary, which is why picking one shape globally would be
choosing to be wrong about half of it.

## Consequences

- **"Low unit-test count" is not a defect report for the app.** A PR that adds
  unit tests asserting what the type system already guarantees is noise, and
  should be pushed toward an integration test that renders something.
- **`core/` staying pure is now a testing decision as well as an architecture
  one.** The pyramid half only works while the domain layer has no I/O to mock.
  Anything that pushes network or AWS into `core/` degrades the cheapest, most
  valuable tests in the repo — and `test_architecture.py` will fail first.
- **The seam between the two halves is covered by contract pins, not by
  end-to-end coverage.** `packages/insolvia_api_client`'s tests are the
  agreement; when `services/api` changes a field name, those tests are supposed
  to be what breaks. This is why they are hand-written rather than generated —
  generated clients regenerate and the break disappears.
- **Adding an E2E test now needs a justification the other layers cannot
  satisfy.** "It would be good to check" is not one.
- **AWS adapters remain the least-covered code by design.** `DynamoDbWaitlistStore`,
  `SigV4MailerClient` and `CognitoJwksProvider` are tested against
  monkeypatched transports; real SigV4 signing and DynamoDB call shapes are
  first exercised on staging. That is an accepted risk of having no `moto` and
  no emulator, and it is the strongest argument for the E2E suite growing along
  #40's phases.
- **Coverage percentage is not adopted as a gate.** Ratcheting a number rewards
  the tests this ADR says not to write. What replaces it is the second
  cross-cutting rule above: every escaped bug owes a lower-level test.

## Alternatives considered

**One shape everywhere — pyramid.** Rejected because it prescribes a thick unit
layer for the app and the design system, where the units are components whose
interesting behaviour only exists once they are composed and rendered. It would
produce exactly the brittle, implementation-coupled tests that
[the literature warns about](https://kentcdodds.com/blog/common-mistakes-with-react-testing-library),
and it would duplicate what `tsc --strict` already proves.

**One shape everywhere — trophy.** Rejected for the mirror reason. The Python
services' `core/` is pure domain logic with a high branch count — parameterised
unit tests are both the cheapest and the most precise way to cover it, and
`test_waitlist.py` alone would become a far slower, vaguer integration suite
for no gain in confidence.

**Adopt a coverage threshold instead of a written shape.** Rejected: a
percentage is agnostic about which tests it is buying, so it is satisfied
equally by a test that would have caught a real defect and one that asserts a
getter returns what was set. It also cannot express any of the four rows in the
table above. Measuring coverage as *information* is fine and is not forbidden
here; gating on it is what this rejects.

**Leave it undocumented and let each area's `CLAUDE.md` imply it.** Rejected —
that is the status quo, and it is why this ADR exists. The per-area files
correctly say *how* to write a test in that directory; none of them says why
the directories disagree, which is the thing that gets "fixed" by someone
tidying up.
