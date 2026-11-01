# ADR 0029 — One testing shape: xUnit, Jest, Playwright and `terraform test`; route tests and contract tests split the API between them

- **Status:** Proposed
- **Date:** 2026-11-01
- **Relates to:** [#578](https://github.com/insolvia-ai/insolvia/issues/578)
  carries the work this decides, item by item.
  **Amends [ADR 0008](0008-testing-shape-follows-the-code-it-tests.md)** — its
  per-area table named the Python services and a Vitest api-client, and it
  left the API's two outside-in suites with no rule between them; its two
  cross-cutting rules (push tests down; an escaped bug owes a lower test), its
  refusal of a coverage gate and its "static analysis is the base layer" all
  stand. **Amends [ADR 0021](0021-test-tiers-and-seed-fixtures.md)** — its
  *integration* tier stops being a pytest directory and becomes `contract/`'s
  deployed target; its other tiers, its seed fixtures and "nothing signed-in
  runs against production" stand.
  [ADR 0028](0028-backend-moves-to-dotnet-and-postgres.md) is why `contract/`
  exists and why it is the wire's oracle.
  [ADR 0011](0011-cross-tenant-administration-is-a-separate-principal-class.md)
  is why staff sign in through Google and not the pool. The
  `insolvia-testing` skill owns *how* to write a test at each level and must
  not be restated here.

## Decision

**The repo tests with four frameworks, and each level of testing has exactly
one owner.** Vitest and pytest leave the repo.

| Level | Framework | Owns | Local | CI |
|---|---|---|---|---|
| **Unit** | **xUnit** (backend), **Jest** (every TypeScript unit) | logic, components, the api-client's request/response mapping against a stubbed `fetch` | `scripts/dev-test-unit.sh` (`dotnet test` in `backend/`, `npm test` per unit); the pre-push hook | every PR |
| **Data** | xUnit + Testcontainers Postgres 17 | the stores, migrations and grants | `dotnet test` in `backend/` (Docker running) | every PR |
| **API, in-process** | xUnit route tests (`WebApplicationFactory`) | auth, validation, error paths, edge cases | `dotnet test` in `backend/` | every PR |
| **API, outside-in** | **Jest — one suite, `contract/`**, through the shipped api-client | each client method's happy path, the wire shape, tenancy as the client sees it | `contract/scripts/dev-test.sh` (offline target) | offline on every PR; **deployed** target on staging after the API, admin and MCP deploys |
| **UI flows** | Playwright `flows` | the user journeys no lower level can prove | `e2e/scripts/dev-test.sh` against a dev stack | staging, post-deploy |
| **Smoke** | Playwright `smoke` | a broken deploy announcing itself, with no credentials | same wrapper | staging, then production |
| **Infra modules** | `terraform test` | a module's invariants, provider mocked | `terraform -chdir=<module> test` | every PR (`shared-infra-plan.yml`'s staging leg) |

Five rules go with it.

### 1. Route tests and contract tests split the API; an assertion goes to one of them

They used to overlap with no rule: `Insolvia.Api.Tests` (738 tests,
in-process) and `contract/` both assert route behaviour. From here:

- **A route test owns everything about *refusing* a request** — every
  principal against every route (the auth matrix), every validation error,
  every error mapping, every edge case, and anything that needs a host seam
  (a frozen clock, a failing store, a composed configuration).
- **A contract test owns what a caller of the shipped client relies on** — the
  happy path of each `InsolviaApiClient` method, the wire shape it parses
  (names, types, an optional field absent rather than `null`), and tenancy as
  the client sees it (firm A's token cannot read firm B's case). It asserts an
  error only where the client's *own* mapping is under test — a status
  becoming the right exception class — once per class, not once per route.
- **The tie-breaker is what the test needs to touch.** If it needs the shipped
  client, real tokens, or (on the deployed target) the real pool and the
  deployed role, it is a contract test. Otherwise it is a route test, because
  that is cheaper and fails closer to its cause (ADR 0008's "push tests down").
- **Existing overlap is removed as it is touched, not in a sweep.** A PR that
  edits a duplicated assertion deletes the copy at the wrong level and says so
  in its test-count report. A sweep would delete tests faster than anyone can
  check what each one covered.

The api-client's own unit suite (stubbed `fetch`) is not a third level: it pins
how the client *builds* requests and maps responses; `contract/` proves the
same client against the real host.

### 2. One outside-in API suite, two targets

`integration/` (pytest over HTTPS on staging) and `contract/` (TypeScript,
offline) made the same kind of assertion and differed only in where they
pointed. `contract/` is the survivor because it goes through the shipped
client, and the client is what callers actually run.

- **`offline` is the default target**: the .NET hosts on a throwaway Postgres,
  composed by `contract/scripts/dev-test.sh`, tokens signed by the run's own
  JWKS. It runs on every PR (`Contract (.NET)`) and on any laptop, and the
  coverage gate — every client method called at least once — is enforced on
  its full run.
- **`deployed` is the second target**: a running dev stack or staging, signed
  in by real Cognito SRP as the people in `seeds/<env>.json`, with the
  scratch-case discipline `integration/` had (write only to scratch, undo in
  teardown, never edit a fixture case). It needs every input from the
  environment — base URL, pool, client, `E2E_TEST_USER_PASSWORD` — and has no
  defaults. It runs on staging after each deploy and on a developer's dev
  stack on demand; it is never a required PR check.
- **There is no production target.** The suite refuses one, as ADR 0021's
  integration tier did.
- **A spec only the offline stack can express is marked offline-only, by one
  helper, with its reason** — forged and claim-broken tokens, host
  configuration, anything that needs the run's own JWKS. The deployed run
  reports those as skipped by name; it never skips silently. Every other spec
  runs on both targets, and one that passes on only one of them is a bug in
  the spec (ADR 0021's rule, unchanged).

### 3. One JavaScript test runner: Jest, at the major `jest-expo` requires

Jest is not chosen on merit over Vitest; it is forced. `jest-expo` is the only
supported way to test a React Native app on Expo, so the app cannot leave Jest,
and two runners means two mock APIs, two config dialects and two sets of
gotchas for one language.

- **Every TypeScript unit moves to Jest**: `packages/insolvia_api_client`,
  `contract/`, `apps/insolvia_admin` and `apps/insolvia_marketing`.
- **One Jest major repo-wide — whichever `jest-expo` requires** (29 under
  SDK 57). A `jest-expo` upgrade that moves the major moves every unit with
  it, in the same PR.
- **ESM is transpiled, through Babel or SWC, as `jest-expo` does.** Never
  `--experimental-vm-modules`: it is still experimental in Jest 29, needs a
  Node flag on every invocation, and changes module semantics under the
  mocks.
- **Marketing keeps its own lockfile** and stays outside the root workspace
  (the root `package.json`'s `//` comments own why); it pins the same major in
  its own manifest.

Playwright is not a second unit runner: it drives a browser against a deployed
bundle, and nothing else here can.

### 4. Python is a tooling language only

Python stays for **tools** — a script a person or a scheduled workflow runs to
produce or check committed data — and for nothing else:

- **Never product code, never a test, never a PR check.** No pytest, no
  Python job on a required check.
- **Standard library only, or its own pinned virtualenv created by its own
  wrapper script.** Nothing installs Python packages into a shared
  environment.
- **What a tool produces is tested by its consumer, in that consumer's
  framework.** The engines' xUnit tests load the UST registry the ingest
  writes; `npm run tokens:check` gates the marks the cutter writes.

Under this exception today:

| Tool | Why it stays Python |
|---|---|
| `scripts/check-ust-data.py`, `scripts/ingest-ust-data.py` | Standard library only; run by `regulatory-refresh.yml` (weekly, not a PR check) and by a developer refreshing a dataset. A ~900-line port buys no coverage the engines' tests don't already have. |
| `scripts/render-brand-marks.py` | Cuts the logo and icon out of the display face with fontTools, HarfBuzz and Pillow, in its own venv. Neither .NET nor Node has an equal without a WebAssembly shaper; its outputs are committed and gated by `tokens:check`. |

**`forms/scripts/check.py` leaves.** It *is* a PR check (`Form specs`), and it
validates specs that `Insolvia.Forms.Tests` and `Insolvia.Engines.Tests`
already load — so it becomes an xUnit test there.

A new Python file is admissible only if it fits this definition, and the PR
adding it says which row it joins.

### 5. Bash tests and `terraform test` stay where they are

A shell script is tested in shell: `.github/scripts/*.test.sh` test CI glue
that only bash runs, with no framework (no bats). An infra module is tested by
`terraform test` with the provider mocked. Neither is a fifth framework in the
sense this ADR reduces: each is the native test mode of the only language the
code under test is written in.

### Staff sign-in is not automated on any deployed environment

Staff sign in to the admin API and the admin portal through Google Workspace
OIDC (ADR 0011). The verifier requires Google's `hd` claim to name the
Workspace domain, and a verified email
(`backend/src/Insolvia.Core/Auth/TokenVerification.cs`). A Google service
account's ID token carries no `hd`, so the only token that passes is a real
Workspace user's — and automating that means a Workspace password and second
factor held as a CI secret, driving Google's sign-in page, which Google's
bot-detection treats as an attack. Giving staging a second, test-only staff
issuer would be a cross-tenant back door on a deployed environment. Both are
rejected.

So:

- **Signed-in admin coverage runs offline.** `contract/`'s `admin` specs
  already sign Google-shaped tokens with the run's JWKS, through the same
  verifier the deployed admin uses. The admin portal's Playwright flow
  (provision a firm, invite a user) runs against the portal served locally
  over the offline stack, with the run's own auxiliary server standing in as
  the OIDC provider — a stand-in that exists only in the local composition.
- **On staging, admin is checked signed out**: health, CORS, and every route
  refusing both an anonymous call and a seeded *Cognito* user's token (the
  wrong principal class — a real check of ADR 0011 against the deployed role).
- **MCP is signed in on staging** if its accepted client ids admit a token a
  seeded person obtains by SRP; #578's item 4 verifies that, and falls back to
  the signed-out checks above if they do not.

**Whether production gets a signed-in read-only smoke check** is #578's
item 5 to decide, against ADR 0021's reasons for refusing one; it records the
outcome as an amendment here either way.

## Context

The suites grew one at a time — the Python backend's tests, then the .NET
port, then `contract/` as the cut-over's oracle (ADR 0028) — and nobody
stepped back afterwards. On 2026-11-01:

| Suite | Framework | Tests |
|---|---|---|
| `backend/tests/*` (12 projects) | xUnit | ~2,800 (`Api` 738, `Core` 827, `Engines` 419, `Data` 255, …) |
| `apps/insolvia_app` | Jest (`jest-expo`) | ~655 |
| `packages/insolvia_api_client` | Vitest | ~419 |
| `apps/insolvia_marketing` | Vitest | ~51 |
| `contract/` | Vitest | 48 declared, 63 with expansions |
| `apps/insolvia_admin` | Vitest | 22 |
| `integration/` | pytest | 62 |
| `e2e/` | Playwright | 12 (4 flows, 3 smoke files) |
| `infra/modules/*/tests` | `terraform test` | 3 modules |
| `forms/scripts/check.py` | Python script | one PR check |

(Counts are declarations found by a static scan; each move in #578 reports the
runner's own count before and after.)

That is five test frameworks plus a Python checker; two outside-in API suites
in two languages making the same assertions against different targets; route
behaviour asserted at two levels with no rule for which owns what; four
Playwright flows for 56 screens; no browser coverage of the admin portal; and
`integration/` with no linter since the Python services' `ruff` steps went
with them.

## Consequences

- **`vitest` and `pytest` appear in no manifest, lockfile, config or
  workflow** once #578 lands, and the `insolvia-testing` skill describes the
  shape above.
- **Required check names do not have to change.** `API client`,
  `Contract (.NET)`, `Marketing site`, `Admin portal` and `App` keep their job
  names across the runner change. `Form specs` keeps its name if its job
  becomes the xUnit run; folding it into the backend's check instead is a
  ruleset change, made by the maintainer through `scripts/update-ruleset.sh`.
  `api-staging.yml`'s `integration` job is post-deploy, not a required check.
- **`scripts/dev-test-unit.sh`'s path map moves with the suites**, so the
  pre-push hook keeps running what a push can break.
- **Every move reports the runner's test count before and after and explains
  each difference.** A test dropped as a duplicate is listed by name with the
  test that already covers it.
- **The contract suite runs longer on staging than `integration/` did** — the
  whole suite against a deployed stack instead of 62 targeted tests. That is
  the price of one suite, paid post-deploy, off every PR's critical path.
- **Adding a Playwright flow still needs a reason no lower level can satisfy**
  (ADR 0008); the journeys #578 adds — one full case from intake to the
  filing-set preview, document upload and request, the means test, the admin
  portal — are the ones whose seams only a browser crosses.

## Alternatives considered

**Vitest everywhere.** Rejected: the app cannot leave `jest-expo`, so this
would be "two runners" under another name.

**Keep `integration/` beside `contract/`.** Rejected: two suites asserting the
same wire in two languages drift apart, and the pytest one does not go through
the client callers actually run.

**Route tests only; delete `contract/`.** Rejected: nothing would prove the
shipped client against the real host, which is the seam ADR 0008 says is held
by contract pins and which ADR 0028 made the oracle.

**Contract only; thin the route tests.** Rejected: an outside-in suite is
slower, cannot reach host seams (a failing store, a frozen clock), and fails
far from its cause. Refusals are cheapest and most precise in-process.

**No Python at all.** Rejected for now: it means porting the marks cutter onto
a WebAssembly shaper and ~900 lines of UST parsing, for tools that run weekly
or by hand and whose outputs are already tested by their consumers. Revisit if
UST parsing ever moves to runtime.

**Jest with native ESM (`--experimental-vm-modules`).** Rejected: experimental,
flag-gated on every invocation, and not what `jest-expo` does.

**An automated Google sign-in for a staff test identity.** Rejected above: a
real Workspace credential in a public repo's pipeline, against a sign-in page
built to stop exactly this.
