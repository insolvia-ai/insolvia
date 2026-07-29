---
name: insolvia-design-system-pr
description: >-
  The rule that any change to the design-system package must be its OWN pull
  request with a version bump. Use this BEFORE editing ANYTHING under
  packages/insolvia_design_system_react/ — including README/CLAUDE.md/docs/
  scripts, not just component code. It publishes to GitHub Packages on merge to
  main, and CI fails any PR that changes the package without bumping its version.
  Bundling a design-system change into an unrelated PR breaks that PR's checks
  and couples a publish to unrelated work. Reach for this the moment a task will
  touch a file in the design-system directory.
---

# Design-system changes ship in their own PR

There is **one** design-system package: `packages/insolvia_design_system_react`
(`@insolvia-ai/design-system`), the marketing site's UI. It **publishes to npm on
GitHub Packages on merge to `main`** and is version-gated in CI. The gate
(`Require a version bump when the package changed`, in `design-system-react-pr.yml`)
fires on **any** file under the package directory — README, `CLAUDE.md`, and
`scripts/` included, not just `src/`. Bump `version` in `package.json`.

> There used to be a second one, `packages/insolvia_design_system` (Flutter,
> published as a git tag). It went with the rest of the Flutter stack in the
> React Native migration (ADR 0004). If a search turns it up in old history,
> that is why — it no longer exists.

## What this does NOT cover

**The app has its own design system, and it is not this.** `app.insolvia.ai`
renders bare React Native primitives from `apps/insolvia_app/src/components/`.
That directory is **not published, not version-gated, and not a design-system
package** — editing it is ordinary app work in an ordinary app PR. The two design
systems share only token *values* (via `@insolvia-ai/tokens`), never components,
and this rule applies to the marketing one alone.

## Rules

1. **A change to the package goes in its OWN PR** — never bundled with app,
   docs, infra, or other work. If a broad change (e.g. a repo-wide docs pass or
   a toolchain change) would touch a design-system file, split that file out.
2. **Bump the version in the same PR.** CI fails the PR otherwise, and an
   unbumped merge publishes nothing (idempotent by version) — the surface
   silently goes stale.
3. **Consumers update in a *separate, later* PR.** Only after the new version
   publishes may a PR bump the marketing dependency
   (`apps/insolvia_marketing/package.json` + its lockfile). Never pin a version
   that isn't published yet — that breaks `npm ci` in CI.
4. **Never make it a root workspace member.** It is consumed by its published
   version, not by path; adding it to the root `package.json` `workspaces` would
   symlink marketing to local source, so a broken package would pass CI and only
   break after publishing. (This is why the root member list is explicit and
   excludes it — see `insolvia-new-package`.)

Full publish + consume flow: [`docs/PACKAGE_PUBLISHING.md`](../../../docs/PACKAGE_PUBLISHING.md).
