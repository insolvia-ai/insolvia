# @insolvia-ai/admin-client

The typed client the staff portal (`apps/insolvia_admin`) uses to call the
admin service (`backend/src/Insolvia.Admin`): six methods over its firm and
firm-user routes, a token provider read per request, and two error classes.

It lives here, not in the portal, because it has two consumers: the portal,
and `contract/`, whose admin specs go through exactly this code against the
real service ([ADR 0029](../../docs/adr/0029-one-testing-shape.md) rule 2).
It is private and never published.

```bash
npm run test --workspace @insolvia-ai/admin-client       # Jest, stubbed fetch
npm run typecheck --workspace @insolvia-ai/admin-client
npm run lint --workspace @insolvia-ai/admin-client
```
