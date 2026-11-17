# insolvia_api_client

A hand-written, typed client for the Insolvia API (`services/api`), published
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

Issue #66 left the door open to generating this client from an OpenAPI
spec "if practical". It is not, yet, and the decision is deliberate:

- `services/api` ships **no OpenAPI spec today** — there is nothing to
  generate from. Authoring a spec *plus* adopting a generator toolchain
  (and its output style, its dependency set, its CI hooks) to cover **two
  endpoints** is more machinery than the surface justifies, and generated
  output is typically far noisier than the ~200 lines here.
- The real risk codegen addresses — the client silently drifting from the
  API — is covered another way: **this package's tests are the contract
  pin.** `src/index.test.ts` asserts the exact paths, methods, field names,
  status codes, and error-body shapes the API actually produces, with
  pointers back to the `services/api` source they mirror. A contract change
  must break those tests.

The OpenAPI route stays open: when the API surface grows past a handful of
endpoints (real case/e-filing resources), publish a spec from
`services/api` and revisit generating this package from it. Until then,
every endpoint added to the API gets a hand-written method here and a
contract test alongside it.

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
