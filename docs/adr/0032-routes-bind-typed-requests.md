# ADR 0032 — Routes bind typed requests; the Python wire goes

- **Status:** Accepted 2026-11-11 — the maintainer asked for it ("fix this
  properly everywhere"); built across the PRs listed under *Build*.
- **Date:** 2026-11-11.
- **Relates to:** **Amends [ADR 0031](0031-typed-routes-and-a-generated-openapi-document.md)**:
  its responses stay typed; its requests stop being docs-only, and its
  `[SnakeCase]`, `[Flatten]` and document patching go. **Amends
  [ADR 0028](0028-backend-moves-to-dotnet-and-postgres.md)**: the HTTP contract
  changes, with the clients and the app in the same PRs.

## Decision

**A handler takes its request as a typed record and returns a typed
record.** The framework reads the body; nothing reads `HttpContext` for it,
and no handler sees a `JsonObject`:

```csharp
routes.MapPost("/v1/cases/{caseId}/creditors",
    (string caseId, EntityRequest<CreditorBody> request, …) => … TypedResults.Created(…, view));
```

**Validation is the framework's.** Limits are attributes on the record
(`[MaxLength]`, `[Range]`, `[RegularExpression]`); a rule across fields is
`IValidatableObject` on the record. `AddValidation()` checks the bound
request before the handler runs. A domain rule that needs the store (the
case is filed, the claim exists) stays in the handler.

**One JSON naming: the platform's.** camelCase keys everywhere (the wire,
the stored jsonb, provenance paths); nulls are left out when writing; money
is a JSON number read into `decimal`; a court date is `yyyy-MM-dd`; enum
values keep their spelled names. No `[SnakeCase]`, no `[Flatten]`, no
hand-written parsers (`FieldParsers`, `ParseBody`, `BodyAsync`).

**The error body stays `ErrorBody`** (`error`, `message`, `fields`). A
failed validation is a 400 `ValidationError` whose `fields` are keyed by the
camelCase path (`body.address.postalCode`); a body that is not the declared
type is the same, keyed by the path the reader stopped at. The clients'
exception mapping does not change.

**A case collection's request nests its body**: `{ body, provenance,
amended }` in, `{ id, caseId, createdAt, updatedAt, body, provenance,
amended }` out. Each collection's routes are mapped with its own body type,
one line each, no reflection.

**A stored body is the record, serialized.** Reading a row deserializes it;
it is never parsed again. What did need the parsers outside a request — the
extraction worker's candidates, a question's answer, the seed loader — reads
the same record type and validates it with the same attributes.

## Why

The .NET backend was a line-by-line port of a Python backend with no types:
one generic dictionary engine for every collection, hand parsers that
re-validated every body on every read, and a serializer that reproduced
Flask's output. ADR 0031 typed the responses but kept all of that underneath,
and then patched the OpenAPI document to describe it. Binding typed requests
removes the layers instead of describing them: the document is generated
from the parameters, the validation is declared where the field is, and a
reader of a route sees its request and response.

Nothing is live and prod data is disposable, so the wire and storage change
once, together with every client, instead of being carried forward.

## Consequences

- What the server no longer does: trim text, turn a whitespace-only string
  into null, accept money as a string, or report a wrong type per field
  beyond the first one the reader meets. The app sends what it means.
- The stored data is reseeded, not migrated: local by `scripts/dev-aws-seed.sh`
  and `scripts/dev-fixture.sh` (fixtures move to a new version), staging by
  its seed action, prod at its next promote.
- `packages/insolvia_api_client`, `packages/insolvia_admin_client`, the app,
  the admin portal, the MCP tools and `contract/` change in the same PR as
  the routes they call.

## Build

1. Validation into `ErrorBody`, and this ADR.
2. The case collections: typed bodies, nested requests, typed storage,
   extraction, questions, seeds and fixtures.
3. Cases, debtors and the lifecycle.
4. The firm: firm, settings, clients, library creditors, me, public.
5. Events, tasks, notes, documents, jobs, analyses, filing.
6. The portal.
7. The admin host and its client.
8. Delete what is left of the port's wire: `[SnakeCase]`, `[Flatten]`,
   `FieldParsers`, `BodyAsync`, the document patching.
