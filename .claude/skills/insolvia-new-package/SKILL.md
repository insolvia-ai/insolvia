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

## Dart package or app (pub workspace member)

1. Create it under `packages/<name>/` (shared library) or `apps/<name>/`
   (runnable app), with its own `pubspec.yaml` (`resolution: workspace`).
2. Add it to the root `pubspec.yaml` `workspace:` list.
3. Give it a **`README.md`** (human: what it is + how to run it via its
   `scripts/`) and a **`CLAUDE.md`** (agent rules/conventions for that area).
   Every area has both — see any existing package for the shape.
4. If it deploys, add `<name>-pr.yml` + `<name>-<env>.yml` workflows (follow the
   existing `<area>-pr.yml` / `<area>-<env>.yml` shape and the PR-gate design in
   [`docs/ARCHITECTURE.md`](../../docs/ARCHITECTURE.md)) and an `infra/envs/*`
   entry.
5. Document it in the map in the root [`README.md`](../../README.md).

## Python service (the exception — not a workspace member)

Lives under `services/<name>/`, is **not** a pub workspace member (no
`pubspec.yaml`, no root `workspace:` entry). Use the mailer-style layered `src/`
layout (`core/api/adapters/entrypoints`) with a per-service `pyproject.toml`
(pytest) plus the shared root `ruff.toml`, and its own `tests/test_architecture.py`
enforcing the dependency direction. Steps 3–5 above still apply.

## The design systems are special

If the new thing is a design-system surface, read
`packages/insolvia_design_system_react/CLAUDE.md` first — the React package is
capped at six components and the parity rules gate what may be added.
