# insolvia_tokens — agent rules

The single source of truth for every design token. Human docs:
[`README.md`](README.md).

- **`tokens.json` is the only place token values live** — pure data, no CSS, no
  TypeScript. Every color, spacing step, radius, shadow, and font.
- **Never hand-edit a generated file.** `tool/generate-tokens.ts` renders two
  outputs: `apps/insolvia_marketing/app/styles/theme.css` (Tailwind `@theme` for
  the marketing site) and this package's own `src/tokens.ts` (typed, for the
  app), each with a `DO NOT EDIT` banner naming `npm run tokens`. To change a
  value: edit `tokens.json`, then `npm run tokens` from the repo root. CI gate:
  `npm run tokens:check` (part of the root `npm run ci`; fails the PR on drift,
  naming the file you edited). If `git diff` is non-empty after `npm run tokens`,
  the *generator* is wrong — never reconcile by editing a generated file.
- **Both banners name `npm run tokens` now — don't re-split them.** `theme.css`
  once landed inside `insolvia_design_system_react`, a version-gated package, so
  its banner deliberately named no toolchain to keep those expensive bytes
  toolchain-agnostic (that is what let the generator be re-implemented in a
  different language with no design-system PR). That package is gone
  ([ADR 0006](../../docs/adr/0006-theming-over-design-system.md)): `theme.css`
  generates straight into an ordinary marketing app directory with no version
  gate, so the special-case banner lost its reason and both outputs point at the
  same regeneration command.
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
  (`ink`/`brass`/`paper`) — a re-brand is then a one-file change. Neither output
  emits the palette at all; keep it that way.
- **A missing dark-mode color must stay a compile error.** `src/tokens.ts`
  declares every semantic role as required on `ColorScheme` and asserts scheme
  exhaustiveness with a `never` type. Don't relax either into an optional
  property or a runtime fallback.
- Workspace member of the npm workspace. `package.json` is private and
  unpublished, so its version is not gated.
