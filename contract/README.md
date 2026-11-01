# contract — the backend's HTTP contract suite

A Jest suite that calls a running backend by base URL and checks what comes
back over the wire. It was built for the .NET rewrite (ADR 0028, plan 18.2):
the Python backend passed it first, the .NET one passed it unchanged before
the switch, and since the Python was deleted (18.23) it is what holds the
wire contract the app depends on.

- **API** calls go through the shipped client, `@insolvia-ai/api-client`, so a
  green run proves the client the app ships, not a copy of it.
- **Admin** (all seven routes) and **MCP** (protected-resource metadata,
  discovery, `initialize`, the tool listing and all eight tools) are called over
  raw HTTP and JSON-RPC.
- **A coverage gate** fails the run when any public `InsolviaApiClient` method
  was never called. The run prints `client coverage: N/N methods called`.

## Run it

```bash
npm ci                                          # once, at the repo root
contract/scripts/dev-test.sh                    # the .NET stack on Postgres: start, run, stop
contract/scripts/dev-test.sh mcp                # a filtered run (the gate is not enforced)
contract/scripts/dev-test.sh --target deployed  # against this machine's running dev stack (below)
```

Offline (the default): no AWS, no sign-in, no network beyond 127.0.0.1. It mints a throwaway
RS256 key, starts the stack, waits for it, runs the suite, and stops it.
Ports 18060–18090, overridable with `CONTRACT_*_PORT`.
`CONTRACT_SHOW_STACK_LOG=1` prints every stack log on a failure.

It needs Docker and the .NET SDK. It starts a throwaway `postgres:17`
container, runs the real migrator (migrations, roles, grants, then the local
seed of `seeds/dev.json` and its fixture versions), starts
`scripts/aux-server.ts` (the run's JWKS, and the blob server the API's local
directory store points its presigned URLs at), then the API, admin and MCP
hosts — each as its own database role, trusting the key through
configuration only (`AUTH_ISSUER_URL`; admin's local-only `GOOGLE_JWKS_URL`).
CI runs exactly this as `Contract (.NET)`
(`.github/workflows/contract-dotnet-pr.yml`). `--target dotnet` is still
accepted as another name for `--target offline`.

## The deployed target

The same suite runs against a **running** dev stack or staging, signed in by
real Cognito SRP as the people `seeds/<environment>.json` names
(`src/support/session.ts`, `src/support/cognito-srp.ts`). Staging runs it after
every API deploy (`api-staging.yml`'s `integration` job); on a laptop:

```bash
backend/scripts/dev-up.sh                       # the compose stack, API on :8080
scripts/dev-aws-seed.sh                         # once: the seeded people exist in the shared dev pool
contract/scripts/dev-test.sh --target deployed  # sign in, run, report
```

The script reads the pool ids from `backend/.env` and the password from
`E2E_TEST_USER_PASSWORD` or `~/.config/insolvia/dev.env`. It never aims at
staging — CI does that. **There is no production target**:
`CONTRACT_ENVIRONMENT` must be `dev` or `staging`, and the global setup
refuses to sign anyone in unless the API's `/health` reports that environment.

Every input comes from the environment, with no defaults in the suite:

| Variable | What |
|---|---|
| `CONTRACT_TARGET` | `deployed` (`offline`, the default, is the stack this script starts) |
| `CONTRACT_ENVIRONMENT` | `dev` or `staging` — picks `seeds/<environment>.json` |
| `CONTRACT_API_URL` | the API origin |
| `CONTRACT_AUTH_POOL_ID` | the Cognito user pool |
| `CONTRACT_AUTH_CLIENT_ID` | the firm app's client (staff sign in here) |
| `CONTRACT_AUTH_PORTAL_CLIENT_ID` | the portal's client (portal clients sign in here) |
| `E2E_TEST_USER_PASSWORD` | the seeded people's password — never in a file here |

What changes on the deployed target:

- **Writes go to scratch only, and are undone.** Scratch clients and cases are
  tracked and deleted (cases) or archived (clients) after each file
  (`src/scratch.setup.ts`); firm settings a spec changes are restored in
  `finally`. Fixture cases are found by their content and never written to —
  with one documented exception, the seeded client's upload, which deletes
  what it adds.
- **Some specs cannot run there, and say so by name.** `src/support/gates.ts`
  is the one way to mark a spec: `offlineOnly(reason)` (it signs its own
  tokens, provisions a firm through admin, or waits out a sign-in window),
  `needsSeeded(...)` (the seed has no second firm, or no unlinked colleague),
  `needsBlobUrls()` (the dev stack's blob store has no URL to fetch). Jest
  lists each skipped spec with its reason in brackets. Admin and MCP are
  offline-only until #583.
- **The coverage gate is reported, not enforced** — it is enforced only on a
  full offline run.

## Aim it at another offline stack

Start the stack yourself, then:

| Variable | What |
|---|---|
| `CONTRACT_API_URL` | the API origin |
| `CONTRACT_ADMIN_URL` | the admin service origin |
| `CONTRACT_MCP_URL` | the MCP endpoint, path included (`…/mcp`) |
| `CONTRACT_ISSUER_URL` | the Cognito-shaped issuer the stack trusts |
| `CONTRACT_SIGNING_KEY` | a PKCS#8 PEM whose public half the stack serves as that issuer's JWKS (and as Google's, for admin) |

```bash
npm run contract --workspace @insolvia-ai/contract
```

The stack must trust the client ids in `stack/stack.json`, know the people in
its `accounts` (the subjects the .NET migrator's local seed derives), and be
seeded from `seeds/dev.json`.
