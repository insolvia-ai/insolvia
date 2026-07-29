# insolvia_api_client — agent rules

Hand-written typed client for `services/api`. Human docs:
[`README.md`](README.md).

**Two clients live here for exactly one PR.** `src/` is the TypeScript client
(the one the Expo app will use); `lib/` + `test/` are the Dart client it
replaces, and a later PR deletes them. Until then **both must stay green** —
`.github/workflows/api-client-pr.yml` runs a job per half (`API client` and
`Dart API client`), and a contract change has to land in both.

## Rules that apply to both halves

- **No OpenAPI codegen — the tests are the contract pin.**
  `src/index.test.ts` and `test/insolvia_api_client_test.dart` assert the exact
  paths, methods, field names, status codes, and error-body shapes
  `services/api` produces. A contract change must break those tests. Keep the
  two suites readable side by side, assertion for assertion.
- **Models mirror the wire format exactly** (camelCase: `currentSoftware`,
  `submittedAt`); optional request fields are **omitted when absent, never
  sent as `null` or `""`**.
- Every endpoint added to the API gets a hand-written method here **and** a
  contract test alongside it — in both halves, while both exist.

## TypeScript half (`src/`)

- **No dependencies, runtime or dev.** Platform `fetch`, no HTTP library;
  `vitest`/`typescript`/`eslint`/`prettier` come from the workspace root.
- **No build step** — `package.json` exports `./src/index.ts`, which Metro and
  Node >=24 consume directly. Write relative imports with a literal `.ts`
  extension (`from './models.ts'`); the `//allowImportingTsExtensions` comment
  in the root `tsconfig.base.json` owns that rule and the trap it avoids.
- **The module split mirrors the Dart client file for file** —
  `src/{models,exceptions,client}.ts` ↔ `lib/src/{models,exceptions,client}.dart`,
  with `src/index.ts` as the barrel. Keep it that way while both halves exist:
  a contract review reads the two side by side.
- The root `tsconfig.base.json` is strict and `erasableSyntaxOnly` is
  load-bearing — no `enum`, no `namespace`, no constructor parameter
  properties. `npm run typecheck --workspace @insolvia-ai/api-client` is what
  catches them.
- The test imports the client **by package name**, not by relative path, so it
  exercises the real export map.

## Dart half (`lib/`, `test/`) — maintenance only

- Pure Dart, no Flutter dependency; workspace member (`resolution: workspace`).
- Don't add features here. Fix it only to keep it green, or to carry a
  contract change across from the TypeScript half.
