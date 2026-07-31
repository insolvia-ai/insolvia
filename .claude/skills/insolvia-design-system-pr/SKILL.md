---
name: insolvia-design-system-pr
description: >-
  The rule that any change to the design-system package must be its OWN pull
  request with a version bump. Use this BEFORE editing ANYTHING under
  packages/insolvia_design_system/ — including README/CLAUDE.md/docs, not just
  component code. It publishes to GitHub Packages on merge to main, and CI
  fails any PR that changes the package without bumping its version. Bundling
  a design-system change into an unrelated PR breaks that PR's checks and
  couples a publish to unrelated work. Reach for this the moment a task will
  touch a file in the design-system directory.
---

# Design-system changes ship in their own PR

There is **one** design-system package: `packages/insolvia_design_system`
(`@insolvia-ai/design-system`, 0.2.x) — the owned, platform-split components:
per component, a shared `<name>.props.ts` plus a `.web.tsx` and a `.native.tsx`
leaf, with the consumer's bundler picking the leaf. It **publishes to npm on
GitHub Packages on merge to `main`** (`design-system-publish.yml`) and is
version-gated in CI. The gate (`Require a version bump when the package
changed`, in `design-system-pr.yml`) fires on **any** file under the package
directory — README and `CLAUDE.md` included, not just `src/`. Bump `version`
in the package's `package.json`.

> Two predecessors are gone. The web-only
> `packages/insolvia_design_system_react` (Base UI, 0.1.x of the same npm
> name) was replaced by this package in the cross-platform cutover; before
> that, a Flutter package occupied this same directory name and published as a
> git tag (removed with the Flutter stack, ADR 0004). A search of old history
> turns both up — neither exists now.

## Rules

1. **A change to the package goes in its OWN PR** — never bundled with app,
   docs, infra, or other work. If a broad change (e.g. a repo-wide docs pass or
   a toolchain change) would touch a design-system file, split that file out.
2. **Bump the version in the same PR.** CI fails the PR otherwise, and an
   unbumped merge publishes nothing (idempotent by version) — the surface
   silently goes stale under the marketing site.
3. **Only marketing takes a consume PR — the app never does.** Two consumers,
   two channels: the app is a fellow npm workspace member and consumes
   **source** through the symlink, so it picks up a merged change
   automatically; an app-side "update the dependency" PR no longer exists.
   The marketing site consumes the **published version** — only after the new
   version publishes may a *separate, later* PR bump
   `apps/insolvia_marketing/package.json` + its lockfile. Never pin a version
   that isn't published yet — that breaks `npm ci` in CI.
4. **The package IS a workspace member; marketing must never become one.**
   The member symlink is the app's consumption channel, so do not "clean it
   up" out of the root `package.json` `workspaces`. Adding
   `apps/insolvia_marketing` instead would symlink *it* to local source, and a
   broken package would pass CI and only break after publishing. (The root
   member list is explicit for this reason — see `insolvia-new-package`.)

## The pattern's own rules — a PR here must respect them

- **Props modules import nothing platform-specific.** `<name>.props.ts` and
  `src/lib/` never import `react-native`, `react-dom`, or any other renderer —
  ESLint enforces it; don't weaken that override.
- **Never add a build step.** The package publishes `src/` as-is because leaf
  resolution (`.web.tsx` vs `.native.tsx`) belongs to the *consumer's*
  bundler; any package-side emit (tsup, `tsc --emit`) collapses the pair and
  breaks both consumers.
- **`src/styles/theme.css` is generated** from
  `packages/insolvia_tokens/tokens.json` — edit `tokens.json` and run
  `npm run tokens`, never the CSS itself.

Editing the app's own code under `apps/insolvia_app/` is ordinary app work in
an ordinary app PR — this rule fires only on the package directory.

Full publish + consume flow:
[`docs/PACKAGE_PUBLISHING.md`](../../../docs/PACKAGE_PUBLISHING.md). The
package's full rule set: `packages/insolvia_design_system/CLAUDE.md`.
