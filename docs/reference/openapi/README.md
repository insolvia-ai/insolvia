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

Each host generates its document from its endpoints: the handlers' typed
results, their doc comments, and the auth and permission metadata on each
route ([ADR 0031](../../adr/0031-typed-routes-and-a-generated-openapi-document.md);
`backend/src/Insolvia.Http/WireOpenApi.cs`). A running host serves it at
`/openapi/v1.json` and renders it at `/docs`. These files are committed
copies:

```bash
backend/scripts/dev-openapi.sh    # rewrite both from the hosts
```

## Why they can be trusted

- **Freshness and coverage:** `OpenApiDocumentTests` in
  `backend/tests/Insolvia.Api.Tests` and `Insolvia.Admin.Tests` fail when
  a committed copy differs from what the host serves, when a mapped route
  is missing, or when an operation declares no typed success response.
- **Bodies:** the contract suite (`contract/scripts/dev-test.sh`) checks every
  request it sends and every answer it gets against the documents. That
  covers each operation a client method calls.

Response schemas are the server's own types, so they list every field it
sends. Request schemas are docs-only records (`.RequestBody<T>()`); the
route's parser still owns validation.
