# insolvia_api_client

A hand-written, typed client for the Insolvia API (`backend/src/Insolvia.Api`), published
into the workspace as `@insolvia-ai/api-client` and consumed by the Expo app.

## Surface

```ts
import { InsolviaApiClient } from '@insolvia-ai/api-client';

const client = new InsolviaApiClient('https://staging-api.insolvia.ai');

const status = await client.health(); // GET /health
const confirmation = await client.joinWaitlist({
  // POST /v1/waitlist
  name: 'Ada Lovelace',
  firm: 'Lovelace Law LLC',
  email: 'ada@lovelace.law',
});
```

The constructor takes an optional `{ fetch }` override; without one it calls
the platform `fetch`, looked up at call time so a host that polyfills late
still works. There is no `close()` — `fetch` owns no client to release.

## Failure model

- **`ApiValidationException`** — a 400 with `{"error", "fields"}`; carries
  the per-field messages verbatim (keyed by JSON field name) so a form can
  surface each next to its input.
- **`ApiException`** — any other unexpected status, or a success status
  whose body is not valid JSON; carries the status code and raw body.
- **Transport failures** (DNS, refused connection, timeouts) propagate
  untouched, as the `TypeError` `fetch` throws. Callers can distinguish "the
  API rejected this" from "the network is down" by type alone.

## Why hand-written, not generated from an OpenAPI spec

The API now has an OpenAPI document
([`docs/reference/openapi/api.json`](../../docs/reference/openapi/api.json)),
but it is generated *from* this package — its types and its methods' doc
comments — not the other way round. The .NET hosts write their JSON by hand
and cannot describe their own bodies, and this package's types are the
most exact statement of the wire there is: `src/index.test.ts` pins paths,
methods, field names, status codes and error shapes, and `contract/` drives
this same client against the running hosts. Generating the client from the
document would make that loop circular.

So every endpoint added to the API still gets a hand-written method here, a
contract test, and a row in `tool/openapi/api-routes.ts` (the document's
route table; `npm run openapi` regenerates it).

## Conventions

- Models mirror the wire format exactly (camelCase: `currentSoftware`,
  `submittedAt`); optional request fields are omitted when absent, never
  sent as `null` or `""`.
- Tests stub the transport with a `fetch` stub. No live server, no test server.
- Zero dependencies and no build step. `package.json` exports `./src/index.ts`
  directly, because Metro and Node >=24 both consume TypeScript natively.
  `src/{models,exceptions,client}.ts` carry the client, with `src/index.ts` as
  the barrel and the package's only entry point.

## Running it locally

```sh
npm ci                                              # from the repo root
npx prettier --check packages/insolvia_api_client
npx eslint packages/insolvia_api_client
npm run typecheck --workspace @insolvia-ai/api-client
npm run test --workspace @insolvia-ai/api-client
```
