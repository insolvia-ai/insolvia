---
name: insolvia-testing
description: >-
  How Insolvia tests — which level owns an assertion, where a new test file
  goes, and the per-stack conventions that are easy to get subtly wrong. Use
  this BEFORE writing or changing any test in this repo: an xUnit test under
  backend/tests (unit, data or route), a contract spec under contract/ (offline
  or deployed), a Jest test in apps/insolvia_app, packages/insolvia_api_client,
  packages/insolvia_admin_client, apps/insolvia_admin or apps/insolvia_marketing,
  a Playwright spec under e2e/, a `terraform test` under infra/modules, or a
  `.github/scripts/*.test.sh`. Reach for it the moment a task says "add a test",
  "write tests for", "improve coverage", "this needs test coverage", or when you
  have just added an endpoint, an admin route, an MCP tool, a component, a
  route, or a config value and owe it a test. Also read it before proposing a
  coverage threshold, a new test runner, a mocking library, a Python script, or
  an end-to-end test — this repo has decided against several of those and the
  reasons are not obvious. Consult it when a test is failing in a way that
  suggests it was testing the wrong thing, or when you are unsure whether an
  API assertion belongs in a route test or in contract/.
---

# Testing in Insolvia

**Four frameworks, and each level of testing has exactly one owner**: xUnit,
Jest, Playwright and `terraform test` (plus plain-bash tests for CI glue).
[ADR 0029](../../docs/adr/0029-one-testing-shape.md) owns that decision and
its reasons; it amends [ADR 0008](../../docs/adr/0008-testing-shape-follows-the-code-it-tests.md)
(push tests down; an escaped bug owes a lower test; no coverage gate) and
[ADR 0021](../../docs/adr/0021-test-tiers-and-seed-fixtures.md) (seed
fixtures; nothing signed-in runs against production). This file owns *how* to
write a test at each level.

| Level | Where | Framework | Owns | Local | CI |
|---|---|---|---|---|---|
| **Unit** | `backend/tests/Insolvia.<Project>.Tests/`; TypeScript colocated in `apps/insolvia_app`, `packages/insolvia_api_client`, `packages/insolvia_admin_client`, `apps/insolvia_admin`, `apps/insolvia_marketing` | xUnit v3; Jest 29 (`jest-expo` in the app, `babel-jest` elsewhere) | logic, components, each client's request/response mapping against a stubbed `fetch` | `scripts/dev-test-unit.sh` (every suite, or the ones a set of files can break — what the **pre-push hook** runs); per unit `dotnet test` / `npm test` | every PR (`Backend (.NET)`, `App`, `API client`, `Admin client`, `Admin portal`, `Marketing site`) |
| **Data** | `backend/tests/Insolvia.Data.Tests/` | xUnit + Testcontainers Postgres 17 | the stores, migrations and grants | `cd backend && dotnet test Insolvia.slnx` (Docker running) | every PR (`Backend (.NET)`) |
| **API, in-process** | `backend/tests/Insolvia.<Host>.Tests/Routes/` | xUnit, `WebApplicationFactory` | **refusals**: auth, validation, error paths, edge cases, anything needing a host seam | same | every PR (`Backend (.NET)`) |
| **API, outside-in** | `contract/src/{api,admin,mcp}/` | Jest, through each surface's real client | **the happy path** of each client method, the wire shape, tenancy as the client sees it | `contract/scripts/dev-test.sh` (offline); `--target deployed` against a dev stack | offline on every PR (`Contract (.NET)`); deployed on staging after the API, admin and MCP deploys |
| **UI flows** | `e2e/tests/flows/`; the admin portal's in `e2e/tests/admin/` | Playwright | the journeys no lower level can prove | `e2e/scripts/dev-test.sh` (dev stack); `e2e/scripts/admin-offline-test.sh` (admin, over the offline stack) | `flows` on staging after deploy (`app-staging.yml`); the admin flow on every PR (`admin-flow-pr.yml`, not required) |
| **Smoke** | `e2e/tests/smoke/` | Playwright | a broken deploy announcing itself, with **no credentials** | same wrapper | staging, then production (`app-prod.yml`) — the only thing that runs on prod |
| **Infra modules** | `infra/modules/<module>/tests/` | `terraform test`, provider mocked | a module's invariants | `terraform -chdir=infra/modules/<module> test` | every PR (`shared-infra-plan.yml`'s staging leg) |
| CI glue | `.github/scripts/*.test.sh` | plain bash, no framework | the shell that only CI runs | `scripts/dev-test-unit.sh` (`ci-scripts`) | none — the pre-push hook runs them |
| `tool/` | — | gated by `npm run tokens:check` | the generated brand outputs | `npm run tokens:check` | `Cognito branding` |

**Pick the level by what the test needs to touch.** If it needs AWS, a running
server or a secret, it is not a unit test, whatever it asserts. If it needs
the shipped client, real tokens, or the real pool and the deployed role, it
is a contract spec. If it needs a real browser, it is Playwright. If it must
run against production, it is `smoke`, and it gets no credentials at all.
(The backend's data level is the one exception to "no server": a
Testcontainers Postgres is part of the unit run, because an in-memory EF
provider would test a database we do not ship.)

## Route tests vs contract tests — an assertion goes to one of them

ADR 0029 rule 1. The API is tested twice from different sides, so each
assertion has exactly one home:

- **A route test owns everything about *refusing* a request** — every
  principal against every route (the auth matrix), every validation error,
  every error mapping, every edge case, and anything that needs a host seam
  (a frozen clock, a failing store, a composed configuration).
- **A contract spec owns what a caller of the shipped client relies on** — the
  happy path of each client method, the wire shape it parses (names, types,
  an optional field *absent* rather than `null`), and tenancy as the client
  sees it (firm A's token cannot read firm B's case). It asserts an error only
  where the client's *own* mapping is under test — a status becoming the right
  exception class — once per class, not once per route.
- **The tie-breaker is what the test needs to touch.** Shipped client, real
  tokens, the real pool or the deployed role → contract. Anything else → route
  test, because it is cheaper and fails closer to its cause.
- **Existing overlap is removed as it is touched, not in a sweep.** A PR that
  edits a duplicated assertion deletes the copy at the wrong level and names
  it in its test-count report.

The api-client's own unit suite is not a third level: it pins how the client
*builds* requests and maps responses against a stubbed `fetch`; `contract/`
proves the same client against the real host.

## Before you write it

1. **Push it down.** Verify the behaviour at the cheapest level that can
   observe it. A rule about *references* belongs in
   the layering test (`Insolvia.Core.Tests/Architecture`), not in a route test.
2. **Ask what would catch this in production.** If `tsc --strict`, the C#
   compiler with warnings as errors and nullable on, or ESLint already makes
   the failure impossible, the test is noise. Static analysis is the base
   layer here, not a separate concern.
3. **Colocation is not uniform.** TypeScript tests sit **beside** the file they
   test (`heading.tsx` ↔ `heading.test.tsx`) — never a `__tests__/` directory.
   C# tests live in the project's own test project,
   `backend/tests/Insolvia.<Project>.Tests/`, one per `src` project (the
   architecture test fails until a new project has one); shared builders sit
   in that project's `Support/`, committed oracle outputs in `Fixtures/`,
   `Goldens/` or `Parity/`.
4. **Name the behaviour, not the function.** `A_malformed_email_is_rejected`,
   not `TestParse`. The existing suites are consistent about this; match them.
5. **Report the runner's count.** A PR that moves, deletes or merges tests
   says the runner's own count before and after and explains every
   difference; a test dropped as a duplicate is named with the test that
   already covers it. No test is lost silently.

## .NET (`backend/`) — xUnit v3

`backend/CLAUDE.md` owns the layering and the data level's rules; these are
the testing half.

- **Arrange–Act–Assert, one act per test.** The
  arrange–assert–act–assert–act pattern makes a failure ambiguous about which
  step broke.
- **`[Theory]` with `[InlineData]` / `TheoryData` over copy-paste.** Table-drive
  the cases; do not write five near-identical `[Fact]`s.
- **Configuration is a function, not the process environment.** Each host's
  config loads from a `Func<string, string?>` (`ApiConfig.Load(read)` and
  siblings); a test passes a lambda. Do not set real environment variables in
  a test — tests run in parallel.
- **Fake at the port.** `Insolvia.Core` defines the interfaces; its memory
  twins implement them, and every Postgres store must pass the same
  conformance suite as its twin (`Insolvia.Data.Tests/Conformance`). A new
  external dependency gets a port and a twin, not an inline mock. There is
  deliberately **no mocking library** in `Directory.Packages.props`.
- **Route tests go through the real pipeline** — `WebApplicationFactory`
  (`Insolvia.Api.Tests/Support`), real auth with test-signed tokens, the
  error envelope as the client sees it. They own the refusals (above).
- **The data level uses a real Postgres** (`PostgresFixture`, a collection
  fixture; a fresh database per test). Never the EF in-memory provider. A new
  write with a `landed` check gets a lost-acknowledgement test in `RetryTests`.
- **Committed oracle outputs are the spec.** `Parity/*.expected.json`,
  `Goldens/`, `python-*.json` and `debtor-txt/` were captured from the
  since-deleted Python backend; their generators are gone. A deliberate
  behaviour change edits them in the same PR, reviewed value by value — never
  regenerated to make a red test green.
- **The form specs are checked here too.** `forms/scripts/dev-test.sh` runs
  `Insolvia.Core.Tests/Forms/Specs` filtered (the `Form specs` check); a forms
  edit is gated by it.
- **The layering test (`Insolvia.Core.Tests/Architecture`) is not editable to make your change pass.**
  If it fails, your layering is wrong. Fix the layering.

## Jest — every TypeScript unit, and `contract/`

One runner for all TypeScript, **Jest 29, the major `jest-expo` requires**
(ADR 0029 rule 3). A `jest-expo` upgrade that moves the major moves every
unit with it, in the same PR. The workspace members
(`packages/insolvia_api_client`, `packages/insolvia_admin_client`,
`contract/`) spread the root `jest.base.js`; the admin portal and marketing
state their own `jest.config.js` and pin the same major in their own
manifests, because they are outside the workspace.

- **ESM is transpiled to CommonJS by `babel-jest`**, as `jest-expo` does.
  Never add `--experimental-vm-modules`. Code a test loads must not read
  `import.meta`: move the read into its own module and map it (admin's
  `build-environment.ts`), or import the JSON (`contract/src/support/env.ts`).
- **Import the API from `@jest/globals`** (`describe`, `it`, `expect`, `jest`)
  rather than relying on injected globals, so `tsc` and ESLint see real
  bindings.
- **`expect` takes one argument.** To say which exchange or field failed, wrap
  the assertion: `withMessage(label, () => expect(x).toBe(y))`
  (`contract/src/support/wire.ts`).
- **`process.env` is saved and restored by hand** — marketing's
  `test/env.ts` (`stubEnv` / `restoreEnv`); there is no `stubEnv` in Jest.
  Replace a global function with `jest.spyOn(globalThis, 'fetch')`, which
  `restoreMocks` (or `jest.restoreAllMocks()`) puts back.
- **`it.each` takes mutable tuples.** Type the table (`it.each<[A, B]>([...])`)
  instead of `as const`.

### React Native (`apps/insolvia_app`) — `jest-expo` + RNTL v13

The app is **React Native**, not react-dom. Guidance written for
`@testing-library/react` mostly transfers, but the package is
`@testing-library/react-native` and it is **pinned to v13** — `userEvent`
semantics changed in v14, so check `package.json` before copying any snippet
found online.

- **Query priority, strictly:** `getByRole` → `getByLabelText` →
  `getByPlaceholderText` → `getByText` → `getByDisplayValue` → `getByTestId`.
  `getByTestId` is a **last resort** — it couples the test to something no user
  can perceive. Prefer `getByRole('button', { name: 'Sign out' })`, which
  asserts the accessible name and the role in one go.
- **`userEvent`, not `fireEvent`.** `userEvent.press()` emits the real
  `pressIn`/`press`/`pressOut` sequence; `fireEvent` fires one synthetic event
  and will pass on components a user could not actually operate. Adding a
  `fireEvent` needs a reason.
- **`queryBy*` is only ever for proving absence.** For anything present, `getBy*`
  and `findBy*` throw a useful error; `queryBy*` returns `null` and produces
  `expected null not to be null`, which tells you nothing.
- **`findBy*` for async — never `waitFor` + `getBy*`.** One assertion per
  `waitFor`, and never a side effect inside one.
- **Use `screen`**, don't destructure `render()`'s result.
- **Never call `cleanup()`** and **never wrap `render`/`userEvent`/`fireEvent`
  in `act()`** — RNTL already does. The legitimate exception is calling a hook's
  returned function imperatively (see `session-provider.test.tsx`), and even then
  `act` is imported from `@testing-library/react-native`, never from `react`.
- **Route-level tests use `renderRouter('src/app', { initialUrl: … })`** from
  `expo-router/testing-library`, which mounts the real route tree.
- **Never override `transformIgnorePatterns`.** A hand-copied list silently drops
  `standard-navigation` (pulled in by expo-router) and breaks every route test.

### The client pins (`packages/insolvia_api_client`, `packages/insolvia_admin_client`)

Each client's own suite **is** that client's half of the contract with its
host; `contract/` is the host's half, driving the same client against the
running .NET hosts. There is no OpenAPI codegen, by decision.

- **Every endpoint added gets a hand-written client method, a pin in the
  client's suite, *and* a call in `contract/`** (its coverage gate fails on a
  client method nothing calls), in the same PR.
- **Assert the exact request and the exact response mapping** — method, URL,
  headers, body — against a stubbed `fetch`. Copy the literal JSON the server
  actually returns; read the route handler to get it, don't infer it.
- **Import by package name**, not relative path, so the test exercises the real
  export map.
- A server field rename is *supposed* to break these tests. That is the feature.

## The outside-in suite (`contract/`) — one suite, two targets

ADR 0029 rule 2. [`contract/CLAUDE.md`](../../contract/CLAUDE.md) owns the
detail (the gates in `support/gates.ts`, the actors and scratch teardown in
`support/actors.ts`); these are the rules a new spec must keep.

- **Every surface goes through the client its callers run, imported by
  package name.** The API through `@insolvia-ai/api-client`; admin through
  `@insolvia-ai/admin-client` (`packages/insolvia_admin_client`, the portal's
  own client); MCP through the official `@modelcontextprotocol/sdk` `Client`
  over Streamable HTTP. No spec hand-builds a request to a route
  (`support/browser.ts` is the one exception, for what only a browser does:
  the CORS preflight, a request with no credential, `/health`).
- **Coverage gates, enforced on the offline full run:** every
  `InsolviaApiClient` method called, every admin-client method called, every
  tool the MCP server lists in `tools/list` called.
- **`offline` is the default target** — the .NET hosts on a throwaway
  Postgres, composed by `contract/scripts/dev-test.sh`, tokens signed by the
  run's own JWKS. It runs on every PR (`Contract (.NET)`, required) and on any
  laptop.
- **`deployed` is the second target** — a running dev stack or staging,
  signed in by real Cognito SRP as the people in `seeds/<env>.json`.
  It needs every input from the environment (base URL, pool, client,
  `E2E_TEST_USER_PASSWORD`) and has no defaults. It runs on staging after each
  deploy (`api-staging.yml`, `admin-staging.yml`, `mcp-staging.yml`) and on a
  dev stack on demand (`--target deployed`); it is **never a required PR
  check**, and its failure fails the staging stage.
- **There is no production target.** The suite refuses one.
- **Staff are signed in offline only.** Staff sign in through Google
  Workspace OIDC (ADR 0011), which cannot be automated on a deployed
  environment; on staging the admin specs run signed out (health, CORS, every
  route refusing an anonymous call and a seeded Cognito user's token).
- **A spec only the offline stack can express says so with
  `offlineOnly(reason)`** — forged and claim-broken tokens, host
  configuration, anything that needs the run's own JWKS. The deployed run
  reports it as skipped by name. Every other spec runs on both targets, and
  **one that passes on only one of them is a bug in the spec.** Branch on the
  fixture's contents (`needsSeeded`), never on the target name.
- **It imports no backend code and reaches no store.** A test that needs to
  is a backend test wearing the wrong directory.
- **Who exists comes from `seeds/<target>.json`, by handle.** A spec that needs
  a person the fixture lacks adds them to the fixture; it never invents an
  address or a subject.
- **Scratch discipline.** Cases are only soft-deleted through the API (the
  rows stay), so work inside scratch cases `support/actors.ts` tracks and
  deletes in teardown; restore any firm setting you change; never write to a
  fixture case.

## End-to-end (`e2e/`) — Playwright

[`e2e/CLAUDE.md`](../../e2e/CLAUDE.md) owns the detail.

- **Two projects against deployed environments, by what they may hold.**
  `flows` signs in and is offered against dev and staging; `smoke` holds no
  credentials and is the only thing that runs against production. A smoke
  spec that needs a token is a `flows` spec; a `flows` spec that needs
  nothing signed-in is a `smoke` spec. **Production gets no signed-in check**,
  read-only or not (ADR 0029 § Amendments).
- **The admin portal's flow is offline** (`e2e/tests/admin/`,
  `playwright.admin.config.ts`): the portal served locally over `contract/`'s
  offline stack, Google's sign-in stood in for in the browser. It runs on any
  laptop and on every PR (`Admin portal flow (offline)`, deliberately not
  required).
- **Adding a flow needs a reason no lower level can satisfy.** Slow,
  environment-dependent, and a flake blocks production promotion through
  `verified-commit`. A `smoke` test needs a reason a `curl` cannot satisfy.
- **Never make a deployed project a required PR check** — that puts staging's
  availability on every PR's critical path. Post-deploy only.
- **Write only to the scratch case** (`support/scratch-case.ts`), never to a
  seeded fixture case — the loader will not repair it and the next deployed
  contract run cannot find it. Reading a fixture case is fine.
- **Role-based selectors only**, matching the app's accessibility contract.
- **Never let a credential reach a file, a default, a log line, or an uploaded
  artifact.** Traces record typed values verbatim and the repo is public.
- If a spec starts passing only on retry, **quarantine it** — do not raise the
  retry count.

## Infra and shell

- **An infra module's invariant is a `terraform test`** in
  `infra/modules/<module>/tests/`, with the provider mocked
  (`filing_credentials`, `filing_queue` and `filing_worker` are the examples).
  Prefer it over a prose rule in a `CLAUDE.md`.
- **A shell script CI runs is tested in shell**: `.github/scripts/<name>.test.sh`
  beside it, plain bash, no bats. `scripts/dev-test-unit.sh` runs them.

## Seed data

- **`seeds/<env>.json` says who exists and which fixture cases they hold;
  `seeds/fixtures/<version>/` says what a case contains**, in the API's own
  body shapes. The loader (the migrator's `seed`) parses both with the route's
  own parsers, so a malformed row fails with the API's field errors — write a
  fixture the way you would write a request.
- **A loaded fixture is never edited; a changed fixture is a new version.**
  Rows that exist are left alone on every load, so editing `v1` after it has
  loaded changes nothing anywhere. Copy the latest version to the next and
  curate it by hand (`seeds/README.md`).
- **Nothing real, ever.** Every value describes nobody; every address ends in
  `.test`.

## Things this repo has decided against

Each of these looks like an improvement and is not. Do not add one without an
ADR:

- **A coverage threshold as a merge gate.** It rewards exactly the tests
  ADR 0008 says not to write. Measuring coverage for information is fine.
  (`contract/`'s gates are not thresholds: they require every client method
  and MCP tool to be *called*.)
- **A second JavaScript test runner** — Vitest included. The app cannot leave
  `jest-expo`, so a second runner means two mock APIs and two config dialects
  for one language.
- **Python, for anything.** No Python file, test or tool of ours (ADR 0029
  rule 4; a CI guard fails on a tracked `*.py`). A tool is written in the
  language of what consumes its output. `pre-commit` stays: it is a
  third-party program, not our code.
- **A mocking or network-mocking library** (Moq, NSubstitute, WireMock, and
  their kin). Ports plus memory twins, and a hand-written `HttpMessageHandler`
  where a transport must be faked, already cover this.
- **A second outside-in API suite.** `contract/` is the one; a new target is a
  new value of `CONTRACT_TARGET`, not a new directory.
- **Snapshot tests.** Nothing in the repo uses them; they pin rendered output
  rather than behaviour, and they get regenerated rather than read.
- **Testing an area's private helpers directly.** Test the exported surface.

## Where a new test file goes

| You changed | Test goes |
|---|---|
| a domain rule or an engine | `backend/tests/Insolvia.<Core\|Engines>.Tests/`, pure, table-driven |
| a store | the conformance suite both the twin and the Postgres store run (`Insolvia.Data.Tests/Conformance`) |
| a route's refusals, validation or error mapping | `backend/tests/Insolvia.<Host>.Tests/Routes/`, through `WebApplicationFactory` |
| a new API endpoint | its route tests (refusals), the api-client pin, **and** a happy-path call in `contract/src/api` |
| an admin route, or an `AdminClient` method | its route tests, `packages/insolvia_admin_client`'s pin, **and** a call in `contract/src/admin` (its gate counts every method) |
| an MCP tool | `Insolvia.Mcp.Tests`, **and** a call in `contract/src/mcp` through the SDK (its gate requires every listed tool) |
| tenancy — who may see whose data, as the client sees it | `contract/src/api/tenancy.test.ts` |
| a form spec | `forms/scripts/dev-test.sh` (the xUnit spec tests) gates it |
| a new AWS-touching adapter | a port in `Insolvia.Core`, a twin beside it, the adapter in `Insolvia.Aws` with its transport faked |
| an app component | beside it, `<name>.test.tsx`, rendered, queried by role |
| an app route | beside the screen, via `renderRouter` |
| an admin-portal or marketing module | beside it, Jest |
| a design-system component | not here — [`insolvia-ai/design-system`](https://github.com/insolvia-ai/design-system), where the `.web`/`.native` leaves have separate tests |
| an infra invariant | a `terraform test` in the module, or another executable check (the layering test, a workflow guard) over prose |
| a script under `.github/scripts/` | `<name>.test.sh` beside it |
| something only a real bucket, pool, database role or the deployed Lambda role can answer | a `contract/src/` spec through the client, inside a scratch case — it runs deployed too |
| something only a real browser and a real sign-in can answer | `e2e/tests/flows/<subject>.spec.ts` (`e2e/tests/admin/` for the admin portal) |
| something a production deploy must announce when broken | `e2e/tests/smoke/<subject>.spec.ts` — no credentials, and it runs on staging first |
| a person or a case a suite needs to exist | `seeds/<env>.json` (who) and `seeds/fixtures/<version>/` (what) — never a script, never a secret |
