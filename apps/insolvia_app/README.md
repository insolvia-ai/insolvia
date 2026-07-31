# insolvia_app

The Insolvia app — an [Expo](https://expo.dev) / React Native codebase shipped as
a **web single-page app** at `app.insolvia.ai`. Currently a themed hello-world on
the shared design tokens.

## Run

```bash
scripts/dev-setup.sh            # once: installs the npm workspace at the repo root
scripts/dev-up.sh               # expo start --web on http://localhost:3000
```

Port 3000 is not arbitrary: `infra/envs/{dev,staging}` register
`http://localhost:3000` as an exact-match Cognito allowed origin, and Expo's own
default is 8081.

From the repo root, `npm run app` does the same thing and `npm run app:build`
produces the production web export.

## Scripts

| Command             | What it does                                            |
| ------------------- | ------------------------------------------------------- |
| `npm run web`       | dev server with fast refresh on port 3000               |
| `npm run build`     | production web export to `dist/` (`expo export -p web`) |
| `npm run lint`      | ESLint                                                  |
| `npm run typecheck` | `tsc --noEmit`                                          |
| `npm test`          | Jest (`jest-expo` + Testing Library)                    |

These five names are a contract with `.github/workflows/app-*.yml` — renaming one
orphans a required status check on `main`.

## Environment

The target environment is **compiled in**, so a staging bundle is not one
configuration change away from a production one:

```bash
EXPO_PUBLIC_INSOLVIA_ENV=staging npm run build
```

`local` (the default), `staging` and `production` are the three values. Anything
unrecognised — including an unset variable — resolves to `local`, never to
production. Expo inlines only variables prefixed `EXPO_PUBLIC_`, which is why this
one is named the way it is.

`npm run build` passes `--clear`. Metro's transform cache keys on file content and
config but **not** on environment variables, so two exports in the same shell with
different environments can otherwise produce identical output — and ship a staging
bundle as production.

## Platforms

Web is the only target built today. Nothing under `ios/` or `android/` is
committed: `expo prebuild` generates those from `app.config.ts` if and when mobile
starts, which is what keeps them cheap options rather than rewrites. Desktop
(previously unsigned macOS and Windows builds) is deferred — see
[ADR 0004](../../docs/adr/0004-react-native-replaces-flutter.md).

Only Expo's free, open-source layer is used: no EAS Build, Submit, Update or
Hosting, and no Expo account anywhere in CI. The web export is built by GitHub
Actions and served from the existing S3 + CloudFront distribution.

## Layout

`src/app/` is Expo Router — every file there is a URL. Screen bodies live in
`src/screens/`, app-specific components in `src/components/`, tokens-derived
styling helpers in `src/theme.ts`. `public/` is copied verbatim to the export root
(favicon, icons, `manifest.json`). Button and Field come from the shared
[`@insolvia-ai/design-system`](../../packages/insolvia_design_system) — the app
renders the package's React Native leaves on every platform, web included, via a
resolver override explained in [`metro.config.js`](metro.config.js).

There is deliberately no third-party component library and no styling library;
the reasoning, with numbers, is in
[ADR 0004](../../docs/adr/0004-react-native-replaces-flutter.md), the owned
design-system split is in
[ADR 0006](../../docs/adr/0006-owned-cross-platform-design-system.md), and the
folder conventions are in [ADR 0005](../../docs/adr/0005-expo-app-layout.md).
Working on the code? The agent rules for this app are in
[`CLAUDE.md`](CLAUDE.md).
