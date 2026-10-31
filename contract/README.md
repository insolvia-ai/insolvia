# contract — the backend's HTTP contract suite

A Vitest suite that calls a running backend by base URL and checks what comes
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
```

No AWS, no sign-in, no network beyond 127.0.0.1. It mints a throwaway
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
accepted; the Python target it once chose against went with the Python
backend.

## Aim it at another stack

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
