# insolvia_tokens

The **single source of truth** for every Insolvia design token.

Insolvia ships two front-end stacks — Flutter (desktop + web app) and
React/Tailwind (marketing and future web surfaces). Hand-maintaining a palette
in both guarantees drift, so neither owns the values: `tokens.json` does, and a
small Dart script renders it into both.

```
tokens.json  ──┬──▶ packages/insolvia_design_system/lib/src/tokens/colors.dart
               ├──▶ packages/insolvia_design_system/lib/src/tokens/spacing.dart
               ├──▶ packages/insolvia_design_system/lib/src/tokens/radii.dart
               ├──▶ packages/insolvia_design_system/lib/src/tokens/semantics.dart
               └──▶ packages/insolvia_design_system_react/src/styles/theme.css
```

## The rule

**Never hand-edit a generated file.** Every one of them opens with a
`DO NOT EDIT` banner naming the regeneration command. Edit `tokens.json`, then
regenerate. CI enforces this (see *Drift check* below), so a hand-edit fails the
PR rather than silently surviving until the two stacks disagree.

## Regenerating

From the repo root:

```bash
dart run packages/insolvia_tokens/tool/generate_tokens.dart
```

or, through Melos:

```bash
melos run tokens
```

The generator is intentionally dependency-free (`dart:io` + `dart:convert`
only — no Style Dictionary, no Node toolchain). At this token count a single
readable script beats a configuration-driven pipeline.

## Drift check

```bash
dart run packages/insolvia_tokens/tool/generate_tokens.dart --check   # melos run tokens:check
```

Regenerates in memory and exits non-zero, listing the offending paths, if any
committed output differs. Wired into `.github/workflows/design-system-pr.yml`.

## Token structure

| Group | Consumed by | Notes |
|---|---|---|
| `palette` | Dart only | Raw brand primitives (`ink`, `brass`, `paper`, …). **Not** emitted as CSS variables — see below. |
| `spacing`, `radii` | both | Dart gets logical pixels; CSS gets `rem` (`radii` carries an explicit `css` value). |
| `shadows`, `fonts` | CSS only | Flutter takes elevation from Material and type from `typography.dart`. |
| `semantic` | both | The indirection layer, with a `light` and `dark` mapping per token. |
| `semanticDerived` | both | Hover/active states computed from another semantic token. |

Every token carries a `description`. That string becomes the doc comment on the
generated Dart member, so the JSON is the only place documentation is written.

### Semantic indirection

Raw palette names are an implementation detail. Consumers — components, themes,
and downstream apps — speak only the semantic vocabulary (`primary`, `accent`,
`bg`, `ink`, `muted`, `line`, `card`, `danger`, …), and a re-brand swaps the
mapping in one file instead of touching every call site. This is why the CSS
output emits no `--color-ink-brass`-style palette variables at all: there is
nothing for a consumer to accidentally couple to.

`semanticDerived` tokens keep that property for interaction states. In CSS they
emit `color-mix(in srgb, var(--color-primary) 88%, black 12%)`, so overriding
`--color-primary` also moves its hover state. Dart cannot defer that
computation, so the generator pre-computes the identical sRGB blend at
generation time — the two stacks land on the same pixel.

On the Dart side the layer is `InsolviaSemanticColors.light` / `.dark`, which
`InsolviaTheme` reads instead of reaching for `InsolviaPalette` directly.
