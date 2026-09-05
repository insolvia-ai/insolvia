// Insolvia's colour palette: the installed `@insolvia-ai/tokens`, with
// `brand/colors.json` layered on top.
//
// Shared by the two generators that consume it —
// `reconcile-cognito-branding.ts` (the sign-in page) and `render-brand-theme.ts`
// (the app, the marketing site, the admin portal). Neither reads the tokens
// package directly any more, so there is exactly one answer in this repo to
// "what colour is `primary`?" and all four surfaces are generated from it.
//
// ── Why a merge, and not simply our own palette ──────────────────────────────
//
// From tokens 0.5.0 the base theme is monochrome by design: `primary`,
// `accent` and `brand` all resolve to neutral-12, and the chrome is steps on
// the neutral ramp. The package left the brand seam empty on purpose, so a
// re-brand is an override at the consumer rather than a fork.
//
// Layering keeps that bargain in both directions. `brand/colors.json` names
// only the roles Insolvia actually moves; everything else — the status
// colours, `dangerText`, the overlay values, the twelve ramp steps — stays the
// package's to improve. A tokens release that adds a role or re-measures a
// contrast reaches every surface here without an edit, which is the whole
// reason the base is worth tracking rather than snapshotting.
//
// The merge is per scheme and one level deep, which is all the shape allows:
// `colors.json` is `{ light: {role: hex}, dark: {role: hex} }` on both sides.

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { join, resolve } from 'node:path';

/** The brand overrides, relative to the repo root. */
export const BRAND = 'brand/colors.json';

const TOKENS_PACKAGE = '@insolvia-ai/tokens';

/**
 * Read through the package's `exports` rather than a hand-built
 * `node_modules/...` path, so a hoisting change cannot silently point this at
 * nothing — and an absent dependency fails loudly, naming the package.
 *
 * It is the JSON and not `src/tokens.ts` because Node refuses to strip
 * TypeScript types for any file under `node_modules`
 * (ERR_UNSUPPORTED_NODE_MODULES_TYPE_STRIPPING), and these tools run as plain
 * `node` with no loader. Metro and Vite have no such limit, which is why the
 * apps are unaffected.
 */
const COLORS_SPECIFIER = `${TOKENS_PACKAGE}/colors.json`;

/** The brightnesses every surface here carries a mapping for. */
export const MODES = ['light', 'dark'] as const;
export type Mode = (typeof MODES)[number];

export type Scheme = Readonly<Record<string, string>>;
export type Palette = Readonly<Record<Mode, Scheme>>;

type JsonValue = string | number | boolean | null | JsonValue[] | { [key: string]: JsonValue };
type JsonObject = { [key: string]: JsonValue };

/**
 * Roles whose value the web side computes rather than stores.
 *
 * `theme.css` declares each of these as a `color-mix()` over its base role —
 * darkening on light, lightening on dark — so overriding `--color-primary`
 * moves `--color-primary-hover` with it. Emitting an explicit override for one
 * would FREEZE it instead, breaking the derivation for every consumer that
 * later re-brands, so `render-brand-theme.ts` omits them from the CSS.
 *
 * React Native has no live blend: `@insolvia-ai/tokens` ships these as
 * pre-computed values and `ThemeProvider` merges values, not formulas. So the
 * native surfaces DO need them stated, which is why they are in
 * `brand/colors.json` at all.
 */
export const DERIVED = new Set(['primaryHover', 'primaryActive', 'accentHover', 'dangerHover']);

/**
 * The merged palette: every role the tokens package defines, with Insolvia's
 * overrides applied.
 *
 * Complete by construction — the result always carries every role the package
 * has, so a consumer that spells one out cannot silently get `undefined`.
 */
export function palette(root: string): Palette {
  const base = loadTokens();
  const brand = loadBrand(root);

  const merged = {} as Record<Mode, Scheme>;
  for (const mode of MODES) {
    merged[mode] = { ...base[mode], ...brand[mode] };
  }
  return merged;
}

/**
 * The overrides alone, unmerged — what `brand/colors.json` actually claims.
 *
 * The CSS output needs this rather than the merge: emitting all ~50 roles
 * would restate the package's own defaults as ours, and a later tokens release
 * that improved one would be silently overridden by a stale copy of it.
 */
export function overrides(root: string): Palette {
  return loadBrand(root);
}

function loadTokens(): Palette {
  let path: string;
  try {
    path = fileURLToPath(import.meta.resolve(COLORS_SPECIFIER));
  } catch {
    throw new Error(
      `Cannot resolve ${COLORS_SPECIFIER}. Is ${TOKENS_PACKAGE} installed? ` +
        'It comes from github.com/insolvia-ai/design-system via GitHub Packages; ' +
        'see scripts/github-packages-auth.sh if the install 401s.',
    );
  }
  return schemes(readFileSync(path, 'utf8'), COLORS_SPECIFIER);
}

function loadBrand(root: string): Palette {
  const path = join(root, BRAND);
  let raw: string;
  try {
    raw = readFileSync(path, 'utf8');
  } catch {
    throw new Error(`Cannot read ${BRAND}. It is the source of every generated theme here.`);
  }
  return schemes(raw, BRAND);
}

/**
 * Parse `{ light: {...}, dark: {...} }`, asserting both schemes and rejecting
 * a non-string value.
 *
 * The `//` key that carries `brand/colors.json`'s reasoning is skipped rather
 * than rejected — JSON has no comments, and the repo's manifests already use
 * this convention.
 */
function schemes(raw: string, where: string): Palette {
  const document = asObject(JSON.parse(raw) as JsonValue, where);

  const result = {} as Record<Mode, Scheme>;
  for (const mode of MODES) {
    const scheme = asObject(document[mode], `${where}:${mode}`);
    const values: Record<string, string> = {};
    for (const [role, value] of Object.entries(scheme)) {
      if (role === '//') continue;
      if (typeof value !== 'string') {
        throw new Error(`${where}: ${mode}.${role} is not a string.`);
      }
      values[role] = value;
    }
    result[mode] = values;
  }
  return result;
}

function asObject(value: JsonValue | undefined, where: string): JsonObject {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) {
    throw new Error(`${where}: expected an object.`);
  }
  return value;
}

/** Resolved from the module's own location, so behaviour does not vary by cwd. */
export function repoRoot(): string {
  return resolve(import.meta.dirname, '..');
}
