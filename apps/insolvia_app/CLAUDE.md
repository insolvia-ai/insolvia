# insolvia_app — agent rules

Expo / React Native app, shipped as a **web SPA**. Human docs:
[`README.md`](README.md). Run it with `scripts/dev-up.sh` (or `npm run app` at the
repo root).

Pinned stack, verified as a set: **Expo SDK 57.0.8 · React Native 0.86.0 · React
19.2.3 · expo-router 57.0.8**. Upgrade with `npx expo install --check`, never one
package at a time.

## The two decisions most likely to be re-litigated

**No component library and no styling library.** Not gluestack, not NativeWind,
not Unistyles, not UniWind, not Tailwind. Bare React Native primitives plus
`StyleSheet.create` was the lightest *and* fastest of six configurations measured,
and the only one clearing all seven Lighthouse gates; every layer added made both
script weight and LCP worse. The numbers are in
[ADR 0004](../../docs/adr/0004-react-native-replaces-flutter.md) — read it before
proposing one, because "we should just add NativeWind" is the first thing this
setup invites and it has already been tested and rejected.

The app *does* consume `@insolvia-ai/design-system`, and that is not the thing
this paragraph forbids. What ADR 0004 measured and rejected is a **third-party**
component library and a **styling runtime**; the design system is our own code,
and its `.native` leaves are exactly the pattern above — bare RN primitives plus
`StyleSheet.create` over `@insolvia-ai/tokens`, no new dependency, no styling
runtime in the bundle. (The package's Tailwind class strings live in its `.web`
leaves and shared props modules for the marketing site; the app never builds
that CSS.) The split is
[ADR 0006](../../docs/adr/0006-owned-cross-platform-design-system.md).

**Expo's free tier only.** None of the paid EAS services — no Build, Submit,
Update, Hosting; no EAS config file, no EAS command-line tool, no Expo account or
access token in CI, and no over-the-air updates package. Web builds run in GitHub
Actions; hosting is the existing S3 + CloudFront (`infra/modules/web_hosting`).
A CI guard step greps for those names and fails the build if one appears, which is
why this paragraph describes them instead of spelling them out. Note that
`.agents/skills/` contains six EAS skills for those **paid** services, and
`gluestack-ui-v5` for a library this codebase deliberately does not have — see the
root [`CLAUDE.md`](../../CLAUDE.md) for which of those skills apply.

## Where code goes

The layout is Expo's own, per
[`.agents/skills/expo-project-structure/`](../../.agents/skills/expo-project-structure/SKILL.md).
The reasoning is in
[ADR 0005](../../docs/adr/0005-expo-app-layout.md); read it before proposing a
different shape.

```
public/                     static files copied verbatim to the export root
src/
├── app/                    Expo Router — ROUTES ONLY, nothing else
│   ├── _layout.tsx         the one navigator (headerShown: false)
│   ├── index.tsx           /            → <Home />
│   ├── auth/callback.tsx   /auth/callback (path pinned by infra — see below)
│   └── +not-found.tsx      the catch-all; load-bearing, see below
├── screens/                screen bodies the routes render
│   └── home/index.tsx      a screen's private components live beside it
├── components/             APP-SPECIFIC UI — RN primitives, no library
│                           (Button and Field come from the design system)
├── config/environment.ts   build-time configuration
└── theme.ts                StyleSheet helpers over @insolvia-ai/tokens
```

Rules that follow from it:

- **`src/app` is routes-only.** Every file there becomes a URL, so a helper, a
  type, or a test file dropped in it becomes a route. Screen bodies go in
  `screens/`, shared UI in `components/`.
- **Kebab-case filenames** (`env-badge.tsx`), matching Expo's own template.
- **`StyleSheet.create` at the bottom of the component file**, never a separate
  `.styles.ts`. Colors are applied from `useTheme()` at render time because a
  `StyleSheet` block runs once at module load and cannot read the color scheme;
  spacing, radii and type sizes are scheme-independent and so are read statically
  from `@/theme`.
- **Tests are colocated** — `heading.tsx` is tested by `heading.test.tsx` beside
  it, not in a mirrored `__tests__/` tree.
- **`@/*` is aliased to `./src/*`.** Prefer it over relative imports.
- **Create folders when the second file arrives, not before.** A folder holding one
  file carries no information.
- A screen used by two routes stays in `screens/`; UI used by two screens moves to
  `components/`.

## Accessibility is this directory's main job

UI comes from two places, both rendering bare RN primitives:

- **`Button` and `Field` come from `@insolvia-ai/design-system`** — specifically
  its `.native` leaves, which a `resolveRequest` override in
  [`metro.config.js`](metro.config.js) resolves on **every** platform, web
  included (the long comment there owns the reasoning: this app renders the RN
  dialect via react-native-web and has no Tailwind pipeline, so a `.web` leaf
  would arrive unstyled). Import them from `@insolvia-ai/design-system`, never
  by deep path.
- **Everything else in `components/` is app-specific** — the shell chrome and
  branding (`AppShell`, `Heading`, `Wordmark`, `EnvBadge`) that marketing has no
  use for. It stays here by decision
  ([ADR 0006](../../docs/adr/0006-owned-cross-platform-design-system.md)).

Both exist in this shape because react-native-web maps accessibility props onto
real HTML elements (`propsToAccessibilityComponent.js`). That mapping is the
whole accessibility story, and it only fires if a component asks for it:

| Component    | Source | Primitive + role                            | Emits                          |
| ------------ | ------ | ------------------------------------------- | ------------------------------ |
| `Heading`    | app    | `Text role="heading" aria-level={level}`     | `<h1>`–`<h6>`                  |
| `Button`     | design system | `Pressable accessibilityRole="button"` | `<button type="button">`       |
| `AppShell`   | app    | `View role="banner"/"navigation"/"main"/"contentinfo"` | `<header>/<nav>/<main>/<footer>` |
| `Field`      | design system | compound `Field.Root/Label/Control/Description/Error` | labelled input group |
| `Wordmark`, `EnvBadge` | app | `Text` / `View`                   | —                              |

Three rules:

- **`Heading` takes `level` for document structure and a separate `size` for
  appearance.** Never derive the tag from how big the text should look — that is
  what produces `heading-order` failures.
- **The design system's `Field` is the only way to render an input.** No bare
  `TextInput` in a screen, ever, so an unlabelled input cannot be written by
  accident. The package's own suite asserts the label/control wiring; screen
  tests assert this app's usage.
- **No `role="region"`.** A `<section>` without an accessible name is invalid ARIA
  and axe flags it. Open a block with a heading instead.

Two usage rules the package's API makes easy to drop, both asserted in
`src/screens/home/index.test.tsx`:

- **Decorative glyphs never enter the accessible name.** The package button is
  children-based with no `icon` prop, so a trailing glyph is an
  `aria-hidden` `<Text>` child *and* `aria-label` pins the name to the visible
  label (WCAG 2.5.3) — see the home screen's CTA.
- **Buttons are `size="lg"`.** The package's `md` is 40dp, under the 44dp
  WCAG 2.5.5 target-size floor this app enforces.

## `public/index.html` — the SPA shell, and its one sharp edge

`public/index.html` is Expo's own template (`@expo/cli/static/template/index.html`)
plus a `<link rel="manifest">` and an Apple touch icon. `expo export -p web` reads
it when it exists, substitutes `%LANG_ISO_CODE%` and `%WEB_TITLE%` from
`app.config.ts`, appends the script/stylesheet tags and the description and
theme-color `<meta>`s, and writes the result over the verbatim copy that the
`public/` copy step puts in `dist/` first.

**Every one of those substitutions replaces the FIRST occurrence in the file, and
the meta tags are injected before the first `</head>`.** So nothing above the real
`<head>` may mention a placeholder name or contain a head-closing tag — including
a comment. Written the natural way (a header comment explaining the file), the
export shipped `<html lang="%LANG_ISO_CODE%">`, a literal `%WEB_TITLE%` title, and
`<meta name="description">` *inside the comment*. That is why the commentary sits
below the `<title>` and why this paragraph is here rather than in the file.

## Everything else

- **No hard-coded colors, radii, or spacing steps.** Everything comes from
  `@insolvia-ai/tokens` via [`src/theme.ts`](src/theme.ts), and only the
  **semantic** color layer is exported — the raw ink/brass/paper palette is
  unreachable on purpose. Font sizes are the one scale tokens do not carry yet;
  `theme.ts`'s `fontSizes` is their single owner, so a literal `fontSize:` in a
  component is a bug. The two colors in `public/manifest.json` are the unavoidable
  exception — JSON cannot import tokens — so update them by hand if the brand
  changes.
- **Environment** comes from `EXPO_PUBLIC_INSOLVIA_ENV` (`local` default), read in
  [`src/config/environment.ts`](src/config/environment.ts). Expo inlines **only**
  `EXPO_PUBLIC_*` variables — an unprefixed name reads as `undefined` at runtime.
  Unknown or absent resolves to `local` and never to production, and the
  host/API maps are exhaustive, so a new environment cannot compile until it
  declares its API (issue #64). Keep both properties.
- **No `enum`.** `erasableSyntaxOnly` is on: Metro strips types rather than
  compiling them, so `enum`, `namespace` and parameter properties would typecheck
  and then fail at runtime. Use `as const` + a union, as `environment.ts` does.
- **`src/app/+not-found.tsx` is not optional.** CloudFront rewrites 403/404 to
  `/index.html` with HTTP **200**, so nothing at the edge ever 404s and this route
  is the only thing that can tell a user the page does not exist.
- **`/auth/callback` must stay in step with `infra/modules/auth/main.tf`**, whose
  web client registers `<origin>/auth/callback` as its only OAuth callback URL.
  Under file-based routing that path *is* the file's location, so moving the file
  breaks sign-in's return leg. `auth-callback.test.tsx` guards it.
- **The dev server is pinned to port 3000.** Expo defaults to 8081, and
  `infra/envs/{dev,staging}` register `http://localhost:3000` as an
  **exact-match** Cognito allowed origin.
- **The Metro cache does not key on environment variables**, only on file content
  and config. Two exports in one shell with different `EXPO_PUBLIC_INSOLVIA_ENV`
  can therefore produce byte-identical output and ship a staging bundle as
  production. `npm run build` passes `--clear` for that reason; do not remove it.
- **`ios/` and `android/` are not committed.** `expo prebuild` generates them from
  `app.config.ts` if and when mobile starts; a returning native client uses the
  `insolvia://` scheme.
- **Script names are a contract** with `.github/workflows/app-*.yml`: `build`,
  `web`, `lint`, `typecheck`, `test`. Renaming one orphans a required check on
  `main` (see the `insolvia-branch-protection` skill).
- `@insolvia-ai/api-client` is a dependency that nothing imports yet — wired in
  beside `apiBaseUrl` so the first API-backed feature only has to import it.
