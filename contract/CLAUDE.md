# contract — agent rules

The backend's HTTP contract suite (ADR 0028, plan 18.2). Human docs:
[`README.md`](README.md). Run: `contract/scripts/dev-test.sh` (Python, the
default) or `--target dotnet` (the .NET hosts on Postgres — ADR 0028's
acceptance gate). CI: `Contract (.NET)` runs exactly the latter.

- **It is the oracle for the .NET rewrite.** Every test must pass against the
  Python stack today and the .NET stack later, unchanged. So assert the
  **wire** — status, the raw JSON's field names and types, the error envelope —
  never Python internals, log lines or store contents. If a test only passes
  because of how Flask or a memory adapter happens to behave, it is wrong.
- **API calls go through `@insolvia-ai/api-client`, imported by package name.**
  Never a hand-rolled `fetch` to an API route: the point is to prove the
  shipped client. `support/client.ts` wraps the real class — a recording
  `fetch` (for `wire(status, spec)`) and a Proxy (for the coverage gate). Admin
  and MCP have no client; they are raw HTTP / JSON-RPC.
- **The coverage gate is the gate.** `src/coverage.setup.ts` reads the method
  list off `InsolviaApiClient.prototype` and fails the run if one was never
  called. A new client method needs a call here in the same PR. A filtered run
  (`dev-test.sh <filter>`) reports but does not enforce.
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
- **Tokens are real.** `support/tokens.ts` signs RS256 JWTs with the claims
  Cognito and Google issue; the stack verifies them with the same JWKS
  providers its deployed entrypoints use. Never add a verification bypass to
  make a test pass — break one claim per negative test instead.
- **`stack/` is Python and dies with the Python (18.23).** It composes the
  services from outside `services/` because admin's architecture test forbids
  importing `insolvia_api`, and one shared set of memory stores needs one
  process. It must not change route behaviour: it only chooses adapters, as
  the services' own `development_server.py` files do. `LocalBlobStore` is the
  one adapter it adds (an S3 stand-in, so presigned PUTs work).
- **The .NET stack is composed by `scripts/dev-test.sh --target dotnet`**,
  from the hosts' own configuration surfaces — no host code knows the suite
  exists. Seeded ids are not shared between stacks (Python derives from table
  names, .NET from the environment), so a test finds fixture records through
  the API (`support/fixtures.ts`), never by id; the people's subjects are the
  one exception, and `stack/stack.json` lists the ones the migrator derives.
  `scripts/aux-server.ts` is the .NET side's JWKS and S3 stand-in, sharing
  `backend/src/Insolvia.Core/Local/DirectoryDocumentBlobStore.cs`'s layout.
- **TypeScript rules are the workspace's**: no build step, `.ts` relative
  imports, `erasableSyntaxOnly`. Gate: `npm run lint`, `npm run typecheck`,
  `npm run format` from the root all cover this directory.
