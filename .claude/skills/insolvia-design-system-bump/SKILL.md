---
name: insolvia-design-system-bump
description: >-
  How to take a new @insolvia-ai/design-system or @insolvia-ai/tokens version
  into this repo — and the rule that the packages themselves are NOT here to
  edit. Use this whenever a task means changing a shared component (Button,
  Field, Card, Select…), changing a design token or colour, or picking up a
  design-system release: "update the design system", "bump tokens", "change the
  button's padding", "make the brand colour warmer". Reach for it BEFORE
  searching for packages/insolvia_design_system, which no longer exists — the
  packages live in github.com/insolvia-ai/design-system and reach this repo
  only as published versions. It covers which manifests and which TWO lockfiles
  must move together, and why a bump that skips `npm run tokens` leaves the
  sign-in page's colours behind.
---

# Taking a design-system or tokens version

## First: the package is not in this repo

`@insolvia-ai/design-system` and `@insolvia-ai/tokens` live in
**[`insolvia-ai/design-system`](https://github.com/insolvia-ai/design-system)**.
There is no `packages/insolvia_design_system` here any more, and there must not
be one again — see
[ADR 0010](../../../docs/adr/0010-design-system-moves-to-its-own-repository.md).

So a task that says "change the Button" is **two** pieces of work:

1. Change and release it in the design-system repo (its own CI gates the
   version bump and publishes on merge).
2. Bump the dependency here — that is what this skill covers.

**Never** short-circuit step 1 with a `file:` or `link:` path to a local
checkout, even temporarily, even uncommitted. The app used to read the
package's source through a workspace symlink; one package with two live states
is the exact failure ADR 0010 removed. To try an unpublished change, publish a
prerelease from that repo and depend on it by version.

## The bump

Both surfaces consume the packages, and **they do not share a lockfile.**
Missing one is the usual way a bump half-lands.

| File | What to change |
|---|---|
| `package.json` (root) | `devDependencies` → `@insolvia-ai/tokens` — the Cognito reconciler resolves it from here |
| `apps/insolvia_app/package.json` | `dependencies` → `@insolvia-ai/design-system`, `@insolvia-ai/tokens` |
| `apps/insolvia_marketing/package.json` | `devDependencies` → `@insolvia-ai/design-system` (marketing does not use tokens) |
| `package-lock.json` | regenerate with `npm install` from the repo root |
| `apps/insolvia_marketing/package-lock.json` | regenerate with `npm install` **inside `apps/insolvia_marketing`** — it is deliberately outside the workspace |

Installing needs a GitHub Packages token, even for public packages:

```bash
eval "$(./scripts/github-packages-auth.sh --export)"
```

## If tokens moved, regenerate the branding

`infra/modules/auth/managed-login-settings.json` — the colours of Cognito's
hosted sign-in page — is generated from the **installed** `@insolvia-ai/tokens`:

```bash
npm run tokens
```

Commit whatever that writes. `npm run tokens:check` is a required PR check
(`Cognito branding`), and it fails on a tokens bump that did not regenerate —
which is the whole reason it watches `package.json`/`package-lock.json`. Skipping
it leaves the sign-in page on the old palette while the app moves to the new
one, and nothing else in the tree would say so.

## Then verify, locally

```bash
npm run ci
```

```bash
npm run build --workspace @insolvia-ai/app
```

The build matters more than usual for a design-system bump: the app renders the
package's `.native` leaves on **every** platform, web included, through a Metro
`resolveRequest` override in `apps/insolvia_app/metro.config.js`. If a release
reorganises the package's internals, that override is what breaks, and it breaks
*silently* — the bundle still builds, having resolved the `.web` leaves, whose
Tailwind classes reference CSS the app never compiles. A bundle that looks fine
and renders unstyled is the failure mode to watch for.

Marketing has its own gate:

```bash
npm run typecheck --prefix apps/insolvia_marketing
```

## Scope

A version bump is ordinary work and does **not** need its own PR — that rule
belonged to the package while it lived here. Bundle it with the change that
needs it. What still needs care is the list above moving together: a manifest
bumped without its lockfile fails `npm ci` in CI, and a marketing lockfile left
behind means the two surfaces silently render different versions of the same
component.
