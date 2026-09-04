# services/mcp

Insolvia's remote MCP server ([ADR 0016](../../docs/adr/0016-mcp-server-is-its-own-service.md)):
the tool surface [`docs/reference/mcp-surface.md`](../../docs/reference/mcp-surface.md)
specifies, exposed to an attorney's AI harness over Streamable HTTP. Eight
tools over the shared case domain (`packages/insolvia_core`): read cases and
their records, propose candidate records for human review, and poll the
review outcome. An agent never writes case data
([ADR 0013](../../docs/adr/0013-mcp-server-replaces-direct-pms-integration.md)).

Built on the official [MCP Python SDK](https://github.com/modelcontextprotocol/python-sdk)
(spec revision 2026-07-28, stateless Streamable HTTP) behind Mangum on
Lambda, exactly one instance per environment:

| Environment | Endpoint |
|---|---|
| local | `http://127.0.0.1:8788/mcp` (`scripts/dev-up.sh`) |
| staging | `https://staging-mcp.insolvia.ai/mcp` |
| prod | `https://mcp.insolvia.ai/mcp` |

Auth is OAuth against the environment's existing Cognito pool: the server is
an OAuth 2.1 resource server publishing RFC 9728 protected-resource metadata,
and every session resolves to a Cognito `sub` with firm permissions looked up
per call ([ADR 0009](../../docs/adr/0009-a-case-belongs-to-a-firm.md)).

## Layout

```
src/insolvia_mcp/
├── core/          config · candidates (the proposal domain) · tools (the
│                  eight tools' logic and gates) · the candidate-store port
├── api/           the MCP SDK wiring: server + tool registration, token
│                  verification, the result/error envelope
├── adapters/      aws + memory candidate stores (the case stores come from
│                  insolvia_core.adapters)
└── entrypoints/   mcp_lambda (Mangum) · development_server (uvicorn)
```

## Developing

```bash
./services/mcp/scripts/dev-setup.sh   # venv + this machine's dev AWS resources
./services/mcp/scripts/dev-up.sh      # serve http://127.0.0.1:8788/mcp
./services/mcp/scripts/dev-test.sh    # ruff + mypy + pytest, same as CI
```

`dev-up.sh` reads `services/mcp/.env` (written by `scripts/dev-aws-setup.sh`)
and runs against this machine's **real** per-developer tables and Cognito
pool — the same no-emulator rule as the API. Without the file it serves
in-memory stores with auth failing closed (every call 401s).

### Connecting an MCP inspector locally

1. `./services/mcp/scripts/dev-up.sh` in one terminal.
2. `npx @modelcontextprotocol/inspector` in another; choose transport
   **Streamable HTTP** and URL `http://127.0.0.1:8788/mcp`.
3. Hit connect: the inspector gets a 401 pointing at
   `/.well-known/oauth-protected-resource/mcp`, discovers the dev pool, and
   opens its hosted sign-in. Use the seeded dev account
   (`scripts/dev-aws-seed.sh`; password context in
   `~/.config/insolvia/dev.env`).
4. List tools, call `whoami`, then `list_cases` → `propose_case_records` →
   `check_proposals` against your own dev data.

If a client cannot run the browser flow, mint a token directly against the
dev pool's MCP app client (`aws cognito-idp initiate-auth` with
`USER_PASSWORD_AUTH`) and paste it as a bearer token — the server only ever
sees the token either way.

## Testing

pytest, colocated in `tests/` (`insolvia-testing` conventions): the tool
layer over in-memory ports is the pyramid's base, and
`tests/test_protocol.py` pins the MCP wire — JSON-RPC shape, the 401
challenge, the static eight-tool listing, structuredContent, the
`{error: {code}}` envelope — the way the api-client contract test pins the
REST surface.

CI gate: the `MCP service` job in `.github/workflows/mcp-pr.yml` (ruff, mypy
`--strict`, pytest, and the Lambda image build from the repo root).
