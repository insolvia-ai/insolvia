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
`.agents/skills/` contains six EAS skills for those **paid** services — see the
root [`CLAUDE.md`](../../CLAUDE.md) for which of those skills apply. (The
`gluestack-ui-v5` skill, for a library this codebase deliberately does not have,
has been uninstalled.) The four `design-system-*` skills in that same directory
are **ours** and do apply: they are the consumer documentation for the package
this app builds its UI from.

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
│   ├── _layout.tsx         the one navigator (headerShown: false) + SessionProvider
│   ├── index.tsx           /            → <RequireSession><Home /></RequireSession>
│   ├── sign-in.tsx         /sign-in     → <SignIn />   (public)
│   ├── auth/callback.tsx   /auth/callback (path pinned by infra — see below)
│   └── +not-found.tsx      the catch-all; load-bearing, see below
├── screens/                screen bodies the routes render
│   └── home/index.tsx      a screen's private components live beside it
├── components/             APP-SPECIFIC UI — RN primitives, no library
│                           (Button and Field come from the design system)
├── session/                sign-in, tokens, refresh — see below
├── platform/browser.ts     every browser global, each behind a guard
├── config/environment.ts   build-time configuration
└── theme/                  tokens for the active scheme, and the scheme itself
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

- **The shared components come from `@insolvia-ai/design-system`** — `Button`,
  `Field`, `Input`, `Select`, `DateInput` and ~38 more, specifically its
  `.native` leaves, which a `resolveRequest` override in
  [`metro.config.js`](metro.config.js) resolves on **every** platform, web
  included (the long comment there owns the reasoning: this app renders the RN
  dialect via react-native-web and has no Tailwind pipeline, so a `.web` leaf
  would arrive unstyled). Import them from `@insolvia-ai/design-system`, never
  by deep path.

  **Check the `design-system-catalogue` skill before building any UI here.** The
  package is much larger than the handful of components this file once named,
  and the standing risk in this directory is writing an app-local component that
  already exists in the package — which then misses its accessibility work and
  its two-leaf tests. `components/` is for what marketing could never use, not
  for a second copy of something shared. When a component resolves oddly or
  renders unstyled, that is `design-system-platforms`.
- **Everything else in `components/` is app-specific** — the shell chrome and
  branding (`AppShell`, `Heading`, `Wordmark`, `EnvBadge`, `ThemeToggle`,
  `AccountMenu`) that marketing has no use for. It stays here by decision
  ([ADR 0006](../../docs/adr/0006-owned-cross-platform-design-system.md)).

  Two of them are app-local **because the package cannot express them**, not
  by preference. `ThemeToggle` drives an app-level colour-scheme preference the
  package knows nothing about; `AccountMenu` supplies its own trigger because
  `Dropdown.Trigger` wraps children in a `Text` and so cannot hold an `Avatar`.
  Each says so at its definition.

Both exist in this shape because react-native-web maps accessibility props onto
real HTML elements (`propsToAccessibilityComponent.js`). That mapping is the
whole accessibility story, and it only fires if a component asks for it:

| Component    | Source | Primitive + role                            | Emits                          |
| ------------ | ------ | ------------------------------------------- | ------------------------------ |
| `Heading`    | app    | `Text role="heading" aria-level={level}`     | `<h1>`–`<h6>`                  |
| `Button`     | design system | `Pressable accessibilityRole="button"` | `<button type="button">`       |
| `AppShell`   | app    | `View role="banner"/"navigation"/"main"/"contentinfo"` | `<header>/<nav>/<main>/<footer>` |
| `AccountMenu` | app + design system | own `Pressable` trigger around `Avatar`, package `Dropdown` for the menu | `<button aria-haspopup="menu">` + `role="menu"` |
| `Field`      | design system | compound `Field.Root/Label/Description/Error` around a control | labelled input group |
| `Input`      | design system | `TextInput` + the Field's ids, read from context | labelled `<input>`        |
| `Wordmark`, `EnvBadge` | app | `Text` / `View`                   | —                              |

Three rules:

- **`Heading` takes `level` for document structure and a separate `size` for
  appearance.** Never derive the tag from how big the text should look — that is
  what produces `heading-order` failures.
- **Every input is a package control inside a `Field.Root`.** No bare
  `TextInput` in a screen, ever, so an unlabelled input cannot be written by
  accident. The package's own suite asserts the label/control wiring; screen
  tests assert this app's usage.

  **`Field.Control` no longer renders a control** (design system 0.11.0). Put
  `Input` — or `Select`, `DateInput`, `Textarea`, `Combobox` — directly inside
  `Field.Root`; each reads `FieldContext` itself for the id, the
  `aria-describedby` and the invalid flag. `Field.Control` now takes a required
  `render` element and is only for a control the package does *not* own. Note
  `Input` is `value` + **`onValueChange`**, not `onChangeText`, and takes
  `type="email"` rather than a hand-set `keyboardType`/`autoCapitalize` — the
  native leaf derives both from `type`, so spelling them out binds only one leaf.
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

- **No hard-coded colors, radii, or spacing steps.** Everything comes through
  [`src/theme/`](src/theme/index.ts), and only the **semantic** color layer is
  exported — the raw ink/brass/paper palette is unreachable on purpose.

  Colours are now **two layers**: `@insolvia-ai/tokens` underneath, with
  Insolvia's brand over the top. From tokens 0.5.0 the package's base theme is
  deliberately unbranded (monochrome, square, sans headings), so the navy and
  brass arrive as overrides from
  [`brand/colors.json`](../../brand/colors.json) at the repo root, via the
  GENERATED `src/theme/brand-colors.ts` — never edit that file, run
  `npm run tokens`. `themeFor()` does the layering for this app's own
  components; `ThemePreferenceProvider`'s `ThemeProvider` does it for the design
  system's `.native` leaves, and it must pass the brand in *every* arm now —
  passing nothing renders the package's monochrome chrome next to our navy. The
  decision is [ADR 0020](../../docs/adr/0020-the-brand-is-a-consumer-owned-override.md).

  Font FAMILIES layer the same way as of `brand/fonts.json`: Archivo for
  headings, Public Sans for body, IBM Plex Mono for case numbers and form
  references. They reach the screen through two seams and need both —
  `themeFor()` for this app's own components, and `ThemeProvider`'s `fonts` for
  the package's native leaves, whose `StyleSheet.create` runs at module load
  where no context reaches. Import `typography` from `@/theme`, never from
  `@insolvia-ai/tokens`: the latter is the unbranded answer. The faces are
  self-hosted `.woff2` under `public/fonts` with `@font-face` in
  `public/index.html` — naming a family does not load it — and
  `scripts/fetch-brand-fonts.sh` regenerates them.

  Font sizes are the one scale tokens do not carry yet; `theme/theme.ts`'s
  `fontSizes` is their single owner, so a literal `fontSize:` in a component is
  a bug. The two colors in `public/manifest.json` and the `themeColor` in
  `app.config.ts` are the unavoidable exceptions — neither can import anything —
  so check them by hand against `brand/colors.json` when the brand changes.
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
- **`src/session/` owns sign-in, and
  [ADR 0007](../../docs/adr/0007-hosted-ui-pkce-refresh-token-in-local-storage.md)
  owns `src/session/`.** Read it before changing where a token rests. The three
  things most likely to be "fixed" by someone who has not:
  - **Access and ID tokens are in memory only; the refresh token is in
    `localStorage`.** The second half is a deliberate, costed trade-off, not an
    oversight — it buys the pool's 30-day window at a stated XSS risk.
  - **Refresh goes to the hosted domain's `/oauth2/token` as an OAuth
    `refresh_token` grant, never Cognito's SDK refresh flow.** The app client
    permits `ALLOW_USER_SRP_AUTH` only, because Cognito rejects the SDK refresh
    flow outright when refresh-token rotation is on — and it is on, so every
    refresh returns a replacement token that must be persisted.
  - **PKCE is the client's obligation.** Cognito has no "require PKCE" toggle, so
    `pkce.test.ts` and `oauth.test.ts` are the only things asserting we send
    `code_challenge`/S256 at all. Do not delete them as redundant.

  `@/session` is the barrel and the public surface; screens need only
  `useSession()`. The browser globals it used to hold moved to
  `platform/browser.ts` when the colour-scheme preference became a second
  caller — a `localStorage` wrapper is platform plumbing, not session state,
  and reaching into `@/session` for one would be reaching past that barrel. **No new dependency was added for any of this** — PKCE is Web
  Crypto and storage is the platform APIs, each read lazily behind a guard in
  `platform/browser.ts` so a non-web runtime degrades instead of crashing. A
  future **native** client is what would justify `expo-auth-session`; on web it
  would buy nothing.
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
- **`@insolvia-ai/api-client` is now imported** (`src/components/me-panel.tsx`,
  `GET /v1/me`), constructed with the `accessToken` provider seam so a token
  refreshed between calls is picked up without rebuilding the client. It has no
  build step and publishes `src/index.ts` with literal `.ts` specifiers, which is
  why `tsconfig.json` sets `allowImportingTsExtensions` — see the comment there.
  On a 401, `source` decides: `'server'` earns **one** refresh and one retry,
  `'client'` means there is no token and goes to sign-in.
