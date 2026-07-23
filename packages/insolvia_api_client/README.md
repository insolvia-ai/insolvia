# insolvia_api_client

A hand-written, typed Dart client for the Insolvia API (`services/api`).
Pure Dart — no Flutter dependency — so it is usable from the Flutter app,
scripts, and any future Dart tooling alike.

## Surface

```dart
final client = InsolviaApiClient('https://staging-api.insolvia.ai');

final status = await client.health();            // GET /health
final confirmation = await client.joinWaitlist(  // POST /v1/waitlist
  const WaitlistSubmission(
    name: 'Ada Lovelace',
    firm: 'Lovelace Law LLC',
    email: 'ada@lovelace.law',
  ),
);
```

The app resolves the right base URL per environment from
`AppEnvironment.apiBaseUrl` (`apps/insolvia_app/lib/src/config/environment.dart`).

Failure model:

- **`ApiValidationException`** — a 400 with `{"error", "fields"}`; carries
  the per-field messages verbatim (keyed by JSON field name) so a form can
  surface each next to its input.
- **`ApiException`** — any other unexpected status, or a success status
  whose body is not valid JSON; carries the status code and raw body.
- **Transport failures** (DNS, refused connection, timeouts) propagate
  untouched as `package:http` exceptions — callers can distinguish "the API
  rejected this" from "the network is down" by type alone.

## Why hand-written, not generated from an OpenAPI spec

Issue #66 left the door open to generating this client from an OpenAPI
spec "if practical". It is not, yet, and the decision is deliberate:

- `services/api` ships **no OpenAPI spec today** — there is nothing to
  generate from. Authoring a spec *plus* adopting a generator toolchain
  (and its output style, its dependency set, its CI hooks) to cover **two
  endpoints** is more machinery than the surface justifies, and generated
  Dart is typically far noisier than the ~200 lines here.
- The real risk codegen addresses — the client silently drifting from the
  API — is covered another way: **this package's tests are the contract
  pin.** `test/insolvia_api_client_test.dart` asserts the exact paths,
  methods, field names, status codes, and error-body shapes the API
  actually produces, with pointers back to the `services/api` source they
  mirror. A contract change must break those tests.

The OpenAPI route stays open: when the API surface grows past a handful of
endpoints (real case/e-filing resources), publish a spec from
`services/api` and revisit generating this package from it. Until then,
every endpoint added to the API gets a hand-written method here and a
contract test alongside it.

## Conventions

- Pure Dart package, `resolution: workspace` — a member of the root pub
  workspace, mirroring `packages/insolvia_tokens`.
- Models mirror the wire format exactly (camelCase: `currentSoftware`,
  `submittedAt`); optional request fields are omitted when `null`, never
  sent as `null` or `""`.
- Tests use `MockClient` from `package:http/testing.dart` — no live server
  required.
