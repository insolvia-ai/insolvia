---
name: insolvia-new-package
description: >-
  How to add a new package, app, or backend service to the Insolvia monorepo.
  Use this when scaffolding a new buildable unit — "add a package", "create a
  new app", "scaffold a service", "new shared library", "a new backend host or project" —
  so it lands with the workspace wiring, the README + CLAUDE.md pair, the CI
  workflows, and the infra entry the repo expects. Reach for it before creating
  the directory, so nothing is missed.
---

# Adding a new package / app / service

## TypeScript package or app (npm workspace member)

1. Create it under `packages/<name>/` (shared library) or `apps/<name>/`
   (runnable app), with its own `package.json` and a `tsconfig.json` that
   `extends` the root `tsconfig.base.json`.
2. Add it to the root `package.json` `workspaces` list **by exact path** — the
   list is deliberately explicit and must never become `packages/*` or
   `apps/*`. `apps/insolvia_marketing` must stay OUT: it consumes
   `@insolvia-ai/design-system` *by published version*, and a member symlink
   would build it against local source, so a broken package would pass CI and
   only break after publishing. See the comment block in the root
   `package.json` — it owns this reasoning.
3. Give it a **`README.md`** (human: what it is + how to run it via its
   `scripts/`) and a **`CLAUDE.md`** (agent rules/conventions for that area).
   Every area has both — see any existing package for the shape. Add a one-line
   `eslint.config.js` re-exporting the root `eslint.base.js` (there is
   deliberately no discoverable `eslint.config.js` at the repo root — see that
   file for why).
4. If it deploys, add `<name>-pr.yml` + `<name>-<env>.yml` workflows (follow the
   existing `<area>-pr.yml` / `<area>-<env>.yml` shape and the PR-gate design in
   [`docs/reference/architecture.md`](../../../docs/reference/architecture.md)) and an `infra/envs/*`
   entry. **If it ships a container image, its ECR repository comes first, in
   another repository:** add `insolvia-shared-<component>` to
   [insolvia-services](https://github.com/insolvia-ai/insolvia-services)'
   `infra/envs/shared` and let *that* repo's release apply it **before** the PR
   here that deploys it — the env root's `data "aws_ecr_repository"` lookup
   hard-fails until the repository exists, and nothing orders the two repos'
   pipelines for you. If the deploy role needs a new grant for it, that is a
   `ci-trust` PR there too (the `insolvia-deploy-role-permissions` skill).
5. Document it in the map in the root [`README.md`](../../../README.md).

## Backend project or host (.NET — not a workspace member)

Lives in `backend/` as `src/Insolvia.<Name>/` with a matching
`tests/Insolvia.<Name>.Tests/`, both listed in `backend/Insolvia.slnx`, and
named in `tests/Insolvia.Architecture.Tests`' layering rule — the test fails
until all of that is done ([`backend/CLAUDE.md`](../../../backend/CLAUDE.md)
owns the rule). Package versions go in `Directory.Packages.props`; commit the
`packages.lock.json` it generates. A new **host** (a deployable unit) also
gets its own `backend/Dockerfile` target, a build line in `backend-pr.yml`, a
`<name>-<env>.yml` deploy pair, a leg in `release.yml`, and its ECR repository
first in insolvia-services (step 4 above). There is no Python service any
more (ADR 0028, plan 18.23). Steps 3–5 above still apply.

## The design system is in another repository

If the new thing is a shared component or a design token, this skill is the
wrong door — and so is this repository. `@insolvia-ai/design-system` and
`@insolvia-ai/tokens` live in
[`insolvia-ai/design-system`](https://github.com/insolvia-ai/design-system) and
reach this repo only as published versions
([ADR 0010](../../../docs/adr/0010-design-system-moves-to-its-own-repository.md)).
Add the component there, publish, then use the
`insolvia-design-system-bump` skill to take the version.

Do not create a local package to hold shared UI as a way around that. The app
had a source-level channel to the design system once, through a workspace
symlink, and removing it is the whole point of ADR 0010.
