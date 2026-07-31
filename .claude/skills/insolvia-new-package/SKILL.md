---
name: insolvia-new-package
description: >-
  How to add a new package, app, or backend service to the Insolvia monorepo.
  Use this when scaffolding a new buildable unit — "add a package", "create a
  new app", "scaffold a service", "new shared library", "spin up services/<x>" —
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
   [`docs/ARCHITECTURE.md`](../../../docs/ARCHITECTURE.md)) and an `infra/envs/*`
   entry.
5. Document it in the map in the root [`README.md`](../../../README.md).

## Python service (the exception — not a workspace member)

Lives under `services/<name>/`, is **not** an npm workspace member (no
`package.json` in the root `workspaces` list). Use the mailer-style layered `src/`
layout (`core/api/adapters/entrypoints`) with a per-service `pyproject.toml`
(pytest) plus the shared root `ruff.toml`, and its own `tests/test_architecture.py`
enforcing the dependency direction. Steps 3–5 above still apply.

## The design system is special

If the new thing is a design-system surface, this skill is the wrong door:
read the `insolvia-design-system-pr` skill and
`packages/insolvia_design_system/CLAUDE.md` first. That package is version
gated (own PR + bump), platform-split (`.props.ts`/`.web.tsx`/`.native.tsx`
per component), and both a workspace member and published — the one package
where "just add a directory" is never the whole job.
