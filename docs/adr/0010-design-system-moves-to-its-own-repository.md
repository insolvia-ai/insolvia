# ADR 0010 — The design system moves to its own repository

- **Status:** Accepted
- **Date:** 2026-08-06
- **Relates to:** revises the *consumption* half of
  [ADR 0006](0006-owned-cross-platform-design-system.md) — the platform-split
  design, the three-files-per-component rule and the no-build-step rule all
  stand unchanged; supersedes the workspace-membership arrangement described in
  the root `package.json` and `docs/reference/package-publishing.md`.

## Decision

**`@insolvia-ai/design-system` and `@insolvia-ai/tokens` leave this repository
for [`insolvia-ai/design-system`](https://github.com/insolvia-ai/design-system),
and this repository consumes both as ordinary published dependencies.**

`@insolvia-ai/tokens` becomes published (0.2.0) in the process; it was private
and unpublished only because every consumer used to be a workspace sibling.

## Context — one package, two truths

ADR 0006 put both surfaces on one design system, but on two different channels:

| Consumer | Channel, before this ADR |
|---|---|
| `apps/insolvia_marketing` | the **published version** from GitHub Packages |
| `apps/insolvia_app` | the package's **source**, via a workspace symlink plus a Metro `resolveRequest` |

That second row is the problem. A merge to `main` was *instantly live* for the
app and *invisible* to marketing until someone published and bumped. The
package therefore had two simultaneous states, and "what does the design system
do right now?" had no single answer — it depended on which consumer was asking.

The failures this produced were quiet rather than loud:

- A change was validated against the app, which read source, and shipped to
  marketing weeks later as a version bump nobody re-tested.
- The reverse, more often: marketing sat several minor versions behind (it was
  pinned to `^0.2.1` while the package had reached 0.5.0) and nothing surfaced
  the gap, because the app — the surface people actually looked at — was always
  current by construction.
- Pressure to add dependencies to the package resolved differently depending on
  which consumer you had in mind. A dependency is free for a symlinked consumer
  and a forced install for a registry one, so the same change looked harmless
  and harmful at once.

Every one of those is a property of the *arrangement*, not of the code. No
amount of care inside the package fixes a package whose two consumers disagree
about what it is.

## Consequences

**One channel, and it is the registry.** Nothing in the design-system repo
reaches this one until it publishes and a PR here bumps the dependency. The
app loses its live-source shortcut; that loss is the point, not a cost to be
mitigated. Do not restore it with a `file:` or `link:` path — the root
`package.json` and `apps/insolvia_app/package.json` both say so at the point of
temptation.

**The feedback loop for a design-system change gets longer.** Editing a
component and seeing it in the app is now: change → PR → publish → bump here.
For iterating on a component, work in that repo, where the component's own
Vitest suite and both typecheck programs run in seconds. This is the real cost
of the decision and it is accepted deliberately: the previous loop was fast
because it skipped the boundary that makes the package a package.

**The token generator splits along ownership.** `tokens.json` and its generator
went with the packages. Its third output — Cognito's managed-login branding
under `infra/modules/auth/` — stayed, because that file is this repo's
infrastructure and a published package must not write into a consumer's tree.
[`tool/reconcile-cognito-branding.ts`](../../tool/reconcile-cognito-branding.ts)
now reconciles it against the **installed** `@insolvia-ai/tokens`. This makes
the gate stronger than before: it fails on a hand-edit, as it always did, and
also when a tokens bump lands without regenerating.

**The tokens package ships `colors.json` for that one consumer.** Node refuses
to strip TypeScript types under `node_modules`, so a plain `node` script here
cannot import the package's `tokens.ts` at all — Metro and Vite can, which is
why only this consumer is affected. Shipping the resolved colours as data was
chosen over re-deriving them here, which would have put a second implementation
of the blend maths in a second repo, free to drift silently.

**Jest needed the one change nothing else did.** The package publishes
untranspiled source; Jest skips transforming `node_modules` by default. That
was a non-issue while the source sat outside `node_modules` as a workspace
member, and a hard failure of every route test afterwards.
`apps/insolvia_app/jest.config.js` exists to derive `transformIgnorePatterns`
from `jest-expo`'s rather than restate it.

**Several CI jobs now need `packages: read` and `NODE_AUTH_TOKEN`.** The root
`npm ci` reaches a registry for the first time. A root `.npmrc` supplies the
scope mapping; the marketing site keeps its own, as it always had.

**One gate changes name on `main`.** `Design system` is gone; `Cognito
branding` takes its place in the `protect-main` ruleset. The design system's own
gates run in its own repo.

## Alternatives considered

**Keep it here and drop the app to the published version.** This removes the
two-truths problem without moving anything, and was genuinely close. Rejected
because it leaves the package's release cadence coupled to a monorepo whose CI,
lockfile and required checks it shares — an unbumped design-system change would
still be able to turn an unrelated PR red, which is the coupling that made
changing the package unpleasant in the first place.

**Move the design system but leave tokens behind, published.** Rejected as the
worse half of a split: the design system's `theme.css` is *generated from*
tokens, so a colour change would need a release in this repo before the design
system could pick it up — two releases for one edit, in the wrong order.

**Vendor `tokens.json` into both repos with a drift check.** Rejected: two
sources of truth held together by a gate is a worse version of one source of
truth, and the gate can only run where both copies are visible.
