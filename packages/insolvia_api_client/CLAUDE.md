# insolvia_api_client — agent rules

Hand-written typed client for `services/api`, consumed by the Expo app as
`@insolvia-ai/api-client`. Human docs: [`README.md`](README.md).

Gated by `.github/workflows/api-client-pr.yml` (the `API client` job): format,
lint, typecheck, test.

- **No OpenAPI codegen — the tests are the contract pin.** `src/index.test.ts`
  asserts the exact paths, methods, field names, status codes, and error-body
  shapes `services/api` produces. A contract change must break those tests.
- **Models mirror the wire format exactly** (camelCase: `currentSoftware`,
  `submittedAt`); optional request fields are **omitted when absent, never
  sent as `null` or `""`**.
- Every endpoint added to the API gets a hand-written method here **and** a
  contract test alongside it.
- **No dependencies, runtime or dev.** Platform `fetch`, no HTTP library;
  `vitest`/`typescript`/`eslint`/`prettier` come from the workspace root.
- **No build step** — `package.json` exports `./src/index.ts`, which Metro and
  Node >=24 consume directly. Write relative imports with a literal `.ts`
  extension (`from './models.ts'`); the `//allowImportingTsExtensions` comment
  in the root `tsconfig.base.json` owns that rule and the trap it avoids.
- **The module split is `src/{models,exceptions,client}.ts`** with
  `src/index.ts` as the barrel and the package's only entry point. What the
  barrel re-exports is the public surface.
- The root `tsconfig.base.json` is strict and `erasableSyntaxOnly` is
  load-bearing — no `enum`, no `namespace`, no constructor parameter
  properties. `npm run typecheck --workspace @insolvia-ai/api-client` is what
  catches them.
- The test imports the client **by package name**, not by relative path, so it
  exercises the real export map.
