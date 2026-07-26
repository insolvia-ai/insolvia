# insolvia_design_system — agent rules

Shared Flutter UI. Human docs: [`README.md`](README.md). Publishing flow:
[`docs/PACKAGE_PUBLISHING.md`](../../docs/PACKAGE_PUBLISHING.md).

- **Outside the pub workspace, deliberately** — pub silently rewrites a
  dependency on a workspace member back to a local path, which would defeat the
  app's tag pin. It resolves standalone (`flutter pub get` inside the package);
  its `pubspec.lock` is a library lock and is **not** committed.
- **The `lib/src/tokens/` files (except `typography.dart`) are generated** from
  `packages/insolvia_tokens` — never hand-edit them; change tokens there.
- **Read `InsolviaSemanticColors`, never `InsolviaPalette`.** Brand values
  Material lacks go in a `ThemeExtension` (`InsolviaColors`, `InsolviaSpacing`),
  read via `Theme.of(context).extension<…>()`.
- **Every exported component has ≥1 widget test.**
- **Any change to this package is its OWN PR, with a `version` bump in
  `pubspec.yaml`** — never bundled with app/docs/infra work (the version-bump
  gate fires on *any* file here, README/scripts included). Merge to `main`
  publishes the git tag `insolvia_design_system-v<version>` (CI-enforced); an
  unbumped change tags nothing and the published surface goes stale. Consumers
  pin the tag in a *separate, later* PR. See the `insolvia-design-system-pr` skill.
