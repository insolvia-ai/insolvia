// Renders Insolvia's brand palette into the three surfaces that cannot read
// `brand/colors.json` for themselves.
//
//   npm run tokens
//   npm run tokens:check
//
// `--check` renders in memory and exits non-zero if a committed output has
// drifted, so CI gates both a hand-edit and a palette change that forgot to
// regenerate. Same contract as `reconcile-cognito-branding.ts`, which handles
// the fourth surface (Cognito's hosted sign-in page).
//
// ── Why generate at all ─────────────────────────────────────────────────────
//
// The palette has four consumers and no shared import between them. The app is
// a workspace member; the marketing site and the admin portal are deliberately
// OUTSIDE the workspace, each with its own lockfile (see the root
// package.json), so neither can import a module from this tree. And two of the
// three need CSS, which cannot read JSON at all.
//
// The alternative was four hand-maintained copies of the same twenty-six
// values, in four languages, with nothing to notice when one fell behind.
// `npm run tokens:check` is already a required PR check, so generating instead
// costs nothing at review time and makes drift impossible rather than merely
// unlikely.
//
// ── Why the web outputs omit the derived states ─────────────────────────────
//
// `theme.css` declares `--color-primary-hover` and friends as `color-mix()`
// over their base role, so overriding `--color-primary` moves them with it —
// and the blends land on exactly the values `brand/colors.json` records
// (`#0B2A4A` mixed 88% with black IS `#0A2541`). Emitting them explicitly
// would replace a live derivation with a frozen copy of its current output,
// which is strictly worse: the next brand change would move the base and leave
// its hover behind. React Native has no such blend, so the app's module keeps
// them. See `DERIVED` in `brand-palette.ts`.

import { readFileSync, writeFileSync } from 'node:fs';
import { format, resolveConfig } from 'prettier';
import { join } from 'node:path';

import {
  DERIVED,
  FONT_ROLES,
  MODES,
  fonts,
  overrides,
  repoRoot,
  BRAND,
  BRAND_FONTS,
} from './brand-palette.ts';
import type { FontRole, Palette, Scheme } from './brand-palette.ts';

const REGEN_COMMAND = 'npm run tokens';

/**
 * The app's module, and the two stylesheets.
 *
 * Each is committed. A generated file that is gitignored would be absent from
 * a fresh clone and break the build before anyone ran the generator, and these
 * are imported by application source.
 */
const APP = 'apps/insolvia_app/src/theme/brand-colors.ts';
const MARKETING = 'apps/insolvia_marketing/app/styles/brand.css';
const ADMIN = 'apps/insolvia_admin/src/styles/brand.css';

async function main(args: string[]): Promise<void> {
  const check = args.includes('--check');
  const root = repoRoot();
  const brand = overrides(root);
  const families = fonts(root);

  /**
   * Every output is run through Prettier before it is written or compared.
   *
   * Not cosmetic — it is what stops two CI checks disagreeing. `npm run format`
   * checks these files like any other, and `tokens:check` compares them
   * byte-for-byte against what this renders; if the renderer's idea of a line
   * break differs from Prettier's by one character, one of the two must fail
   * and no hand-edit can satisfy both (the files carry DO NOT EDIT). Rendering
   * canonically means the question cannot come up. The mono stack is what
   * surfaced it: long enough that Prettier wraps the declaration.
   */
  const outputs: ReadonlyArray<readonly [string, string]> = [
    [APP, await pretty(appModule(brand, families), APP)],
    [MARKETING, await pretty(stylesheet(brand, families, MARKETING), MARKETING)],
    [ADMIN, await pretty(stylesheet(brand, families, ADMIN), ADMIN)],
  ];

  const drifted: string[] = [];
  for (const [path, rendered] of outputs) {
    const file = join(root, path);
    const current = readCurrent(file);
    if (current === rendered) continue;

    if (check) {
      drifted.push(path);
      continue;
    }
    writeFileSync(file, rendered);
    process.stdout.write(`wrote ${path}\n`);
  }

  if (drifted.length === 0) {
    if (check) process.stdout.write(`brand themes are in sync with ${BRAND} and ${BRAND_FONTS}.\n`);
    return;
  }

  process.stderr.write(`These generated themes have drifted from ${BRAND}/${BRAND_FONTS}:\n\n`);
  for (const path of drifted) process.stderr.write(`  ${path}\n`);
  process.stderr.write(
    `\nEither a file was hand-edited, or ${BRAND} changed without regenerating.\nRun:\n\n  ${REGEN_COMMAND}\n`,
  );
  process.exitCode = 1;
}

/** One rendered output, formatted the way the repo's Prettier config would. */
async function pretty(source: string, path: string): Promise<string> {
  const options = await resolveConfig(path);
  return format(source, { ...options, filepath: path });
}

/** An output that does not exist yet counts as drifted, not as a crash. */
function readCurrent(file: string): string | null {
  try {
    return readFileSync(file, 'utf8');
  } catch {
    return null;
  }
}

/**
 * The app's overrides, as a TypeScript module.
 *
 * TypeScript rather than JSON because this is the one consumer that renders
 * the design system's `.native` leaves, and it feeds them two different ways:
 * `ThemeProvider` takes the overrides object as-is, and the app's own
 * `useTheme()` merges them over the tokens defaults. A `as const` module gives
 * both a literal type and needs no `resolveJsonModule`, no Metro
 * watch-folder reasoning, and no JSON transform in Jest.
 *
 * DERIVED states are kept here — see the header.
 */
function appModule(brand: Palette, families: Readonly<Record<FontRole, string>>): string {
  const lines: string[] = [
    '// GENERATED by tool/render-brand-theme.ts from brand/colors.json — DO NOT EDIT.',
    `// Regenerate with \`${REGEN_COMMAND}\`; \`npm run tokens:check\` gates it.`,
    '//',
    "// Insolvia's brand overrides for @insolvia-ai/design-system, whose base",
    '// theme is deliberately unbranded from tokens 0.5.0 on. Only the roles',
    '// Insolvia moves are here; everything else falls through to the package.',
    '//',
    '// The hover/active states are stated rather than derived because React',
    '// Native has no live blend — `ThemeProvider` merges values, not formulas,',
    '// so overriding `primary` alone would leave its hover behind. The web',
    '// surfaces omit them for the opposite reason. See brand/colors.json.',
    '',
    'export const brandColors = {',
  ];

  for (const mode of MODES) {
    lines.push(`  ${mode}: {`);
    for (const [role, hex] of entries(brand[mode])) {
      lines.push(`    ${role}: '${hex}',`);
    }
    lines.push('  },');
  }

  lines.push('} as const;', '');

  lines.push(
    '/**',
    ' * The brand type families, from brand/fonts.json.',
    ' *',
    " * Two consumers, for the same reason `brandColors` has two: the app's own",
    ' * `useTheme()` reads them as `typography`, and `ThemeProvider` takes them as',
    " * `fonts` so the design system's native leaves follow. A family stated in",
    ' * only one of the two renders branded headings over system body text.',
    ' *',
    ' * Naming a family does not load it. The faces are self-hosted .woff2 under',
    ' * `public/fonts`, declared @font-face in `public/index.html`.',
    ' */',
    'export const brandFonts = {',
  );
  for (const role of FONT_ROLES) {
    // Single-quoted, like every other value this file emits: a stack contains
    // double quotes ("Public Sans") and no single ones, and the output has to
    // land prettier-clean or `npm run ci` fails on a file nobody may hand-edit.
    lines.push(`  ${role}: '${families[role].replace(/'/g, "\\'")}',`);
  }
  lines.push('} as const;', '');

  return lines.join('\n');
}

/**
 * The web overrides, as Tailwind v4 custom properties.
 *
 * Light values go inside `@theme` — that is where the design system declares
 * them, and where Tailwind reads them to generate utilities. Dark values go in
 * a `[data-theme='dark']` rule, matching `theme.css`'s own dark block; ours
 * lands after it in source order at equal specificity, so it wins.
 */
function stylesheet(
  brand: Palette,
  families: Readonly<Record<FontRole, string>>,
  path: string,
): string {
  const lines: string[] = [
    '/* GENERATED by tool/render-brand-theme.ts from brand/colors.json — DO NOT EDIT.',
    ` * Regenerate with \`${REGEN_COMMAND}\`; \`npm run tokens:check\` gates it.`,
    ' *',
    " * Insolvia's brand overrides for @insolvia-ai/design-system, whose base theme",
    ' * is deliberately unbranded from tokens 0.5.0 on: primary, accent and brand all',
    ' * resolve to neutral-12 and the chrome is steps on the neutral ramp. Only the',
    ' * roles Insolvia moves are here; everything else falls through to the package.',
    ' *',
    ' * The hover/active states are absent on purpose. theme.css derives them with',
    ' * color-mix() over the base role, so overriding --color-primary moves them too;',
    ' * restating them here would freeze a derivation that should stay live.',
    ' *',
    ` * Imported by ${path.replace('brand.css', 'app.css')}, AFTER the design system's`,
    ' * theme.css — an override that lands first does nothing.',
    ' */',
    '',
    '@theme {',
  ];

  for (const [role, hex] of paintable(brand.light)) {
    lines.push(`  ${cssVar(role)}: ${hex.toLowerCase()};`);
  }
  // Families are scheme-independent, so they are stated once, in @theme, and
  // never repeated in the dark block. NOTE: this surface receives the names but
  // does not load the faces — only the app does today. Each stack ends in the
  // generic the base theme used, so an unloaded family renders exactly what it
  // rendered before. See brand/fonts.json.
  lines.push('');
  for (const role of FONT_ROLES) {
    lines.push(`  --font-${role}: ${families[role]};`);
  }
  lines.push('}', '', "[data-theme='dark'] {");
  for (const [role, hex] of paintable(brand.dark)) {
    lines.push(`  ${cssVar(role)}: ${hex.toLowerCase()};`);
  }
  lines.push('}', '');

  return lines.join('\n');
}

/** The roles a stylesheet states: everything the brand claims but the blends. */
function paintable(scheme: Scheme): ReadonlyArray<readonly [string, string]> {
  return entries(scheme).filter(([role]) => !DERIVED.has(role));
}

/**
 * Sorted, so the output is a function of the palette's CONTENT and not of key
 * order in the source file. Reordering `brand/colors.json` should not produce
 * a diff in three generated files.
 */
function entries(scheme: Scheme): ReadonlyArray<readonly [string, string]> {
  return Object.entries(scheme).sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0));
}

/** `surfaceAlt` → `--color-surface-alt`, matching the design system's naming. */
function cssVar(role: string): string {
  return `--color-${role.replace(/[A-Z]/g, (c) => `-${c.toLowerCase()}`)}`;
}

await main(process.argv.slice(2));
