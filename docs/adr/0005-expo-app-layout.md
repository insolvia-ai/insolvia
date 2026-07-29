# ADR 0005 — The Expo app follows Expo's own project structure, not a repo-local shape

- **Status:** Accepted
- **Date:** 2026-07-29
- **Relates to:** `apps/insolvia_app/CLAUDE.md`; [ADR 0004](0004-react-native-replaces-flutter.md)
  is the stack decision this implements. **Supersedes**
  [ADR 0003](0003-flutter-app-layout.md), which made the identical call for
  Flutter.

## Decision

**`apps/insolvia_app` is laid out the way Expo lays out an Expo Router app:
`src/app/` holds routes and nothing else, screen bodies live in `src/screens/`,
reusable UI in `src/components/`, and filenames are kebab-case.** The reference
is Expo's published
[app folder structure guidance](https://expo.dev/blog/expo-app-folder-structure-best-practices),
vendored into this repo as the read-only `.agents/skills/expo-project-structure`
skill.

```
apps/insolvia_app/
├── app.config.ts · metro.config.js · package.json
├── public/ · scripts/
└── src/
    ├── app/                    Expo Router routes ONLY — every file is a route
    ├── screens/<screen>/       screen bodies, private sub-components colocated
    ├── components/             our design system — RN primitives, no library
    ├── config/                 environment.ts
    └── theme.ts                (hooks/ · utils/ arrive with their first file)
```

Styles are a `StyleSheet.create({ … })` block at the bottom of the file that
uses them, never a separate `.styles` file. Tests sit beside the file they test
(`format-date.test.ts` next to `format-date.ts`), not in a `__tests__/` folder.
Platform variants are separate files (`bar-chart.web.tsx`) with identical
props, not inline `Platform.OS` branches, once the difference outgrows a
`Platform.select`.

This is the same principle ADR 0003 settled for Flutter — **take the layout the
framework publishes and maintains a reference sample for, rather than inventing
one** — applied to the framework that replaced it. The repo has one rule about
app layout, not two.

## Context

0003's reasoning survives the stack change almost verbatim, which is itself the
argument for repeating it. The failure it prevented was a repo-local convention
copied by resemblance: `lib/src/` was a *package privacy* mechanism that means
something real in a published package and nothing at all in an application, and
`presentation/` only carries information next to a `data/` and a `domain/` that
did not exist. Both cost a directory level on every path and taught future
readers a rule the framework does not have.

An Expo app has an exactly analogous trap, and it is the one worth naming here:

**`src/app/` is not a source folder.** Every file under it becomes a route.
Dropping a component, a helper or a test in there does not merely misfile it —
it publishes a URL. That is why `screens/` exists at all: a screen big enough to
need breaking apart has nowhere to put its pieces inside `app/`, so route files
stay thin and render a screen body from a sibling tree.

```tsx
// src/app/index.tsx — route-level concerns only
import { Home } from "@/screens/home";

export default function HomeScreen() {
  return <Home />;
}
```

The `src/` prefix itself is Expo's default (`@/*` aliases `./src/*` in
`tsconfig.json`) and Expo Router resolves `src/app/` natively. It separates app
code from the config files that must sit at the package root — `app.config.ts`,
`metro.config.js`, `package.json`, `public/`.

### Two deliberate departures from the published layout

Expo's skeleton includes two things this repo will not have, and both follow
from decisions made in ADR 0004 rather than from taste:

- **No `eas.json`.** We are on Expo's free tier — no EAS Build, Submit, Update
  or Hosting, and no Expo account in CI. The file's presence is what the
  free-tier guard in `app-pr.yml` fails on, so this departure is enforced, not
  merely documented.
- **No `src/server/` and no `src/app/api/` routes.** Insolvia's API is Python
  on Lambda in `services/api/`, behind the trust boundary
  [ADR 0001](0001-client-stays-dumb-trust-boundary.md) draws. Expo Router API
  routes would be a second server, in a second language, and shipping them
  means EAS Hosting — which we do not use. `+api.ts` files therefore never
  appear in this app; a capability the client needs is an endpoint on
  `services/api`, exactly as ADR 0001 requires.

Both are worth writing down because the vendored skill describes them as
ordinary parts of the layout, and an agent following it faithfully would add
them.

## Consequences

- Contributors and agents meet the layout Expo itself publishes, and the
  `.agents/skills/expo-project-structure` skill can be followed directly —
  with the two exceptions above, which root `CLAUDE.md` also flags in its
  `.agents/` applicability table.
- **The skill is for new projects and says so.** It explicitly warns against
  restructuring an existing app to match it. We are inside the one window where
  it applies: the app is being scaffolded from nothing in this PR. After this,
  the layout in `apps/insolvia_app/CLAUDE.md` is the rule and the skill is
  background.
- Empty scaffolding is not created ahead of need, per 0003: `hooks/` and
  `utils/` appear with the first hook and the first helper.
- `src/config/environment.ts` is the direct port of the Dart
  `environment.dart`, reading `EXPO_PUBLIC_INSOLVIA_ENV` (ADR 0004 explains why
  the variable had to be renamed).
- Filenames are kebab-case throughout, matching `create-expo-app`. This differs
  from the Dart snake_case that preceded it and from the PascalCase common in
  React codebases; picking the template's convention keeps generated files and
  hand-written ones indistinguishable.

## Alternatives considered

**Feature-first slices — `src/features/<feature>/{components,screens,data}`.**
Rejected for the same reason 0003 rejected them, and the reason has not moved:
our data is genuinely cross-feature. A case, a debtor, a document and a MyCase
sync record are each read by intake *and* the means test *and* the forms
engine, so under strict slices they accumulate in a `shared/` folder — the
documented failure mode of the pattern. Screens belong to one feature;
repositories do not.

**Keep the Flutter shape — `ui/<feature>/`, `domain/models/`,
`data/repositories/`.** Tempting as continuity, and it is a fine shape. Rejected
because it would be a repo-local invention *in this framework*: no Expo
reference sample uses it, `app/` has route semantics that `ui/` never had, and
an agent would have to be told the mapping rather than reading it off the
template. The data-by-type half returns naturally when the first repository
lands — as `src/data/`, alongside these folders, exactly as 0003 sequenced it.
