# ADR 0006 — Theming over a design system

- **Status:** Accepted
- **Date:** 2026-07-30
- **Relates to:** decision D4 in `docs/MVP_PLAN.md` (as revised by D9); revises
  the "two design systems" framing of [ADR 0004](0004-react-native-replaces-flutter.md)

## Decision

**Insolvia themes; it does not run a design system.** The durable shared layer
is *tokens* — one `packages/insolvia_tokens/tokens.json` generated into a
semantic Tailwind `theme.css` for marketing and a typed `tokens.ts` for the
app. Everything above the token layer is **ordinary app code, owned by the app
that renders it**: the marketing site owns its components in
`apps/insolvia_marketing/app/ui/`, the Expo app owns its React Native ones in
`apps/insolvia_app/src/components/`. The two share token *values* only.

There is no shared, versioned component package, and none is reintroduced until
it is **merited** — i.e. a second consumer appears with a real boundary to hold.
A design system is a registry boundary plus a `dist` contract plus a version
gate; those earn their keep against version skew *between separately-deployed
consumers*, and nothing here has that shape today.

## Context

The React design system (`packages/insolvia_design_system_react/`, published as
`@insolvia-ai/design-system` on GitHub Packages) had exactly one consumer — the
marketing site — living in the same repository, built from the same lockfile,
gated by the same CI. For that arrangement it carried the full apparatus of a
published library: a GitHub Packages registry with per-`npm`-read auth, a
PR gate that failed the build unless every package change bumped the version, a
`tsup` `dist` build, a Storybook, and a `ssr.noExternal` trick in the consumer
to keep a registry token out of the runtime image.

All of that machinery exists to manage **version skew** — a consumer installing
an older published artifact than the source it was built against. A
single-consumer, same-repo, same-build package has no skew to hold: the site
always builds against the exact source in the same commit. The registry
boundary was guarding a gap that could not open. Publishing bought a version
number and cost a release step, a separate lockfile, a publish workflow, and a
version-bump gate — the classic tax of a package with no second consumer.

This is the **same fiction the deleted Flutter design system carried**
(`packages/insolvia_design_system`, an annotated-git-tag "publish" consumed by
the app in the same repo — deleted under D9; see
[ADR 0004](0004-react-native-replaces-flutter.md)). D9 removed one half of a
dual design system on the grounds that its second consumer had gone away; the
React half was single-consumer from the day it shipped. Removing it is the same
correction applied to the other target.

Three of the six components (Button, Field, Accordion) sat on Base UI headless
primitives. Base UI bought behaviour we can hand-own at this surface size — the
accordion's open/close logic ported to a few lines of state — so it left with
the package rather than being carried into `app/ui/` for three components.

## Consequences

- **The package is gone.** Its six components (Button, Card, NavBar, Footer,
  Accordion, Field) are now ordinary themed React components in
  `apps/insolvia_marketing/app/ui/`, styled off the generated
  `app/styles/theme.css`. Base UI was removed from the three that used it. The
  marketing site has a Vitest suite (`npm test`) covering the interactive ones
  (Button, Field, Accordion).
- **`theme.css` moved.** The generator now writes it to
  `apps/insolvia_marketing/app/styles/theme.css` instead of into the
  design-system package's `src/styles/`. `@insolvia-ai/tokens` is unchanged and
  remains the single token source feeding both `theme.css` and `tokens.ts`.
- **Nothing publishes to GitHub Packages any more.** The design system was the
  only published package; `insolvia_tokens` and `insolvia_api_client` are
  private workspace members, and the app and marketing are private apps. The
  scope name `@insolvia-ai/design-system` is retired. See
  [`../PACKAGE_PUBLISHING.md`](../PACKAGE_PUBLISHING.md).
- **Storybook is gone**, along with the `insolvia-design-system-pr` skill and
  the `design-system-react-pr.yml` / `design-system-react-publish.yml`
  workflows.
- **The required-check list drops from nine to eight.** `React design system`
  leaves the `protect-main` ruleset; the remaining eight are in
  [`../ARCHITECTURE.md`](../ARCHITECTURE.md). Change the list with
  `scripts/update-ruleset.sh`, never a hard-coded id.
- **Drift is now a discipline problem, not a tooling one** — but it always was.
  There are still two renderings of one design (D4's honest cost), kept in sync
  by owning both, not by a shared component. What changed is that the marketing
  rendering is no longer wrapped in a package boundary that implied a sharing
  it never did.

## When a design system comes back

Reintroduce a versioned component package when a **second consumer with a real
boundary** appears — a separately-built or separately-deployed surface that
would otherwise install a stale copy of shared components. That is the skew a
registry boundary exists to hold, and `PACKAGE_PUBLISHING.md` is the document
that describes standing one back up. Until then, a second app that wants a
component copies or re-implements it against the shared tokens; that is cheaper
than a publish pipeline for as long as there is one real consumer of each set.
