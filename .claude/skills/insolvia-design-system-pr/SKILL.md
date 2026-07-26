---
name: insolvia-design-system-pr
description: >-
  The rule that any change to either design-system package must be its OWN pull
  request with a version bump. Use this BEFORE editing ANYTHING under
  packages/insolvia_design_system/ or packages/insolvia_design_system_react/ —
  including README/CLAUDE.md/docs/scripts, not just component code. Both packages
  publish on merge to main (a git tag for Flutter, GitHub Packages for React),
  and CI fails any PR that changes the package without bumping its version.
  Bundling a design-system change into an unrelated PR breaks that PR's checks
  and couples a publish to unrelated work. Reach for this the moment a task will
  touch a file in either design-system directory.
---

# Design-system changes ship in their own PR

Both design-system packages **publish on merge to `main`** and are version-gated
in CI. The gate (`Require a version bump when the package changed`) fires on
**any** file under the package directory — README, `CLAUDE.md`, and `scripts/`
included, not just `src/`/`lib/`.

- `packages/insolvia_design_system` (Flutter) → git tag
  `insolvia_design_system-v<version>`; gate in `design-system-pr.yml`. Bump
  `version:` in `pubspec.yaml`.
- `packages/insolvia_design_system_react` (`@insolvia-ai/design-system`) → npm
  on GitHub Packages; gate in `design-system-react-pr.yml`. Bump `version` in
  `package.json`.

## Rules

1. **A change to either package goes in its OWN PR** — never bundled with app,
   docs, infra, or other work. If a broad change (e.g. a repo-wide docs pass or
   a toolchain change) would touch a design-system file, split that file out.
2. **Bump the version in the same PR.** CI fails the PR otherwise, and an
   unbumped merge publishes nothing (idempotent by version) — the surface
   silently goes stale.
3. **Consumers update in a *separate, later* PR.** Only after the new version
   publishes may a PR bump the app's git-tag `ref`
   (`apps/insolvia_app/pubspec.yaml` + root `pubspec.lock`) and the marketing
   dependency (`apps/insolvia_marketing/package.json` + its lockfile). Never pin
   a version that isn't published yet — that breaks `pub get` / `npm ci` in CI.

Full publish + consume flow: [`docs/PACKAGE_PUBLISHING.md`](../../../docs/PACKAGE_PUBLISHING.md).
