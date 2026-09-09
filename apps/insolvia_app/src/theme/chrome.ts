import { brandColors, brandFonts, brandRadii } from './brand-colors';

/**
 * The colours of the app's CHROME — the header, the case rail and the footer —
 * which are dark in BOTH schemes.
 *
 * That is the composition rather than an oversight. In light mode the chrome
 * is near-black against an ivory page, which is what gives the frame a
 * permanent identity and keeps it from dissolving into the page now that no
 * colour is doing that job; in dark mode it is a step above the ground for
 * the same reason. It is the one place the app paints a colour the scheme did
 * not choose, so anything drawn on it takes its ink and its muted text from
 * HERE rather than from `theme.colors`, which would hand it near-black text
 * on near-black the moment somebody switched to light.
 *
 * Read from `brandColors.dark` rather than spelled out, so the one file that
 * owns the palette still owns this.
 */
export const chromeColors = brandColors.dark;

/**
 * The theme the design system's leaves run under when they sit on the chrome.
 *
 * BOTH slots hold the dark palette, which is the same trick
 * `ThemePreferenceProvider` uses for an explicit scheme: the package's leaves
 * consult the OS themselves and cannot be redirected, so the way to pin them
 * is to make both answers the same one. Without it a `Sidebar.Item` in light
 * mode takes near-black ink from the active scheme and paints it on the
 * near-black rail, and an `Avatar` in the header does the same.
 *
 * Nesting is supported and the nearest provider wins outright — the package
 * says so explicitly — so this pins one region without touching the rest of
 * the app. Frozen and hoisted so it is one stable object for the module's
 * life, which is what keeps the leaves' `React.memo` boundaries intact.
 */
export const CHROME_THEME = Object.freeze({
  light: brandColors.dark,
  dark: brandColors.dark,
  fonts: brandFonts,
  radii: brandRadii,
});
