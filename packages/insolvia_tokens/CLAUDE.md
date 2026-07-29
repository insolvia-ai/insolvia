# insolvia_tokens — agent rules

The single source of truth for every design token. Human docs:
[`README.md`](README.md).

- **`tokens.json` is the only place token values live** — pure data, no Flutter,
  no CSS, no TypeScript. Every color, spacing step, radius, shadow, and font.
- **Never hand-edit a generated file.** `tool/generate-tokens.ts` renders six
  outputs: `insolvia_design_system/lib/src/tokens/{colors,spacing,radii,semantics}.dart`,
  `insolvia_design_system_react/src/styles/theme.css`, and this package's own
  `src/tokens.ts`, each with a `DO NOT EDIT` banner. To change a value: edit
  `tokens.json`, then `npm run tokens` from the repo root. CI gate:
  `npm run tokens:check` (fails the PR on drift, naming the file you edited).
- **Two generators exist right now, and must agree byte for byte.**
  `tool/generate_tokens.dart` is the original and still runs its own `--check`
  in `design-system-pr.yml` for this one PR; `tool/generate-tokens.ts` is the
  port. Running either must leave the other's committed output untouched — if
  `git diff` is non-empty after `npm run tokens`, the *generator* is wrong.
  Never reconcile them by editing a generated file. Do not touch
  `generate_tokens.dart`; it is deleted with the rest of the Flutter stack.
- **Add no dependencies to the generator** (`node:fs` + `node:path` only). It
  runs as plain `node …/generate-tokens.ts` — no loader, no build step — on
  Node's native type-stripping, so it **cannot use `enum`, `namespace`, or
  constructor parameter properties**. `erasableSyntaxOnly` in
  `tsconfig.base.json` makes `npm run typecheck` catch all three.
- **Generated output is linted and formatted like any other source.**
  `src/tokens.ts` is inside the `prettier --check` and `eslint` targets, so the
  generator has to emit Prettier-clean bytes — including Prettier's quote choice
  and line-breaking. Verify with `npm run format && npm run lint`, never by
  reformatting the output file.
- **Consumers speak the semantic layer only** (`primary`, `accent`, `bg`, `ink`,
  `muted`, `line`, `card`, `danger`, …), never raw palette names
  (`ink`/`brass`/`paper`) — a re-brand is then a one-file change. Neither the CSS
  nor the TypeScript output emits the palette at all; keep it that way.
- **A missing dark-mode color must stay a compile error.** `src/tokens.ts`
  declares every semantic role as required on `ColorScheme` and asserts scheme
  exhaustiveness with a `never` type, mirroring the no-default-arm switches in
  `apps/insolvia_app/lib/config/environment.dart`. Don't relax either into an
  optional property or a runtime fallback.
- Workspace member of both the pub and npm workspaces — bump `version` in
  `pubspec.yaml` when it changes. `package.json` is private and unpublished, so
  its version is not gated.
