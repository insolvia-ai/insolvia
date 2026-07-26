# insolvia_api_client — agent rules

Hand-written typed Dart client for `services/api`. Human docs:
[`README.md`](README.md).

- **Pure Dart, no Flutter dependency** — usable from the app, scripts, and any
  Dart tooling. Workspace member (`resolution: workspace`).
- **No OpenAPI codegen — the tests are the contract pin.**
  `test/insolvia_api_client_test.dart` asserts the exact paths, methods, field
  names, status codes, and error-body shapes `services/api` produces. A contract
  change must break those tests.
- **Models mirror the wire format exactly** (camelCase: `currentSoftware`,
  `submittedAt`); optional request fields are omitted when `null`, never sent as
  `null`/`""`.
- Every endpoint added to the API gets a hand-written method here **and** a
  contract test alongside it.
