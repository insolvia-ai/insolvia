# OpenAPI documents

What the backend's HTTP services accept and answer: every route, its verb,
who may call it, the permissions it needs, its request and response bodies,
and what it does.

| Document | Service |
|---|---|
| [`api.json`](api.json) | The API the app calls (`backend/src/Insolvia.Api`): 131 operations. |
| [`admin.json`](admin.json) | The admin service behind the staff portal (`backend/src/Insolvia.Admin`): 7 operations. |

Both are OpenAPI 3.1. Open either in any OpenAPI viewer, or read it in an
editor: each operation's `summary` and `description` say what it does, and
`x-insolvia-permissions` lists the `<feature>:<level>` a firm user's role
must hold. The MCP server speaks JSON-RPC, not REST, so it is not here:
[`../mcp-surface.md`](../mcp-surface.md) is its reference.

## Generated — never edit the JSON

```bash
npm run openapi          # regenerate both
npm run openapi:check    # fail if a committed document is stale
```

[`tool/render-openapi.ts`](../../../tool/render-openapi.ts) builds them from
one route table per service ([`tool/openapi/`](../../../tool/openapi/)) and
the clients' TypeScript types. Its header says why they are not generated
from the .NET hosts.

## Why they can be trusted

Three checks hold the documents to the running code:

- **Routes, principals and permissions:** `OpenApiDocumentTests` in
  `backend/tests/Insolvia.Api.Tests` and `Insolvia.Admin.Tests` compare each
  document with the endpoints the host maps. A route added, removed or
  re-permissioned without its table row fails `dotnet test`.
- **Bodies:** the contract suite (`contract/scripts/dev-test.sh`) checks every
  request it sends and every answer it gets against the documents. That
  covers each operation a client method calls (130 of the API's 131).
- **Freshness:** `npm run openapi:check` runs in the `Contract (.NET)` check.

Response schemas list the fields the clients read, which is not always every
field the server sends: the contract run's summary line counts the extra
ones. Request schemas are exact.
