# ADR 0006 — One owned, platform-split design system serves both surfaces

- **Status:** Accepted
- **Date:** 2026-07-31
- **Relates to:** revises the *Marketing stays put* section of
  [ADR 0004](0004-react-native-replaces-flutter.md) (that section only — the
  stack decision, the free-tier rule and the no-component-library measurements
  all stand); second revision of decision D4 in `docs/plan.md`;
  `docs/reference/package-publishing.md` owns the operational rules. *(A draft numbered
  0006, "theming over design system", existed only on the abandoned
  `claude/marketing-theming-05` branch and never merged — a superseded
  **direction**, not a superseded ADR. This is the first ADR 0006.)*

## Decision

**`packages/insolvia_design_system` — published as `@insolvia-ai/design-system`
(0.2.x) — is the one design system, and both surfaces consume it.** The
marketing site installs the published version (cut over in PR #66, live on
staging); the app consumes the source through the npm workspace symlink and
renders the `.native` leaves on every platform, web included (the app-adoption
PR this ADR lands with).

Each component is **three files**, split by what varies:

| File | Contains | Picked by |
|---|---|---|
| `<name>.props.ts` | the shared contract — types, variant data, platform-agnostic state; imports **no renderer** (ESLint-enforced) | both leaves import it |
| `<name>.web.tsx` | plain DOM + Tailwind classes over the generated `theme.css` | marketing's Vite |
| `<name>.native.tsx` | React Native primitives over `@insolvia-ai/tokens` | the app's Metro |

The per-component index re-exports the extensionless `./<name>` and the
**consumer's bundler picks the leaf** — the package itself never decides:

- **Marketing (Vite):** web-first `resolve.extensions` picks `.web.tsx`, and
  `ssr.noExternal` bundles the package's TypeScript source into the SSR build.
- **The app (Metro):** a `resolveRequest` override in `metro.config.js`, scoped
  to imports *originating inside the package*, prefers the `.native` leaf on
  **all** platforms — web included. react-native-web renders it in the browser
  exactly like the app's own components, and **no Tailwind enters the app**,
  preserving ADR 0004's decision 3. Package `exports` conditions cannot express
  this: conditions select entry points, not the platform suffix of a package's
  internal relative imports.

The package therefore **publishes source** — `files: ["src"]`, an `exports` map
pointing at `.ts`, deliberately no build step. Leaf selection lives in the
consumer's bundler, so the `.web`/`.native` pairs must survive into the
published artifact verbatim; a package-side build (tsup, `tsc --emit`) would
collapse each pair into one compiled entry and break the pattern.

**What this revises in ADR 0004:** the *Marketing stays put* section's ending —
"the repo keeps **two** design systems sharing token *values* only." Marketing
still stays put (its stack, hosting and Core Web Vitals argument are
untouched); what changed is that the two renderings of one design now live in
one package as sibling leaves instead of in two packages kept in sync by
discipline.

## Context

ADR 0004 left the repo with two design systems over one `tokens.json`: a
web-only React package for marketing and the app's own unpublished React
Native components. D4 in `plan.md` called the cost honestly — *"two
implementations of one design, kept in sync by discipline. Visual drift between
parallel component libraries is the classic failure mode of dual-platform
design systems"* — and contained it by scope caps rather than by mechanism.

The question this ADR answers: can one implementation serve both surfaces
**without** paying the react-native-web floor on marketing? ADR 0004 measured
that floor at 293 KB gzip against marketing's ~125 KB — 2.3× the script weight
on the one page whose entire job is SEO — which is why marketing could never
simply render the app's components. The answer is the platform split above:
one package, one contract per component, and the web leaf never touches React
Native at all.

## The measurements

Same methodology as ADR 0004's spike table: static export, throttled-mobile
Lighthouse, the marketing CI gates. Spike measured 2026-07-31; the cutover
numbers are from live CI.

### Platform-split package vs. inline web components (control)

| | control (inline web components) | platform-split package |
|---|---|---|
| `/` script (gzip) | 117.5 KB | 117.4 KB |
| `/` LCP | 1,957 ms | 1,957 ms |
| `/waitlist` script (gzip) | 115.4 KB | 116.0 KB |
| CI gates | all pass | all pass |

The package costs marketing nothing measurable — the `.web` leaves compile to
the same DOM + Tailwind the inline components were.

Two verified facts ride along:

- **react-native-web is absent from marketing's bundle, falsifiably.** The
  `.native` leaves *do* contain the string `react-native`, so a grep over
  `build/client` is a real test, not a tautology — and none of it reaches the
  web build. That grep is now a standing guard step in `marketing-pr.yml`, so
  the property is re-proven on every PR rather than asserted here once.
- **Post-cutover, marketing staging's Lighthouse accessibility score went
  0.96 → 1.0.** The cutover surfaced a pre-existing text-accent contrast
  failure (3.06:1) and fixed it (text-primary: 13.8:1 light, 7.8:1 dark).

### The Tamagui reference — measured, validated the class, not adopted

Before authoring the split by hand, the obvious alternative — a cross-platform
styling runtime with a compiler — was measured on the same rig (Tamagui,
RR7 + Vite SSR, compiler on): **139 KB** page script, ~30 KB of it Tamagui
runtime, no react-native-web, all gates pass. Viable. But core-only
`styled(View)` emits `<div tag="button">`, not a real `<button>` — accessible
web semantics without react-native-web require an explicit web-specific path
*anyway*. That finding is what settled it: the fork Tamagui's escape hatch
forces is the same fork this ADR authors deliberately — except owned, zero
dependencies, and lighter (~117 KB shipped vs ~155 KB projected).

### The react-native-web floor, restated precisely

ADR 0004's 293 KB floor applies to **marketing's SEO-gated page**, not to the
auth-walled app. The app pays it willingly on every platform — that is what
being an RN app on web means — so rendering the `.native` leaves through
react-native-web costs the app nothing new, while marketing's `.web` leaves
keep it at zero. One package, two cost models, each surface on the right side
of its own line.

## The litmus test for new components

Where a new component's implementation goes, decided up front:

- **Describable as pure data** — a tag plus style values, no behavior → a
  candidate for a future one-file helper (see *Consequences* for why that
  helper does not exist yet).
- **Needs events, runtime state, or accessibility wiring** → a leaf pair from
  day one. The platforms' event and accessibility models do not unify: a web
  `<label for>` and an RN `accessibilityLabelledBy` are different contracts,
  not different spellings of one contract.

## Consequences

- **Two rendering leaves per behavioral component — the honest cost.** Field,
  the heaviest component, shares ~55 lines of contract against ~120 lines per
  leaf; the accessibility wiring is irreducibly per-platform (the litmus test
  above is the reason). This is D4's dual-implementation cost surviving in a
  smaller, better-fenced form: the two renderings now sit in one directory, in
  one PR, reviewed side by side against one props module — drift is a diff,
  not a discipline.
- **The `styled()` one-file helper was deliberately not adopted** for
  presentational components. Today's pure-presentational count doesn't justify
  the abstraction. Revisit trigger: when that count makes the leaf duplication
  annoying — component count, not taste, same as ADR 0004's library trigger.
- **Two consumers, two channels — unchanged and load-bearing.** The app is a
  workspace member and tracks source (no install, no version pin, no consume
  PRs); marketing installs the published version and takes a consume PR per
  bump. Any change to the package is its own PR with a version bump
  (machine-enforced; `insolvia-design-system-pr` skill).
- **Phantom peers must never return.** GitHub Packages strips
  `peerDependenciesMeta` from registry metadata, so an "optional" peer is
  treated as required and a web consumer's install 404s on the unpublished
  tokens package — the 0.2.1 lesson. `react-native` and `@insolvia-ai/tokens`
  are deliberately **not declared** as peers; the native leaves' imports
  resolve from the consumer's own dependencies. The package's `package.json`
  comment block is the enforcement-adjacent documentation.
- **ADR 0004's decision 3 stands whole.** No component library, no Tailwind in
  the app — the app renders `.native` leaves styled with `StyleSheet` over
  tokens, exactly as its own components are. What 0004 called "our own small
  design system on top" now lives one directory over, published, serving both
  surfaces.
- **`apps/insolvia_app/src/components/` still exists, smaller.** Shared
  behavioral components (Button, Field) come from the package; app-local
  chrome (app shell, wordmark, environment badge, heading) stays in the app,
  unpublished and unversioned, per ADR 0005's layout.

## Alternatives considered

**Keep two design systems sharing token values only** — the status quo this
ADR revises. Rejected for the reason D4 already named: two implementations
kept in sync by discipline, and the second copy is always the one that rots.
The scope caps contained the risk; they never removed it.

**Adopt a cross-platform styling runtime (Tamagui).** Measured, above. Rejected
not on weight — 139 KB passed the gates — but because its core-only web output
is not semantic HTML, so the web-specific fork must be written either way.
Writing it inside an owned package beats writing it inside someone else's
escape hatch, and drops a large dependency from the critical path of both
surfaces.

**Move marketing onto react-native-web.** Re-rejected. ADR 0004's numbers
still govern: 2.3× the script weight on the SEO-gated page. The platform split
exists precisely so this never has to happen.

**Give the package a build step.** Rejected because compiled output collapses
the `.web`/`.native` pairs — leaf selection is the consumer's bundler's job,
so the pairs must reach `node_modules` verbatim. The cost is that every
consumer transpiles TypeScript out of `node_modules`: Metro does natively,
marketing via `ssr.noExternal`. `docs/reference/package-publishing.md` owns the details.
