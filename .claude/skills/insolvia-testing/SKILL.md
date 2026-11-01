---
name: insolvia-testing
description: >-
  How Insolvia tests — which shape applies where, where a new test file goes,
  and the per-stack conventions that are easy to get subtly wrong. Use this
  BEFORE writing or changing any test in this repo: an xUnit test under
  backend/tests, a contract test under contract/ (offline or deployed), a Jest test in apps/insolvia_app,
  packages/insolvia_api_client, packages/insolvia_admin_client, apps/insolvia_admin or apps/insolvia_marketing, or a Playwright spec under e2e/. Reach for it the moment a task says "add a test",
  "write tests for", "improve coverage", "this needs test coverage", or when you
  have just added an endpoint, a component, a route, or a config value and owe
  it a test. Also read it before proposing a coverage threshold, a new test
  runner, a mocking library, or an end-to-end test — this repo has decided
  against several of those and the reasons are not obvious. Consult it when a
  test is failing in a way that suggests it was testing the wrong thing.
---

# Testing in Insolvia

Several runners, one per stack. **The shape differs by area on purpose** —
[ADR 0008](../../docs/adr/0008-testing-shape-follows-the-code-it-tests.md) owns
that decision and the reasoning. **Every test belongs to one of four tiers,
and the tier is a directory** —
[ADR 0021](../../docs/adr/0021-test-tiers-and-seed-fixtures.md) owns which tier
runs against which environment. This file owns *how* to write one.

| Tier | Where | Runner | Command | Runs against |
|---|---|---|---|---|
| **unit** (incl. route and data) | `backend/tests/Insolvia.<Project>.Tests/` | xUnit v3 | `cd backend && dotnet test Insolvia.slnx` (CI: `Backend (.NET)`, after `dotnet format --verify-no-changes`) | nothing remote — memory twins; route tests in-process through `WebApplicationFactory`; the data tier a throwaway Postgres via Testcontainers (needs Docker) |
| **unit** | `apps/insolvia_app` (colocated) | Jest (`jest-expo`) | `npm test --workspace apps/insolvia_app` | nothing — mocks |
| **unit** | `packages/insolvia_api_client`, `packages/insolvia_admin_client`, `apps/insolvia_admin`, `apps/insolvia_marketing` (colocated) | Jest (Babel-transpiled; see below) | `npm run test --workspace @insolvia-ai/api-client` (or `admin-client`); `npm test` in admin and marketing (own lockfiles) | nothing — stubbed `fetch` |
| **unit**, all of the above at once | — | `scripts/dev-test-unit.sh` | every suite, or the ones a set of files touches; what the **pre-push hook** runs | nothing |
| **contract** | `contract/src/` | Jest | `contract/scripts/dev-test.sh` (CI: `Contract (.NET)`) | the .NET hosts on a throwaway Postgres, offline, through each surface's real client (the api-client, the admin client, the MCP SDK) — the wire contract's oracle |
| **integration** (contract, deployed) | `contract/src/` | Jest, `CONTRACT_TARGET=deployed` | `contract/scripts/dev-test.sh --target deployed` (dev); `api-staging.yml`, `admin-staging.yml`, `mcp-staging.yml` (staging) | the RUNNING API and MCP server over HTTP, signed in by SRP as a seeded person; admin signed out |
| **e2e `flows`** | `e2e/tests/flows/` | Playwright | `e2e/scripts/dev-test.sh` (dev); `app-staging.yml` (staging) | a real browser, signed in |
| **e2e `smoke`** | `e2e/tests/smoke/` | Playwright | same wrappers; `app-prod.yml` (production) | HTTP + a browser, **no credentials** — the only thing that runs on prod |
| `tool/` | none — gated by `npm run tokens:check` | — | `npm run tokens:check` | — |

**Pick the tier by what the test needs to touch.** If it needs AWS, a running
server or a secret, it is not a unit test, whatever it asserts — and the unit
tier's fixtures give it nothing that reaches a network. If it needs to sign in, it is
integration (HTTP) or `flows` (browser). If it must run against production, it
is `smoke`, and it gets no credentials at all. (The backend's data tier is
the one exception to "no server": a Testcontainers Postgres is part of the
unit run, because an in-memory EF provider would test a database we do not
ship.)

## Before you write it

1. **Push it down.** Verify the behaviour at the cheapest level that can
   observe it. A rule about *references* belongs in
   `Insolvia.Architecture.Tests`, not in a route test.
2. **Ask what would catch this in production.** If `tsc --strict`, the C#
   compiler with warnings as errors and nullable on, or ESLint already makes
   the failure impossible, the test is noise. Static analysis is the base layer here, not a separate concern.
3. **Colocation is not uniform.** TypeScript tests sit **beside** the file they
   test (`heading.tsx` ↔ `heading.test.tsx`) — never a `__tests__/` directory.
   C# tests live in the project's own test project,
   `backend/tests/Insolvia.<Project>.Tests/`, one per `src` project (the
   architecture test fails until a new project has one); shared builders sit
   in that project's `Support/`, committed oracle outputs in `Fixtures/`,
   `Goldens/` or `Parity/`.
4. **Name the behaviour, not the function.** `A_malformed_email_is_rejected`,
   not `TestParse`. The existing suites are consistent about this; match them.

## React Native (`apps/insolvia_app`) — Jest + RNTL v13

The app is **React Native**, not react-dom. Guidance written for
`@testing-library/react` mostly transfers, but the package is
`@testing-library/react-native` and it is **pinned to v13** — `userEvent`
semantics changed in v14, so check `package.json` before copying any snippet
found online.

- **Query priority, strictly:** `getByRole` → `getByLabelText` →
  `getByPlaceholderText` → `getByText` → `getByDisplayValue` → `getByTestId`.
  `getByTestId` is a **last resort** — it couples the test to something no user
  can perceive. The suite currently uses it once across 124 tests; keep it that
  way. Prefer `getByRole('button', { name: 'Sign out' })`, which asserts the
  accessible name and the role in one go.
- **`userEvent`, not `fireEvent`.** `userEvent.press()` emits the real
  `pressIn`/`press`/`pressOut` sequence; `fireEvent` fires one synthetic event
  and will pass on components a user could not actually operate. There are
  currently **zero** `fireEvent` calls in the repo — adding one needs a reason.
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

## .NET (`backend/`) — xUnit v3

`backend/CLAUDE.md` owns the layering and the data tier's rules; these are the
testing half.

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
  error envelope as the client sees it.
- **The data tier uses a real Postgres** (`PostgresFixture`, a collection
  fixture; a fresh database per test). Never the EF in-memory provider. A new
  write with a `landed` check gets a lost-acknowledgement test in `RetryTests`.
- **Committed oracle outputs are the spec.** `Parity/*.expected.json`,
  `Goldens/`, `python-*.json` and `debtor-txt/` were captured from the Python
  backend before 18.23 deleted it; their generators are gone. A deliberate
  behaviour change edits them in the same PR, reviewed value by value — never
  regenerated to make a red test green.
- **`Insolvia.Architecture.Tests` is not editable to make your change pass.**
  If it fails, your layering is wrong. Fix the layering.

## Jest outside the app (api-client, contract, admin, marketing)

One runner for all TypeScript, at the major `jest-expo` requires
([ADR 0029](../../docs/adr/0029-one-testing-shape.md) rule 3). The workspace
members spread the root `jest.base.js`; admin and marketing state their own
`jest.config.js`, because they are outside the workspace.

- **Import the API from `@jest/globals`** (`describe`, `it`, `expect`, `jest`)
  rather than relying on injected globals, so `tsc` and ESLint see real
  bindings.
- **ESM is transpiled to CommonJS by Babel.** Never add
  `--experimental-vm-modules`. Code a test loads must not read `import.meta`:
  move the read into its own module and map it (admin's
  `build-environment.ts`), or import the JSON (`contract/src/support/env.ts`).
- **`expect` takes one argument.** To say which exchange or field failed, wrap
  the assertion: `withMessage(label, () => expect(x).toBe(y))`
  (`contract/src/support/wire.ts`).
- **`process.env` is saved and restored by hand** — marketing's
  `test/env.ts` (`stubEnv` / `restoreEnv`); there is no `stubEnv` in Jest.
  Replace a global function with `jest.spyOn(globalThis, 'fetch')`, which
  `restoreMocks` (or `jest.restoreAllMocks()`) puts back.
- **`it.each` takes mutable tuples.** Type the table (`it.each<[A, B]>([...])`)
  instead of `as const`.

## The api-client contract pin

`packages/insolvia_api_client/src/index.test.ts` **is** the client's half of
the contract with the API; `contract/` is the server's half, driving the same
client against the running .NET hosts. There is no OpenAPI codegen, by
decision.

- **Every endpoint added to the API gets a hand-written method here, a
  contract test here, *and* a call in `contract/`** (its coverage gate fails
  on a client method nothing calls), in the same PR.
- **Assert the exact request and the exact response mapping** — method, URL,
  headers, body — against a stubbed `fetch`. Copy the literal JSON the server
  actually returns; read the route handler to get it, don't infer it.
- **Import by package name**, not relative path, so the test exercises the real
  export map.
- A server field rename is *supposed* to break these tests. That is the feature.

## The deployed target (`contract/`, `CONTRACT_TARGET=deployed`)

The same specs as the offline run; [`contract/CLAUDE.md`](../../contract/CLAUDE.md)
owns the detail (the gates in `support/gates.ts`, scratch teardown in
`support/actors.ts`).

- **It drives each surface over HTTP and nothing in-process**, through the
  client its callers run: the shipped api-client, the portal's admin client,
  the official MCP SDK; `seededStaff('paralegal')` is a real token from
  the real pool. It imports no backend code and reaches no store — a test
  that needs to is a backend test wearing the wrong directory.
- **Who exists comes from `seeds/<target>.json`, by handle.** A spec that needs
  a person the fixture lacks adds them to the fixture (and the next seed
  creates them); it never invents an address or a subject.
- **Scratch discipline.** Cases are only soft-deleted through the API (the
  rows stay), so work
  inside scratch cases `support/actors.ts` tracks and deletes in teardown;
  restore any firm setting you change; never write to a fixture case.
- **A test that passes only on one target is a bug in the test.** Both `dev`
  and `staging` run the same files; branch on the fixture's contents (a
  one-firm fixture cannot express cross-tenant isolation — return early and
  say so through `needsSeeded`), never on the target name; a spec that
  genuinely cannot run deployed says so through `offlineOnly(reason)`.
- **Never a required PR check, never a unit-tier dependency.** It runs after
  the staging alias shift and on a developer's local stack, and its failure
  fails the staging stage. (The contract suite is the offline, per-PR check
  of the same wire.)

## End-to-end (`e2e/`)

- **Two projects, by what they may hold.** `flows` signs in and is offered
  against dev and staging; `smoke` holds no credentials and is the only thing
  that runs against production. A smoke spec that needs a token is a `flows`
  spec; a `flows` spec that needs nothing signed-in is a `smoke` spec.
- **Adding a `flows` test needs a reason the other layers cannot satisfy.**
  Slow, environment-dependent, and a flake blocks production promotion through
  `verified-commit`. A `smoke` test needs a reason a `curl` cannot satisfy.
- **Never make either a required PR check** — that puts staging's availability
  on every PR's critical path. Post-deploy only.
- **Write only to the scratch case** (`support/scratch-case.ts`), never to a
  seeded fixture case — the loader will not repair it and the next integration
  run cannot find it. Reading a fixture case is fine.
- **Role-based selectors only**, matching the app's accessibility contract.
- **Never let a credential reach a file, a default, a log line, or an uploaded
  artifact.** Traces record typed values verbatim and the repo is public.
- If a spec starts passing only on retry, **quarantine it** — do not raise the
  retry count.

## Seed data

- **`seeds/<env>.json` says who exists and which fixture cases they hold;
  `seeds/fixtures/<version>/` says what a case contains**, in the API's own
  body shapes. The loader parses both with the route's own parsers, so a
  malformed row fails with the API's field errors — write a fixture the way
  you would write a request.
- **A loaded fixture is never edited; a changed fixture is a new version.**
  Rows that exist are left alone on every load, so editing `v1` after it has
  loaded changes nothing anywhere. Copy the latest version to the next and
  edit it by hand (`seeds/README.md`; the Python `capture` was not ported).
- **Nothing real, ever.** Every value describes nobody; every address ends in
  `.test`.

## Things this repo has decided against

Each of these looks like an improvement and is not. Do not add one without an
ADR:

- **A coverage threshold as a merge gate.** It rewards exactly the tests
  ADR 0008 says not to write. Measuring coverage for information is fine.
- **A mocking or network-mocking library** (Moq, NSubstitute, WireMock, and
  their kin). Ports plus memory twins, and a hand-written `HttpMessageHandler`
  where a transport must be faked, already cover this.
- **A second test runner in an existing unit**, or migrating one to another.
- **Snapshot tests.** Nothing in the repo uses them; they pin rendered output
  rather than behaviour, and they get regenerated rather than read.
- **Testing an area's private helpers directly.** Test the exported surface.

## Where a new test file goes

| You changed | Test goes |
|---|---|
| a domain rule or an engine | `backend/tests/Insolvia.<Core\|Engines>.Tests/`, pure, table-driven |
| a store | the conformance suite both the twin and the Postgres store run (`Insolvia.Data.Tests/Conformance`) |
| a route | `backend/tests/Insolvia.<Host>.Tests/Routes/`, through `WebApplicationFactory` |
| a new AWS-touching adapter | a port in `Insolvia.Core`, a twin beside it, the adapter in `Insolvia.Aws` with its transport faked |
| an app component | beside it, `<name>.test.tsx`, rendered, queried by role |
| an app route | beside the screen, via `renderRouter` |
| an API endpoint | the route's backend tests, the api-client contract pin, **and** a `contract/` call |
| an admin route, or an `AdminClient` method | the route's backend tests, `packages/insolvia_admin_client`'s pin, **and** a call in `contract/src/admin` (its gate counts every method) |
| an MCP tool | `Insolvia.Mcp.Tests`, **and** a call in `contract/src/mcp` through the SDK (its gate requires every listed tool) |
| a design-system component | beside it — and remember the `.web`/`.native` leaves are separate files with separate tests |
| an infra invariant | prefer an executable check (`Insolvia.Architecture.Tests`, a `terraform test`, a workflow guard) over prose |
| something only a real bucket, pool, database role or the deployed Lambda role can answer | a `contract/src/` spec through the api-client, inside a scratch case — it runs deployed too |
| something only a real browser and a real sign-in can answer | `e2e/tests/flows/<subject>.spec.ts` |
| something a production deploy must announce when broken | `e2e/tests/smoke/<subject>.spec.ts` — no credentials, and it runs on staging first |
| a person or a case a suite needs to exist | `seeds/<env>.json` (who) and `seeds/fixtures/<version>/` (what) — never a script, never a secret |
