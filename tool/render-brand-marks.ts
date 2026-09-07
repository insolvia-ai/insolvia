// Colours Insolvia's marks for every surface that needs a file of its own.
//
//   npm run tokens
//   npm run tokens:check
//
// `brand/wordmark.svg` and `brand/icon.svg` are cut from the display face by
// `scripts/render-brand-marks.sh` and carry NO colour — only outlines, with an
// `id` on each path naming the role it plays. This reads them, resolves those
// roles against the palette, and writes one coloured copy per consumer.
//
// The split is the same one `brand/colors.json` already makes, extended to
// shape: the cutter owns the letterforms, this owns what colour they are, and
// neither is a place to hand-edit an SVG. A logo checked in with its fill
// baked in is a logo that survives the next re-brand.
//
// WHY THE FILES EXIST AT ALL. Everywhere the app itself renders the mark, it
// renders the `Wordmark` COMPONENT and no file is involved. These copies are
// for the two places a component cannot reach: Cognito's managed-login page,
// which serves the logo as an <img> from AWS's own origin, and the favicon,
// which has no document to inherit anything from. Both are outside React, and
// both are the first thing a user sees.
//
// WHY THE RASTERS ARE NOT HERE. `favicon.ico` and the PWA icons are pixels,
// and pixels cannot defer a colour to render time, so the cutter bakes the
// palette in when it rasterises. That makes a re-brand a two-step job — change
// brand/colors.json, run `npm run tokens`, then re-run the cutter — which is
// the one seam in this arrangement. `--check` says so out loud rather than
// letting a stale icon pass quietly.

import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

import { BRAND, MODES, palette, repoRoot } from './brand-palette.ts';
import type { Mode, Palette } from './brand-palette.ts';

const REGEN_COMMAND = 'npm run tokens';
const CUTTER = 'scripts/render-brand-marks.sh';

/**
 * A mark's paths, by `id`, mapped to the semantic role each one wears.
 *
 * Semantic roles only, never palette names — the same rule the Cognito
 * reconciler follows, and for the same reason: a re-brand should move the mark
 * without anything here being edited.
 */
const WORDMARK = {
  source: 'brand/wordmark.svg',
  roles: { mark: 'brand', dot: 'accent' },
} as const;

/**
 * The icon is DARK IN BOTH SCHEMES, so it resolves its roles in the dark
 * palette whatever the viewer's browser is doing.
 *
 * That is not a shortcut around emitting two of them. The tile is chrome — the
 * same argument the case rail makes — and it is chrome that appears at 16px
 * against a browser's own tab strip, which follows the OS rather than the
 * page. An icon that inverted with the scheme would be a near-white square on
 * a light tab strip: the mark would vanish and only its letter would remain.
 */
const ICON = {
  source: 'brand/icon.svg',
  roles: { ground: 'bg', letter: 'ink' },
  mode: 'dark',
} as const;

/**
 * Where each coloured copy goes.
 *
 * Every one is committed. A generated file that is gitignored is absent from a
 * fresh clone, and Terraform reads two of these at plan time.
 */
const OUTPUTS: ReadonlyArray<{
  readonly path: string;
  readonly mark: typeof WORDMARK | typeof ICON;
  readonly mode: Mode;
}> = [
  // Cognito's managed-login page: the form's logo, one asset per colour mode,
  // uploaded by infra/modules/auth/main.tf.
  ...MODES.map((mode) => ({
    path: `infra/modules/auth/wordmark-${mode}.svg`,
    mark: WORDMARK,
    mode,
  })),
  // …and its favicon, for the tab the sign-in page opens in.
  { path: 'infra/modules/auth/favicon.svg', mark: ICON, mode: ICON.mode },
  // The app's own favicon. The .ico beside it is the fallback for browsers
  // without SVG icon support, and comes from the cutter.
  { path: 'apps/insolvia_app/public/favicon.svg', mark: ICON, mode: ICON.mode },
];

function main(args: string[]): void {
  const check = args.includes('--check');
  const root = repoRoot();
  const colors = palette(root);

  const sources = new Map<string, string>();
  const read = (path: string): string => {
    const cached = sources.get(path);
    if (cached !== undefined) return cached;
    const file = join(root, path);
    if (!existsSync(file)) {
      throw new Error(`${path} is missing — cut the marks first:\n\n  ./${CUTTER}\n`);
    }
    const source = readFileSync(file, 'utf8');
    sources.set(path, source);
    return source;
  };

  const drifted: string[] = [];
  for (const { path, mark, mode } of OUTPUTS) {
    const rendered = render(read(mark.source), mark, colors, mode, path);
    const file = join(root, path);
    if (existsSync(file) && readFileSync(file, 'utf8') === rendered) continue;

    if (check) {
      drifted.push(path);
      continue;
    }
    writeFileSync(file, rendered);
    process.stdout.write(`wrote ${path}\n`);
  }

  if (drifted.length === 0) {
    if (check) process.stdout.write(`brand marks are in sync with ${BRAND}.\n`);
    return;
  }

  process.stderr.write(`These generated marks have drifted from ${BRAND}:\n\n`);
  for (const path of drifted) process.stderr.write(`  ${path}\n`);
  process.stderr.write(
    `\nEither a file was hand-edited, or ${BRAND} changed without regenerating.\nRun:\n\n` +
      `  ${REGEN_COMMAND}\n\n` +
      'If a COLOUR changed, the committed rasters — favicon.ico and the PWA\n' +
      'icons — have the old one baked in and cannot be regenerated from here.\n' +
      `Re-cut them too:\n\n  ./${CUTTER}\n`,
  );
  process.exitCode = 1;
}

/**
 * Re-emit the mark as a MINIMAL document, with every path filled by the role
 * its `id` names.
 *
 * Rebuilt rather than string-patched, and that is Cognito's doing. It
 * sanitises an uploaded SVG against an allowlist and rejects the file outright
 * — `InvalidParameterException: element [svg#role|aria-label] is not allowed`
 * — for anything outside it, including the `role="img"` and `aria-label` the
 * source carries and, on the same reasoning, the comment block explaining
 * where the file came from. So the output holds a viewBox, the paths, and
 * nothing else. The commentary stays in `brand/wordmark.svg` and
 * `brand/icon.svg`, where a person reads it and no API parses it.
 *
 * A rejected upload is not a quiet failure: the resource is REPLACED on an
 * asset change, so Terraform destroys the branding, fails to create the
 * replacement, and leaves the sign-in page on Cognito's defaults until the
 * next successful apply. Worth keeping in mind before loosening this.
 *
 * Document order is the source's, because it is paint order — the icon's
 * ground has to be laid before its letter.
 *
 * Deliberately strict: a role the palette does not define, and a path the
 * source does not carry, are each an error rather than a silent skip. Either
 * produces a mark that renders — just wrong, and usually as a black shape on
 * a black ground that nobody notices until it is on a sign-in page.
 */
function render(
  source: string,
  mark: typeof WORDMARK | typeof ICON,
  colors: Palette,
  mode: Mode,
  out: string,
): string {
  const viewBox = /<svg[^>]*\sviewBox="([^"]+)"/.exec(source)?.[1];
  if (viewBox === undefined) {
    throw new Error(`${mark.source}: no viewBox — re-cut it:\n\n  ./${CUTTER}\n`);
  }

  const roles: Record<string, string> = mark.roles;
  const paths: string[] = [];
  for (const [, id, d] of source.matchAll(/<path id="([^"]+)" d="([^"]+)"\s*\/>/g)) {
    const role = roles[id as string];
    if (role === undefined) {
      throw new Error(
        `${mark.source}: <path id="${id}"> wears no role. The cutter and the ` +
          'roles in this file have to agree about which paths a mark has.',
      );
    }
    const hex = colors[mode][role];
    if (hex === undefined) {
      throw new Error(
        `${out}: no semantic role "${role}" in the ${mode} scheme. Neither the ` +
          `installed @insolvia-ai/tokens nor ${BRAND} defines it, so the roles in ` +
          'this file need updating.',
      );
    }
    paths.push(`  <path fill="${hex}" d="${d}"/>`);
  }

  const missing = Object.keys(roles).filter((id) => !source.includes(`<path id="${id}" `));
  if (missing.length > 0) {
    throw new Error(
      `${mark.source}: no <path id="${missing.join('">, no <path id="')}"> to fill. ` +
        `Either the cutter's roles changed, or the file was hand-edited — re-cut it:\n\n` +
        `  ./${CUTTER}\n`,
    );
  }

  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="${viewBox}">\n${paths.join('\n')}\n</svg>\n`;
}

main(process.argv.slice(2));
