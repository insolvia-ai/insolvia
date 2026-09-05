# ADR 0021 — Four test tiers, each aimed at the environments that can answer it; seed fixtures from one shared bucket

- **Status:** Accepted
- **Date:** 2026-09-05
- **Relates to:** [ADR 0008](0008-testing-shape-follows-the-code-it-tests.md)
  owns the *shape* of a suite per area and is unchanged by this; this ADR owns
  which **tier** a test belongs to and which **environment** each tier runs
  against. [ADR 0009](0009-a-case-belongs-to-a-firm.md) is why an environment
  has to be seeded before anything signed-in can be tested.
  [ADR 0001](0001-client-stays-dumb-trust-boundary.md) is why production gets
  no test identity. The `insolvia-testing` skill owns *how* to write a test in
  each tier and must not be restated here.

## Decision

**Every suite in this repo belongs to exactly one of four tiers, named by what
a test in it may touch, and each tier runs against a fixed set of
environments.** The tier is a directory, because a test's guards are its
directory's conftest, so "which tier am I in" and "which rules apply to me"
are one question.

| Tier | Touches | Runs against | When |
|---|---|---|---|
| **unit** — `tests/unit/` (Python), colocated `*.test.ts(x)` (TypeScript) | memory adapters, mocks, the Flask test client; **no network, no AWS, no secret** | nothing deployed | every PR check; every `git push` (pre-push hook); a bare `pytest` / `npm test` |
| **integration** — `services/api/tests/integration/` | a **running API over HTTP**, signed in as a seeded person by real Cognito SRP | **dev** (this machine's stack, `dev-test-integration.sh`) and **staging** (`api-staging.yml`, after the alias shift) | post-deploy on staging; on demand locally |
| **e2e `flows`** — `e2e/tests/flows/` | a real browser, a real sign-in, the deployed bundle | **dev** and **staging** | post-deploy on staging (`app-staging.yml`); on demand locally |
| **e2e `smoke`** — `e2e/tests/smoke/` | HTTP and a browser, **no credentials** | dev, staging, **and production** | post-deploy on staging and on production (`app-prod.yml`, the release's last leg) |

Three rules fall out of the table:

- **A bare `pytest` is the unit tier and nothing else.** `testpaths` in every
  Python unit's `pyproject.toml` points at `tests/unit`, and the integration
  directory is additionally skipped at collection unless `INSOLVIA_INTEGRATION=1`
  — belt and braces, because a suite that reaches a real table by accident on a
  laptop with the wrong credentials exported is a bad afternoon.
- **Nothing signed-in ever runs against production.** The `flows` project is not
  offered when `E2E_TARGET=production`, the integration tier has no production
  target, and the seed loader refuses a prod table outright. Production is
  tested by the `smoke` project alone — a *detector*, not a gate: by the time it
  runs the bundle is live, and what it buys is a broken deploy announcing itself
  in minutes with a named cause rather than a customer finding it.
- **Staging is tested by every tier that can reach it, and its failure blocks
  production.** Integration and both e2e projects run inside the staging stage
  of `release.yml`; the production stage sits behind them in the same run, and
  `verified-commit` reads the status that stage stamps.

**Seed data is one fixture per environment (`seeds/<env>.json`) plus versioned,
shared case fixtures (`seeds/fixtures/<version>/`) whose bytes live in one
account-level bucket.** The environment fixture says who exists (firms, people)
and which fixture cases they hold; the versioned fixture says what a case
contains, in the API's own body shapes, with a manifest that checksums its
sample documents. Loading is a convergence: ids are *derived* from the target,
the firm, the version and the case's handle, so a second run finds its rows
and a document copy is a server-side `copy_object` into the target's own
case-documents bucket, confirmed the way the API's `complete` route confirms
an upload. Rows that already exist are left alone — a seeded environment is
somewhere people work — and a changed fixture is a new version. Dev and
staging load through the same code path
(`services/admin/…/entrypoints/seed.py`: `scripts/dev-aws-seed.sh` and
`.github/actions/seed-staging`); `publish` moves a version from git to the
bucket and `capture` writes a new version out of a **dev** stack — never any
other source, which is what keeps every fixture synthetic by construction.

**The unit tier runs at push time, not commit time.** `scripts/dev-test-unit.sh`
maps the pushed files to the suites they can break and runs those; the
pre-commit framework's `pre-push` stage calls it. Commits stay fast (format and
lint); a push is the moment the work leaves the machine, and a red PR check
becoming the feedback loop is the thing the hook is there to prevent.

## Context

Before this the repo had unit suites in flat `tests/` directories, one browser
suite that ran only against staging (and, since #188, dev), liveness curls on
every other deploy, and a firm-only seed. That left four gaps, each of which
had already produced a real failure or was one deploy away from one:

- **Nothing exercised the deployed API as a signed-in caller.** The unit tier
  runs over memory adapters; the e2e suite touches the API only through the
  app. Whether the Lambda's own execution role may make every call the routes
  make — DynamoDB, KMS through DynamoDB and through S3, HeadObject on a bucket
  with no ListBucket — was first learned on staging by a person, and
  ADR 0008 names this as the least-covered code by design.
- **Production was smoke-tested by `curl /`.** A bundle built for the wrong
  environment, a stale bundle at the edge, a sign-in hand-off to a client the
  pool no longer registered, an API that had lost its auth configuration and a
  CORS allowlist without the app's origin all serve a 200.
- **A seeded environment had no case in it**, so the only case the browser
  suite could test was one it opened itself, and nothing with a document in it
  was testable without a human uploading one.
- **Unit tests ran only in CI.** A push with a broken suite was found by the
  PR check, ten minutes and a context switch later.

The arrangement is a known one — tiers as directories with directory-scoped
guards, a post-deploy smoke suite that is explicitly a detector, an idempotent
seeder run on every deploy with the account's identity in a fixture and the
one secret in an environment, a shared fixture bucket published from git and
loaded per environment — applied to two facts of this repo: a **staging**
environment, which is where everything signed-in runs, and **real case data in
production**, which is why nothing signed-in runs there.

## Consequences

- **A test that needs AWS, a server or a secret is not a unit test**, and the
  directory it lives in says so. The unit conftests provide memory adapters and
  nothing that reaches a network; the integration conftest provides a signed-in
  HTTP client and nothing in-process. A test that wants both is two tests.
- **Sign-in is SRP, from the standard library.** The web app client allows
  `ALLOW_USER_SRP_AUTH` and, deliberately, no password flow; the integration
  tier speaks SRP rather than loosening that on every pool it runs against.
  No AWS credentials reach the suite, which is what makes the API's calls run
  under its own role.
- **Staging's seed role now writes the case table and the document bucket.**
  Bounded as the firm grant was — staging only, exact names, assumable only by
  the `insolvia-staging` environment — and applied by a human, because
  `ci-trust` cannot apply itself. Until that apply, the seed step fails on
  `AccessDenied` naming the grant, and so does the staging stage: that is the
  runbook's step 1 again, not a regression.
- **The shared fixture bucket is account-level and CI-applied** (`envs/shared`,
  `modules/dev_fixtures`), versioned, and never destroyed by a dev teardown.
  CI reads it and never writes it; publishing is a developer's act.
- **The `smoke` project is exercised on staging before it is the last word on
  production**, so an assertion that drifts from the app breaks a staging run
  rather than a production deploy.
- **A pushed change touching a service pays for that service's unit suite, and
  a docs-only push pays for nothing.** The map from paths to suites lives in
  `scripts/dev-test-unit.sh` and must move with the `paths:` lists in the PR
  workflows. A machine without a suite's toolchain fails the hook rather than
  skipping it — the alternative is a hook that passes on exactly the machine
  that was never set up.
- **Coverage and shape are unchanged.** ADR 0008's per-area shapes, its
  refusal of a coverage gate, and the testing skill's conventions all stand;
  this ADR only says where a test lives and what may run it.

## Alternatives considered

**A prod smoke account with a firm of its own, signed in like staging's.**
Rejected for now. It would need a test identity and tenancy rows in
production, CI permission to write production's firm table, and a way to
guarantee that account can never reach a real firm's data. Each is doable and
each is a security-posture decision for a bankruptcy platform holding real
case files; none belongs inside a testing change. The unauthenticated smoke
project covers what a signed-in one would add to *deploy* detection (the
shell, the build stamp, the sign-in hand-off, the API's refusals); what it
cannot cover is the production role's grants, which staging's integration
tier proves against an identically-built role. Revisit when a synthetic
production monitor is wanted for its own sake.

**Integration tests against the stores directly, under the developer's
credentials.** Rejected as the tier's definition. They cannot run against
staging from CI without giving the pipeline data-plane access to staging's
case table for a purpose other than seeding, and on dev they prove round
trips under credentials far wider than any deployed role's. Going through the
API is the harder and more honest test, and it is the one that runs in both
places.

**One `tests/` directory with markers.** Rejected. A marker is opt-in per
test; a directory's conftest applies to every test in it whether or not the
author remembered. An unfiltered `pytest_collection_modifyitems` hook skips
*everything*, silently, and reports green — which is why the gate here is
filtered to its own directory and why the tier is a directory at all.

**Loading fixtures from git alone, no bucket.** Rejected. It works for a
two-kilobyte PDF and stops working the day a fixture is a realistic scanned
statement; and a server-side copy is what keeps the bytes off the CI runner
and off the laptop. Git keeps the manifest that names and checksums every
object, so the bucket can never hold something the repository does not
describe.
