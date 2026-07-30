# insolvia_tokens

The **single source of truth** for every Insolvia design token.

Insolvia ships two front-end stacks — React Native (the Expo app) and
React/Tailwind (marketing and other web surfaces). Hand-maintaining a palette in
two places guarantees drift, so neither of them owns the values: `tokens.json`
does, and a small script renders it into both.

```
tokens.json  ──┬──▶ apps/insolvia_marketing/app/styles/theme.css   (Tailwind @theme)
               └──▶ packages/insolvia_tokens/src/tokens.ts          (typed, for the app)
```

## The rule

**Never hand-edit a generated file.** Both of them open with a `DO NOT EDIT`
banner naming `npm run tokens`. Edit `tokens.json`, then regenerate. CI enforces
this (see *Drift check* below), so a hand-edit fails the PR rather than silently
surviving until the stacks disagree.

Both banners name `npm run tokens`, and that is a recent simplification worth
noting so nobody re-splits them. `theme.css` used to land inside a version-gated
design-system package, so its banner deliberately named no toolchain — keeping a
command out of those expensive bytes is what let the generator be re-implemented
in a different language without a design-system PR. That package is gone
([ADR 0006](../../docs/adr/0006-theming-over-design-system.md)): `theme.css` now
generates straight into `apps/insolvia_marketing/app/styles/`, an ordinary app
directory with no version gate, so the toolchain-agnostic banner lost its reason
to exist and both outputs now point at the same regeneration command.

## Regenerating

From the repo root:

```bash
npm run tokens
```

The generator is intentionally dependency-free (`node:fs` + `node:path` only —
no Style Dictionary, no build step, no devDependency of its own). At this token
count a single readable script beats a configuration-driven pipeline.

What keeps that true is **Node's native TypeScript type-stripping** (Node >=24):
`npm run tokens` is plain `node packages/insolvia_tokens/tool/generate-tokens.ts`,
with no loader, no bundler and no compile step. The one cost is that
type-stripping cannot execute `enum`, `namespace`, or constructor parameter
properties — `erasableSyntaxOnly` in `tsconfig.base.json` turns using one into a
typecheck error rather than a runtime crash.

## Drift check

```bash
npm run tokens:check
```

Regenerates in memory and exits non-zero, listing the offending paths, if any
committed output differs. It runs as the first step of the root `npm run ci`
gate (`package.json`), so every workspace member's PR check covers both outputs —
the app's `tokens.ts` and marketing's `theme.css`.

## Token structure

| Group | Consumed by | Notes |
|---|---|---|
| `palette` | neither output | Raw brand primitives (`ink`, `brass`, `paper`, …). Semantic tokens alias them; **nothing** emits them — see below. |
| `spacing`, `radii` | both | React Native gets density-independent pixels; CSS gets `rem` (`radii` carries an explicit `css` value). |
| `shadows` | CSS only | React Native's shadow model is per-platform and not worth a shared token yet. |
| `fonts` | both | Type families as authored; React Native resolves a single registered family. |
| `semantic` | both | The indirection layer, with a `light` and `dark` mapping per token. |
| `semanticDerived` | both | Hover/active states computed from another semantic token. |

Every token carries a `description`. That string becomes the doc comment on the
generated TypeScript member, so the JSON is the only place documentation is
written.

### Semantic indirection

Raw palette names are an implementation detail. Consumers — components, themes,
and downstream apps — speak only the semantic vocabulary (`primary`, `accent`,
`bg`, `ink`, `muted`, `line`, `card`, `danger`, …), and a re-brand swaps the
mapping in one file instead of touching every call site. This is why neither
output emits palette names at all: there is nothing for a consumer to
accidentally couple to.

`semanticDerived` tokens keep that property for interaction states. In CSS they
emit `color-mix(in srgb, var(--color-primary) 88%, black 12%)`, so overriding
`--color-primary` also moves its hover state. React Native cannot defer that
computation, so the generator pre-computes the identical sRGB blend at
generation time — both stacks land on the same pixel.

## The TypeScript output

`src/tokens.ts` is the React Native app's copy, imported as `@insolvia-ai/tokens`
and fed straight to `StyleSheet.create`. Plain exported consts: `colors`
(`light` + `dark`), `spacing`, `radii`, `typography`. No CSS, no Tailwind, no
runtime, no dependencies.

Its types are the point. `ColorScheme` declares every semantic role as required,
and `colors` is checked against `Record<ColorSchemeName, ColorScheme>`, so **a
missing dark-mode value is a compile error** rather than an `undefined` that
renders as a transparent box on one theme. A `ColorSchemeName` that is named but
never declared trips a `never` assertion in the same file.

Generated or not, `src/tokens.ts` is ordinary source as far as the toolchain is
concerned: it sits inside the `prettier --check` and `eslint` targets, so the
generator has to emit Prettier-clean bytes.

The package is private and unpublished; `src/tokens.ts` is exported as source
and consumed by symlink, so there is no build output and no version gate.
