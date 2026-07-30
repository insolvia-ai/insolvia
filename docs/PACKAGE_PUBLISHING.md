# Publishing packages

**Nothing in this repository publishes to a registry.** There is no npm publish,
no GitHub Packages upload, no version gate, no publish workflow. This page exists
to say that plainly, record why, and describe what publishing *would* involve if
a package ever earns it back.

## Why nothing publishes

Every buildable unit is either a **private app** or a **private workspace
member consumed by symlink** — none of them crosses a registry boundary:

| Unit | What it is | How it's consumed |
|---|---|---|
| `apps/insolvia_app` | Expo app (private) | deployed as a static bundle, not installed |
| `apps/insolvia_marketing` | React Router SSR site (private) | deployed as a Lambda image, not installed |
| `packages/insolvia_tokens` | `@insolvia-ai/tokens` (private) | npm-workspace symlink; the app imports its `src/tokens.ts` source directly |
| `packages/insolvia_api_client` | `@insolvia-ai/api-client` (private) | npm-workspace symlink |

Both packages are workspace members in the root `package.json`, so a consumer
in the repo resolves them by symlink to their source in the same commit. There
is no published artifact to fall behind that source, so there is nothing to
version-gate and nothing to publish. `npm run tokens:check` guards the
*generated* token files against drift; that is a build-freshness check, not a
release step.

## The one design system this document used to describe is gone

This page used to be the runbook for publishing **`@insolvia-ai/design-system`**
— the React design system in `packages/insolvia_design_system_react/`, pushed to
GitHub Packages and installed by the marketing site. That package **dissolved
into the marketing site** ([ADR 0006](adr/0006-theming-over-design-system.md)):
its components are ordinary themed code in `apps/insolvia_marketing/app/ui/`, and
the site now shares only *token values* with the app, not components.

Because the design system was the only thing that published, its removal took
the whole publish story with it — the `design-system-react-pr.yml` /
`design-system-react-publish.yml` workflows, the version-bump gate, the
`ssr.noExternal` build trick, and the GitHub Packages auth for `npm ci` are all
gone. **The package name `@insolvia-ai/design-system` is retired**; nothing
resolves it and nothing will republish it.

> **Two orphaned artifacts survive on the remote, both harmless.** The Flutter
> design system before it (`packages/insolvia_design_system`, deleted under D9)
> was "published" as the annotated git tag `insolvia_design_system-v0.1.2`,
> which is still on the remote and resolves to nothing. The React design system
> may likewise have `@insolvia-ai/design-system` versions lingering in GitHub
> Packages. Neither is deleted here: removing a published tag or package version
> is the one irreversible step, and a stale artifact misleads nobody once this
> paragraph exists.

## If a design system comes back

Reintroduce a published, versioned package only when a **second consumer with a
real boundary** appears — a separately-built or separately-deployed surface that
would otherwise install a stale copy of shared code. [ADR
0006](adr/0006-theming-over-design-system.md) has the full test for when that is
merited. The mechanics a return would need — a registry (GitHub Packages accepts
only the org-login scope, `@insolvia-ai`, not `@insolvia`), a `dist` build, a
publish-on-merge workflow, a PR gate that fails an unbumped change, and — for an
SSR consumer — Vite's `ssr.noExternal` to keep a registry token out of the
runtime image — are recoverable from this file's git history (`git log --
docs/PACKAGE_PUBLISHING.md`).

## Related

- [ADR 0006](adr/0006-theming-over-design-system.md) — theming over a design
  system: why the shared component package went away and when one returns.
- [`packages/insolvia_tokens/README.md`](../packages/insolvia_tokens/README.md)
  — the token source, the generator, and the two generated outputs.
- [`ARCHITECTURE.md`](ARCHITECTURE.md) — the monorepo shape and where these
  packages sit.
- [`MVP_PLAN.md`](MVP_PLAN.md) — decision D4 (one token source) as revised by D9
  and D-level context.
