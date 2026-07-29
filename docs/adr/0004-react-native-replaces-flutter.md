# ADR 0004 — React Native on Expo replaces Flutter, on bare primitives

- **Status:** Accepted
- **Date:** 2026-07-29
- **Relates to:** decision D9 in `docs/MVP_PLAN.md` (which supersedes D8);
  supersedes [ADR 0002](0002-desktop-auto-update-deferred.md); superseded
  layout rules in [ADR 0003](0003-flutter-app-layout.md) → [0005](0005-expo-app-layout.md)

## Decision

**`apps/insolvia_app` is a React Native app on Expo. Flutter and Dart leave the
repository entirely — no Dart source, no pub workspace, no Melos, no Flutter
toolchain in CI or on a dev machine.**

Five things are settled together, because each one is only defensible given the
others:

| # | Decision |
|---|---|
| 1 | **Expo SDK 57**, pinned exact, in place of the pinned `flutter-version: "3.44.6"`. |
| 2 | **Expo's free tier only** — no EAS Build, Submit, Update or Hosting, and no Expo account in CI. Machine-enforced by a guard step in `app-pr.yml`. |
| 3 | **No component library.** Bare React Native primitives styled with `StyleSheet.create` plus a generated typed token module, and our own small design system on top. No Tailwind in the app. |
| 4 | **Desktop is deferred, not built.** No macOS or Windows targets, no desktop CI jobs, no artifact hosting, no desktop Cognito client. |
| 5 | **Mobile is latent.** Nothing under `ios/` or `android/` is committed; `expo prebuild` generates both from `app.config.ts` when we want them. |

Bundlers are **Metro** for the app and **Vite** for marketing. Routing is
**Expo Router** with `web.output: "single"`, so the CloudFront SPA rewrite that
already exists keeps working unchanged; `app/+not-found.tsx` is the direct port
of go_router's `errorBuilder`. The marketing site does **not** move — see
*Marketing stays put* below.

## Context

D3 already conceded half of this: Flutter web cannot be server-rendered, so the
marketing site was built in React. That left the repo running two UI stacks,
two package managers, two design systems and two toolchains for what is one
product. The question this ADR answers is which single stack survives, and the
answer follows from where the users actually are — `app.insolvia.ai` is the
promoted surface, and web is the target Flutter serves worst.

React Native on Expo gets us one stack with the marketing site, one package
manager, one language across app, tokens, API client and generator, and keeps a
native path open through `prebuild` rather than through a CI matrix.

### Expo free tier only

Everything Insolvia deploys already runs on our own AWS: CloudFront + S3 for the
app, a Lambda behind CloudFront for marketing, and Terraform describing all of
it. EAS would add a second, paid, differently-shaped deploy path for the same
artifact — `eas deploy` targets Cloudflare Workers, which is precisely what
`infra/modules/web_hosting` already does for free and under Terraform.

The parts of Expo we depend on — the SDK, Metro, Expo Router, `expo export`,
`expo prebuild` — are open source and run locally. The paid parts are a
service, and adopting one would put a vendor account on the critical path of a
deploy that is otherwise entirely ours.

This is easy to erode by accident: several vendor-supplied agent skills in
`.agents/skills/` describe EAS as the normal way to ship an Expo app, and one
carries `allowed-tools: Bash(eas *)`. So the constraint is enforced in CI
rather than merely written down — the guard in `app-pr.yml` fails the build on
an `eas.json`, an `eas-cli` dependency, or a `.eas/` directory. Root
`CLAUDE.md` carries the applicability table for those skills.

### Desktop deferred

D8 kept both desktop targets green in CI, and that was the right call *under
Flutter*: one toolchain built macOS, Windows and web from the same source, so
optionality cost a CI job. React Native does not have those economics. Desktop
means `react-native-macos` / `react-native-windows` — separate forks on their
own release cadence, not a `--platform` flag.

But the optionality D8 was buying does not disappear, it moves: under Expo the
cheap held-open target is **mobile**, and it is held open by `prebuild`, which
needs nothing committed and no CI job. So we keep the option by having chosen
React Native, not by running desktop builds. D9 in `MVP_PLAN.md` records this
in full, and this ADR supersedes ADR 0002, whose subject — a desktop
auto-updater — no longer has a desktop to update.

## The spike record

Six configurations were measured before the styling decision was made. Same
page, same static export, same three-run median throttled-mobile Lighthouse
run, same seven-gate axe + Core Web Vitals assertion. This table is the reason
the decision is what it is, and it is the part of this document most worth
keeping.

| Configuration | JS gzip | LCP | Gates |
|---|---|---|---|
| **bare primitives + `StyleSheet`, no library** | **292,912 B** | **2,226 ms** | **7/7** |
| NativeWind v4.2.6 (real extracted CSS, Tailwind v3) | 296,894 B | 2,378 ms | 7/7, thinner |
| Unistyles v3 (CSSOM `insertRule` at runtime) | 312,596 B | 2,382 ms | 6/7 — **CLS 0.927** |
| UniWind | 359,942 B | — | — |
| Expo's own NativeWind v5 recipe (nightly) | 393,172 B | 2,845 ms | 5/7 |
| gluestack + UniWind | 436,089 B | 3,023 ms | 4/7 |
| *marketing today* | *~125,000 B* | *1,960 ms* | *7/7* |

The heaviest configuration decomposes cleanly: **293 KB** is the
react-native-web + Expo Router floor we pay regardless, **+ 67 KB** UniWind,
**+ 76 KB** gluestack. Every byte above the floor is a library, and each one
bought less than it cost.

**gluestack contributed nothing to semantic HTML.** This was the specific thing
a component library was supposed to give us, and it was already there without
one. A bare `Text role="heading" aria-level={1}` renders a real `<h1>`; a bare
`Pressable role="button"` renders a real `<button>`. That is react-native-web's
own `role`→tag mapping. The library was calling the same mechanism, 76 KB more
expensively.

**Build-time CSS extraction does not help; it hurts.** This was the leading
hypothesis going in — that the atomic-styles runtime was the weight, and moving
styles into a real stylesheet at build time would shed it. It is wrong. The
bundle still ships the class-name→atomic-style interop runtime wherever the CSS
ends up living, so extraction *adds* a stylesheet plus the code to consume it
and removes nothing. Recorded here as a tested-and-failed prediction rather
than an assumption, because it is the one an agent is most likely to re-derive
and act on.

**NativeWind v5 through gluestack's CLI crashes on Expo web.** A circular
import between `react-native-css@3.0.7` and `react-native-web@0.21.2`,
reproduced across three rounds — including one in full conformance with the
vendor's own documented setup, `lightningcss` pinned to `1.30.1`. The crash
belongs to the `react-native-css@3.x` line specifically, **not** to NativeWind
v5 as such: Expo's own recipe renders cleanly. It is simply the heaviest
working build in the table.

**Two of the accessibility failures were library defects, and they disappear
when we own the code.**

- gluestack's `Heading` derives the HTML tag from its `size` prop, so a purely
  visual choice silently changes document structure. That produced
  `heading-order` failures on two of the three spike routes. Our `Heading`
  takes an explicit `level`.
- Its `FormControlLabel` renders a `<div>` with no `for` and no
  `aria-labelledby`, so the label is not programmatically associated with its
  input. Our `Field` is the only sanctioned way to render an input, and it owns
  that association.

### Marketing stays put — and why that is a product call

Bare primitives *do* pass all seven gates, so moving `apps/insolvia_marketing`
onto the same stack is technically viable. It is still a downgrade: 2.3× the
script weight (125 KB → 293 KB) and an 11% LCP margin, on the one property
whose entire job is SEO and conversion. D3 exists because Core Web Vitals on
that page are the whole game.

So marketing stays React + Vite, and the repo keeps **two** design systems
sharing token *values* only — one `packages/insolvia_tokens/tokens.json`, one
TypeScript generator, emitting marketing's `theme.css` **and** a typed
`tokens.ts`, both published from that package as `@insolvia-ai/tokens`. Anyone
revisiting this should read the position as *"viable but worse"*, not
*"impossible"*: it is a judgement about which page's numbers matter most, and a
future where the app and the site converge is not ruled out on technical
grounds.

## Consequences

- **One language.** App, marketing, tokens, generator and API client are all
  TypeScript. `melos bootstrap`, `flutter pub get` and the pub workspace are
  gone; the npm workspace in the root `package.json` is the only one left.
- **The CI matrix collapses.** `Flutter app`, `macOS build`, `Windows build`
  and `Flutter design system` are replaced by a single `App` check, taking the
  required-check list from twelve to nine. See `docs/ARCHITECTURE.md`.
- **The environment variable is renamed.** `--dart-define=INSOLVIA_ENV` becomes
  **`EXPO_PUBLIC_INSOLVIA_ENV`**. This is forced, not cosmetic: Expo inlines
  only variables prefixed `EXPO_PUBLIC_`, and anything else is simply absent
  from the bundle at runtime.
- **We now own accessibility for complex widgets.** `Modal`, `Select`,
  `Combobox` and date pickers have no accessible implementation in bare
  react-native-web, and a petition tool needs all of them. This is the real
  cost of decision 3 and it is unavoidable rather than chosen. Mitigations:
  adopt `@react-native-aria/*` headless primitives when they land, and keep the
  axe assertion in `app-pr.yml` from day one so a regression is a red check
  rather than a discovery.
- **No library also means no free breadth.** At four components that is cheap.
  At forty it is a real cost, and the honest revisit trigger is component
  count, not taste.
- **Reversing the desktop deferral is a rebuild, not a flag.** Under D8 it was
  certificate procurement plus a CI job. It is now a port to
  `react-native-macos` / `react-native-windows`. D9 records the trade that was
  accepted in exchange.

## Alternatives considered

**Keep Flutter for the app, keep React for marketing.** The status quo. It was
sustainable and it was what the repo already had; it is rejected because every
shared concern — tokens, the API client, the design system, the CI shape — had
to be built twice, and the second copy was always the one that rotted.

**Adopt a component library anyway.** Rejected on the table above: the cheapest
library in the spike was 4 KB heavier and 152 ms slower than no library at all,
and the most featureful was 143 KB heavier, 797 ms slower, and failed three
gates *because of* the library. A library is worth revisiting when the
component count makes breadth matter — not to obtain semantic HTML, which we
already have.

**Move marketing onto React Native for Web too.** One stack everywhere is a
genuinely attractive property. Rejected on the numbers above, as a product
call, not a technical one.
