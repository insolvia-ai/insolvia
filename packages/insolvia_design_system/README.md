# insolvia_design_system

The shared **Flutter** UI for Insolvia — tokens, theme, and components. It is the
one deliberately-shared package: the desktop app and the Flutter web app both
build on it. (The marketing site has its own React design system,
`@insolvia-ai/design-system`; the two share only token *values*, generated from
[`packages/insolvia_tokens`](../insolvia_tokens).)

## Working on it

This package resolves **outside** the pub workspace, so set it up standalone:

```bash
scripts/dev-setup.sh            # standalone: fvm flutter pub get
fvm flutter analyze
fvm flutter test
```

To see a change in the running app before publishing, add an *uncommitted*
`apps/insolvia_app/pubspec_overrides.yaml` pointing at this package by path, and
delete it before committing — see
[`docs/PACKAGE_PUBLISHING.md`](../../docs/PACKAGE_PUBLISHING.md).

## Consuming it

The app depends on this package as a **git tag**, never a path:

```yaml
insolvia_design_system:
  git:
    url: https://github.com/insolvia-ai/insolvia.git
    path: packages/insolvia_design_system
    ref: insolvia_design_system-v<version>
```

Merging to `main` publishes the tag automatically; upgrading is a `ref` bump.
Full flow: [`docs/PACKAGE_PUBLISHING.md`](../../docs/PACKAGE_PUBLISHING.md).

## Structure

`lib/src/{tokens,theme,components}/`, with one barrel export
`lib/insolvia_design_system.dart`. Everything under `tokens/` except
`typography.dart` is generated from `insolvia_tokens`.

The conventions and rules for changing this package (semantic colors only,
generated files, the version-bump requirement, the widget-test rule) are in
[`CLAUDE.md`](CLAUDE.md).
