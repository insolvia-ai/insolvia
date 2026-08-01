import type { ExpoConfig } from 'expo/config';

/**
 * Expo configuration for the Insolvia app.
 *
 * The app ships as a **web SPA** and nothing else today. `ios/` and `android/`
 * are deliberately not committed — `expo prebuild` generates them if and when
 * mobile starts (docs/adr/0004-react-native-replaces-flutter.md).
 *
 * Deliberately absent: the EAS config file, and anything else in Expo's paid
 * tier. Web builds run in GitHub Actions and hosting is the existing S3 +
 * CloudFront. (A CI step greps for those names, so this comment describes them
 * rather than spelling them out.)
 */
const config: ExpoConfig = {
  name: 'Insolvia',
  slug: 'insolvia-app',
  version: '0.1.0',

  // Deep-link scheme, registered now so `insolvia://auth/callback` is already
  // the app's identity when mobile arrives.
  scheme: 'insolvia',

  orientation: 'portrait',

  // Follows the OS appearance — on web that is the browser's
  // `prefers-color-scheme`.
  userInterfaceStyle: 'automatic',

  icon: './public/icons/Icon-512.png',

  web: {
    bundler: 'metro',

    // These three land in the shipped `index.html`: `lang` on <html>, and the
    // description / theme-color <meta> tags. The shell itself is
    // `public/index.html` — Expo's template plus a manifest link — and the title
    // comes from `name` above.
    lang: 'en',
    description: 'Bankruptcy case preparation and e-filing.',
    themeColor: '#0B2A4A',

    // A single-page app: one index.html plus content-hashed assets under
    // _expo/static/{js,css}. That is exactly the shape infra/modules/web_hosting
    // serves, and it is why src/app/+not-found.tsx is mandatory — CloudFront
    // rewrites 403/404 to /index.html with HTTP 200, so the router, not the
    // server, is the only thing that can say "not found".
    output: 'single',

    favicon: './public/favicon.png',
  },

  plugins: ['expo-router'],

  experiments: {
    // Metro resolves the `@/*` alias from tsconfig.json. Stated explicitly
    // rather than relying on the default, because the alias is used by every
    // file in src/ and a silent default flip would break the whole app at once.
    // (Jest does not read tsconfig paths at all — see the `moduleNameMapper` in
    // package.json.)
    tsconfigPaths: true,

    // Generates `.expo/types/router.d.ts`, so `href` and `router.replace()` take
    // only routes that exist. Generated, gitignored, and not required to
    // typecheck: without it `Href` is simply `string`.
    typedRoutes: true,
  },
};

export default config;
