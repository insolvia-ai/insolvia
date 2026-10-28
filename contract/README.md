# contract — the backend's HTTP contract suite

A Vitest suite that calls a running backend by base URL and checks what comes
back over the wire. It exists for the .NET rewrite (ADR 0028,
proposed; plan 18.2): the Python backend is the oracle, this suite is what it is the
oracle *for*, and the switch to .NET waits until both pass it unchanged.

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
contract/scripts/dev-test.sh                    # the Python stack: start, run, stop
contract/scripts/dev-test.sh --target dotnet    # the .NET stack on Postgres
contract/scripts/dev-test.sh mcp                # a filtered run (the gate is not enforced)
```

No AWS, no sign-in, no network beyond 127.0.0.1. Both targets mint a
throwaway RS256 key, start the stack, wait for it, run the suite, and stop
it. Ports 18060–18090, overridable with `CONTRACT_*_PORT`.
`CONTRACT_SHOW_STACK_LOG=1` prints every stack log on a failure.

- **`--target python`** (the default until the switch, plan 18.23) needs
  Python 3.12; the first run builds `contract/.venv`. It starts
  `stack/contract_stack.py`: the API, admin and MCP services composed over
  their in-memory adapters in one process, seeded from `seeds/dev.json`.
- **`--target dotnet`** needs Docker and the .NET SDK. It starts a
  throwaway `postgres:17` container, runs the real migrator (migrations,
  roles, grants, then the local seed of `seeds/dev.json` and its fixture
  versions), starts `scripts/aux-server.ts` (the run's JWKS, and the blob
  server the API's local directory store points its presigned URLs at), then
  the API, admin and MCP hosts — each as its own database role, trusting the
  key through configuration only (`AUTH_ISSUER_URL`; admin's local-only
  `GOOGLE_JWKS_URL`). CI runs exactly this as `Contract (.NET)`
  (`.github/workflows/contract-dotnet-pr.yml`).

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
