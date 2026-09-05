// Rewrites the colour slots of Cognito's managed-login branding from
// Insolvia's brand palette — the installed `@insolvia-ai/tokens` with
// `brand/colors.json` layered on top (see `brand-palette.ts`).
//
//   npm run tokens
//   npm run tokens:check
//
// `--check` reconciles in memory and exits non-zero if the committed document
// has drifted, so CI gates a hand-edit, a tokens bump, and a brand change that
// forgot to regenerate.
//
// It reads the MERGED palette rather than the tokens package directly because
// the sign-in page is one of four surfaces wearing the same brand, and it is
// the only one a user meets before the app has loaded. Reading the unbranded
// base here is what would make Cognito the odd one out — grey where the app is
// navy — with nothing in the tree to say so.
//
// ── Why this is here and not in the design-system repo ──────────────────────
//
// This was the third output of that repo's token generator, back when the
// tokens package was a workspace member of THIS repo and the generator could
// simply write across the tree. It stayed behind when the design system was
// extracted: `infra/` is this repo's, and a published package must not depend
// on a consumer's directory layout — a generator that writes into a path only
// one consumer has would silently no-op everywhere else.
//
// So the split is by ownership. The design-system repo owns the token VALUES
// and renders them; this file owns the mapping of those values onto AWS's
// document, which is infrastructure nobody else can see.
//
// ── Why it does not re-derive anything ──────────────────────────────────────
//
// The alternative was to compute the colours here from a palette — the blend
// maths for the hover/active states in particular. That is the version worth
// refusing: the sign-in page's colours would be produced by a second
// implementation and would drift the first time either changed, with nothing
// to say so. `brand-palette.ts` resolves every role once, for every surface.

import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

import { MODES, palette, repoRoot, BRAND } from './brand-palette.ts';
import type { Palette } from './brand-palette.ts';

const OUT = 'infra/modules/auth/managed-login-settings.json';
const SOURCE = `the installed @insolvia-ai/tokens and ${BRAND}`;
const REGEN_COMMAND = 'npm run tokens';

/**
 * Colour slots in the managed-login document, each mapped to the semantic role
 * that owns it. Dotted paths under the document root; `{mode}` is substituted
 * with `lightMode`/`darkMode` and the role resolved in that mode.
 *
 * Semantic roles only — never palette names. That is the rule that makes a
 * re-brand a one-file change in the design-system repo, and it is why the dark
 * primary button is brass rather than the inverted white-on-navy a hand-mapping
 * would reach for: `primary` in the dark scheme already answers that question,
 * and the sign-in page must agree with the app rather than relitigate it.
 */
const COGNITO_COLORS: ReadonlyArray<readonly [string, string]> = [
  // Primary button — the "Sign in" call to action.
  ['components.primaryButton.{mode}.defaults.backgroundColor', 'primary'],
  ['components.primaryButton.{mode}.defaults.textColor', 'primaryText'],
  ['components.primaryButton.{mode}.hover.backgroundColor', 'primaryHover'],
  ['components.primaryButton.{mode}.hover.textColor', 'primaryText'],
  ['components.primaryButton.{mode}.active.backgroundColor', 'primaryActive'],
  ['components.primaryButton.{mode}.active.textColor', 'primaryText'],
  ['components.primaryButton.{mode}.disabled.backgroundColor', 'muted'],
  ['components.primaryButton.{mode}.disabled.borderColor', 'muted'],

  // Secondary button, and the identity-provider buttons that share its shape.
  // No federated providers are configured, so the IdP ones never render today;
  // mapped anyway so enabling one later cannot surface AWS blue.
  ['components.secondaryButton.{mode}.defaults.backgroundColor', 'card'],
  ['components.secondaryButton.{mode}.defaults.borderColor', 'primary'],
  ['components.secondaryButton.{mode}.defaults.textColor', 'primary'],
  ['components.secondaryButton.{mode}.hover.backgroundColor', 'surfaceAlt'],
  ['components.secondaryButton.{mode}.hover.borderColor', 'primaryHover'],
  ['components.secondaryButton.{mode}.hover.textColor', 'primaryHover'],
  ['components.secondaryButton.{mode}.active.backgroundColor', 'surfaceAlt'],
  ['components.secondaryButton.{mode}.active.borderColor', 'primaryActive'],
  ['components.secondaryButton.{mode}.active.textColor', 'primaryActive'],
  ['components.idpButton.standard.{mode}.defaults.backgroundColor', 'card'],
  ['components.idpButton.standard.{mode}.defaults.borderColor', 'line'],
  ['components.idpButton.standard.{mode}.defaults.textColor', 'ink'],
  ['components.idpButton.standard.{mode}.hover.backgroundColor', 'surfaceAlt'],
  ['components.idpButton.standard.{mode}.hover.borderColor', 'primary'],
  ['components.idpButton.standard.{mode}.hover.textColor', 'ink'],
  ['components.idpButton.standard.{mode}.active.backgroundColor', 'surfaceAlt'],
  ['components.idpButton.standard.{mode}.active.borderColor', 'primaryActive'],
  ['components.idpButton.standard.{mode}.active.textColor', 'ink'],

  // Page and form surfaces.
  ['components.pageBackground.{mode}.color', 'bg'],
  ['components.form.{mode}.backgroundColor', 'card'],
  ['components.form.{mode}.borderColor', 'line'],
  ['components.pageHeader.{mode}.background.color', 'surfaceAlt'],
  ['components.pageHeader.{mode}.borderColor', 'line'],
  ['components.pageFooter.{mode}.background.color', 'surfaceAlt'],
  ['components.pageFooter.{mode}.borderColor', 'line'],

  // Text.
  ['components.pageText.{mode}.headingColor', 'ink'],
  ['components.pageText.{mode}.bodyColor', 'ink'],
  ['components.pageText.{mode}.descriptionColor', 'muted'],

  // Inputs, labels, and the focus ring — the keyboard-navigation affordance,
  // which was still AWS blue in both modes before this existed.
  ['componentClasses.input.{mode}.defaults.backgroundColor', 'card'],
  ['componentClasses.input.{mode}.defaults.borderColor', 'line'],
  ['componentClasses.input.{mode}.placeholderColor', 'muted'],
  ['componentClasses.inputLabel.{mode}.textColor', 'ink'],
  ['componentClasses.inputDescription.{mode}.textColor', 'muted'],
  ['componentClasses.focusState.{mode}.borderColor', 'primary'],
  ['componentClasses.divider.{mode}.borderColor', 'line'],

  // Links — "Forgot your password?".
  ['componentClasses.link.{mode}.defaults.textColor', 'primary'],
  ['componentClasses.link.{mode}.hover.textColor', 'primaryHover'],

  // Selection controls and the dropdown.
  ['componentClasses.optionControls.{mode}.defaults.backgroundColor', 'card'],
  ['componentClasses.optionControls.{mode}.defaults.borderColor', 'line'],
  ['componentClasses.optionControls.{mode}.selected.backgroundColor', 'primary'],
  ['componentClasses.optionControls.{mode}.selected.foregroundColor', 'primaryText'],
  ['componentClasses.dropDown.{mode}.defaults.itemBackgroundColor', 'card'],
  ['componentClasses.dropDown.{mode}.hover.itemBackgroundColor', 'surfaceAlt'],
  ['componentClasses.dropDown.{mode}.hover.itemTextColor', 'ink'],
  ['componentClasses.dropDown.{mode}.hover.itemBorderColor', 'line'],
  ['componentClasses.dropDown.{mode}.match.itemTextColor', 'primary'],

  // Status. `success`/`warning`/`danger` carry their own semantic roles, so a
  // re-brand moves them too rather than leaving AWS's red and amber behind.
  ['componentClasses.statusIndicator.{mode}.error.indicatorColor', 'danger'],
  ['componentClasses.statusIndicator.{mode}.error.borderColor', 'danger'],
  ['componentClasses.statusIndicator.{mode}.success.indicatorColor', 'success'],
  ['componentClasses.statusIndicator.{mode}.success.borderColor', 'success'],
  ['componentClasses.statusIndicator.{mode}.warning.indicatorColor', 'warning'],
  ['componentClasses.statusIndicator.{mode}.warning.borderColor', 'warning'],
  ['components.alert.{mode}.error.borderColor', 'danger'],
];

type JsonValue = string | number | boolean | null | JsonValue[] | { [key: string]: JsonValue };
type JsonObject = { [key: string]: JsonValue };

function main(args: string[]): void {
  const check = args.includes('--check');
  const root = repoRoot();
  const file = join(root, OUT);

  const colors = palette(root);
  const current = readFileSync(file, 'utf8');
  const reconciled = reconcile(colors, current);

  if (current === reconciled) {
    process.stdout.write(`${OUT} is in sync with ${SOURCE}.\n`);
    return;
  }

  if (check) {
    process.stderr.write(`${OUT} has drifted from ${SOURCE}.\n\n`);
    process.stderr.write(
      'Either the file was hand-edited, the tokens dependency was bumped, or\n' +
        `${BRAND} changed — in each case without regenerating. Run:\n\n`,
    );
    process.stderr.write(`  ${REGEN_COMMAND}\n`);
    process.exitCode = 1;
    return;
  }

  writeFileSync(file, reconciled);
  process.stdout.write(`wrote ${OUT}\n`);
}

/**
 * Rewrite the colour slots of the managed-login settings, leaving every other
 * key exactly as found.
 *
 * RECONCILED, not rendered: the document's structure — layout, border radii,
 * `enabled` flags, which auth methods appear — is AWS's schema, owned by the
 * console's branding editor and re-exported when it changes. Only the colour
 * slots belong to us. Rewriting those in place makes this idempotent, so
 * `--check` asks exactly the right question ("do the colours still match?")
 * without caring about structure it does not own.
 *
 * A path that does not exist is a hard error, not a skip: it means AWS changed
 * its schema, or a console re-export dropped a component, and silently
 * branding fewer things than intended is precisely the failure this exists to
 * prevent — the sign-in page would quietly regain a patch of AWS blue and
 * nothing would say so.
 */
function reconcile(colors: Palette, current: string): string {
  const root = asObject(JSON.parse(current) as JsonValue, OUT);

  for (const mode of MODES) {
    const values = colors[mode];
    const key = mode === 'light' ? 'lightMode' : 'darkMode';
    for (const [template, role] of COGNITO_COLORS) {
      const hex = values[role];
      if (hex === undefined) {
        throw new Error(
          `No such semantic role "${role}" in ${mode}. Neither the installed ` +
            `@insolvia-ai/tokens nor ${BRAND} defines it — the tokens package ` +
            'dropped or renamed it, so update COGNITO_COLORS.',
        );
      }
      setColor(root, template.replace('{mode}', key).split('.'), cognitoColor(hex));
    }
  }

  return `${JSON.stringify(root, null, 2)}\n`;
}

/** Walk a dotted path and assign, asserting every segment exists. */
function setColor(root: JsonObject, path: readonly string[], value: string): void {
  let node: JsonObject = root;
  for (let index = 0; index < path.length - 1; index += 1) {
    const segment = path[index] as string;
    if (!(segment in node)) {
      throw new Error(`${OUT}: no such path segment "${segment}" in ${path.join('.')}`);
    }
    node = asObject(node[segment], `${OUT}:${path.slice(0, index + 1).join('.')}`);
  }
  const leaf = path[path.length - 1] as string;
  if (!(leaf in node)) {
    throw new Error(`${OUT}: no such colour slot "${path.join('.')}"`);
  }
  node[leaf] = value;
}

/**
 * Cognito wants `rrggbbaa` with no leading `#`, lower-cased for stability —
 * the console writes mixed case and a diff should reflect a real colour change,
 * not a round trip through the branding editor.
 */
function cognitoColor(hex: string): string {
  const bare = hex.replace('#', '').toLowerCase();
  return bare.length === 8 ? bare : `${bare}ff`;
}

function asObject(value: JsonValue | undefined, where: string): JsonObject {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) {
    throw new Error(`${where}: expected an object.`);
  }
  return value;
}

main(process.argv.slice(2));
