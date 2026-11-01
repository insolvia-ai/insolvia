# contract — agent rules

The backend's HTTP contract suite (ADR 0028, plan 18.2). Human docs:
[`README.md`](README.md). Run: `contract/scripts/dev-test.sh` — the .NET hosts
on a throwaway Postgres (the **offline** target, the default). CI: `Contract
(.NET)` runs exactly that. `--target deployed` runs the same specs against a
running dev stack. On staging, `api-staging.yml` runs them after each API
deploy, and `admin-staging.yml` and `mcp-staging.yml` run `src/admin` and
`src/mcp` after theirs (the integration tier of ADR 0021, folded in here by
ADR 0029).

- **It is the wire contract's oracle.** It was proven against the Python
  backend first and the .NET one at the switch, unchanged; since 18.23 deleted
  the Python it is what keeps the wire from drifting. So assert the **wire** —
  status, the raw JSON's field names and types, the error envelope — never
  host internals, log lines or store contents. If a test only passes because
  of how one host or a memory twin happens to behave, it is wrong.
- **Every surface goes through the client its callers run, imported by
  package name** (ADR 0029 rule 2). Never a hand-rolled `fetch` or JSON-RPC:
  the point is to prove the shipped client.
  - API: `@insolvia-ai/api-client`. `support/client.ts` wraps the real
    class — a recording `fetch` (for `wire(status, spec)`) and a Proxy (for
    the gate).
  - Admin: `@insolvia-ai/admin-client`, the portal's own client
    (`packages/insolvia_admin_client`). `support/admin.ts` wraps it the same
    way (`adminWire`, `expectAdminError`); the client calls the platform
    `fetch`, so the recorder wraps the global one. `support/browser.ts` is the
    one exception, and only for what a browser does that the client never
    does: the CORS preflight, a request with no credential, `/health`.
  - MCP: the official `@modelcontextprotocol/sdk` `Client` over Streamable
    HTTP, `connect()` (the handshake) included — `support/mcp.ts`. Refusals
    go through the SDK's `request`, not `callTool`: its REFUSALS comment
    says why. The 2026-07-28 transport no TypeScript SDK speaks yet is
    `Insolvia.Mcp.Tests`' `ProtocolTests`.
- **The coverage gates are the gate.** `src/coverage.ts` fails the run if a
  method of `InsolviaApiClient.prototype` or `AdminClient.prototype` was never
  called, or a tool the server's `tools/list` names was never called. A new
  client method or tool needs a call here in the same PR. A filtered run
  (`dev-test.sh <filter>`) or a deployed run reports but does not enforce.
- **Shapes come from `packages/insolvia_api_client/src/models.ts`.** `wire.ts`'s
  `expectShape` is deliberately not exact (extra fields pass) but a missing or
  retyped declared field fails, and an optional field must be ABSENT, never
  `null`. Shared shapes live in `support/shapes.ts`.
- **Errors: `expectApiError(call, status, code)`** checks the status, the
  client's exception class, and the `{"error", "message"|"fields"}` envelope.
  The API answers validation with **400** `ValidationError`; it has no 422.
- **Never write to seeded data.** The fixture cases (`support/fixtures.ts`,
  flmb/tampa) and the seeded firm admin are read; writes go to scratch clients
  and cases (`support/actors.ts`, flnb/gainesville). Firm-wide settings a test
  changes (questionnaire, checklist, profile) it restores. Files run serially.
  The one exception is the seeded client's upload (the only portal binding a
  deployed stack has); it adds one ad-hoc request and removes what it added.
- **Tokens are real.** `support/tokens.ts` signs RS256 JWTs with the claims
  Cognito and Google issue; the stack verifies them with the same JWKS
  providers its deployed entrypoints use. Never add a verification bypass to
  make a test pass — break one claim per negative test instead.
- **The stack is composed by `scripts/dev-test.sh`** from the hosts' own
  configuration surfaces — no host code knows the suite exists. A test finds
  fixture records through the API (`support/fixtures.ts`), never by id; the
  people's subjects are the one exception, and `stack/stack.json` lists the
  ones the migrator derives (the migrator's `SeedIdTests` reads it too).
  `scripts/aux-server.ts` is the run's JWKS and S3 stand-in, sharing
  `backend/src/Insolvia.Core/Local/DirectoryDocumentBlobStore.cs`'s layout.
- **Two targets, one set of specs** (`support/target.ts`). Deployed signs in
  by real SRP as `seeds/<environment>.json`'s people (`support/session.ts`);
  every input comes from the environment with no defaults, and **there is no
  production target** — `CONTRACT_ENVIRONMENT` is dev or staging, and
  `global.setup.ts` checks `/health` agrees before signing anyone in.
- **A spec that cannot run on a target says why, through `support/gates.ts`**
  — `offlineOnly(reason)`, `deployedOnly`, `needsSeeded`, `needsBlobUrls` —
  never an early `return` or an `if (TARGET…)` around assertions. Jest prints
  each skipped spec by name with its reason. Admin's signed-in specs are
  `offlineOnly` by decision: staff sign in through Google Workspace, which no
  deployed run can automate (ADR 0029), so deployed, admin is checked signed
  out. MCP signs in deployed through an MCP harness's client
  (`CONTRACT_AUTH_MCP_CLIENT_ID`, `session.ts`'s `mcpSignIn`). Prefer making a spec run on both:
  use `seededStaff`/`freshFirmAdmin` rather than minting a token, and a
  scratch case rather than a fixture.
- **Scratch is tracked and undone.** `scratchClient`/`scratchCase`
  (`support/actors.ts`) register what they create; `src/scratch.setup.ts`
  deletes the cases and archives the clients after each file. Anything else a
  spec writes on the deployed target it undoes in `finally`.
- **TypeScript rules are the workspace's**: no build step, `.ts` relative
  imports, `erasableSyntaxOnly`. Gate: `npm run lint`, `npm run typecheck`,
  `npm run format` from the root all cover this directory.
