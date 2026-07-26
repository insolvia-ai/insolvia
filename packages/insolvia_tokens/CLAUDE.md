# insolvia_tokens — agent rules

The single source of truth for every design token. Human docs:
[`README.md`](README.md).

- **`tokens.json` is the only place token values live** — pure data, no Flutter,
  no CSS. Every color, spacing step, radius, shadow, and font.
- **Never hand-edit a generated file.** `tool/generate_tokens.dart` renders five
  outputs: `insolvia_design_system/lib/src/tokens/{colors,spacing,radii,semantics}.dart`
  and `insolvia_design_system_react/src/styles/theme.css`, each with a
  `DO NOT EDIT` banner. To change a value: edit `tokens.json`, then
  `melos run tokens`. CI gate: `melos run tokens:check` (fails the PR on drift).
- **Consumers speak the semantic layer only** (`primary`, `accent`, `bg`, `ink`,
  `muted`, `line`, `card`, `danger`, …), never raw palette names
  (`ink`/`brass`/`paper`) — a re-brand is then a one-file change.
- Workspace member — bump `version` in `pubspec.yaml` when it changes.
