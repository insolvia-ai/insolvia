// Rewrites the colour slots of Cognito's managed-login branding from the
// installed `@insolvia-ai/tokens`.
//
//   npm run tokens
//   npm run tokens:check
//
// `--check` reconciles in memory and exits non-zero if the committed document
// has drifted, so CI gates both a hand-edit and a tokens bump that forgot to
// regenerate.
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
// ── Why it reads colors.json rather than the package's TypeScript ───────────
//
// `@insolvia-ai/tokens` also exports `src/tokens.ts`, which is the nicer
// import and is what the app uses. It is unavailable here: Node refuses to
// strip TypeScript types for any file under `node_modules`
// (ERR_UNSUPPORTED_NODE_MODULES_TYPE_STRIPPING), and this script runs as plain
// `node` with no loader. Metro and Vite have no such limit, which is why the
// app and the marketing site are unaffected and this is the only consumer that
// needs the JSON.
//
// The alternative was to re-derive the colours here from the palette — the
// blend maths for the hover/active states in particular. That is the version
// worth refusing: the sign-in page's colours would be computed by a second
// implementation, in a second repo, and would drift the first time either
// changed, with nothing to say so.

import { readFileSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { join, resolve } from 'node:path';

const OUT = 'infra/modules/auth/managed-login-settings.json';
const TOKENS_PACKAGE = '@insolvia-ai/tokens';
const COLORS_SPECIFIER = `${TOKENS_PACKAGE}/colors.json`;
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

/** The brightnesses the managed-login document carries a mapping for. */
const MODES = ['light', 'dark'] as const;
type Mode = (typeof MODES)[number];

type JsonValue = string | number | boolean | null | JsonValue[] | { [key: string]: JsonValue };
type JsonObject = { [key: string]: JsonValue };

function main(args: string[]): void {
  const check = args.includes('--check');
  const root = repoRoot();
  const file = join(root, OUT);

  const colors = loadColors();
  const current = readFileSync(file, 'utf8');
  const reconciled = reconcile(colors, current);

  if (current === reconciled) {
    process.stdout.write(`${OUT} is in sync with ${TOKENS_PACKAGE}.\n`);
    return;
  }

  if (check) {
    process.stderr.write(`${OUT} has drifted from the installed ${TOKENS_PACKAGE}.\n\n`);
    process.stderr.write(
      'Either the file was hand-edited, or the tokens dependency was bumped\n' +
        'without regenerating. Run:\n\n',
    );
    process.stderr.write(`  ${REGEN_COMMAND}\n`);
    process.exitCode = 1;
    return;
  }

  writeFileSync(file, reconciled);
  process.stdout.write(`wrote ${OUT}\n`);
}

/**
 * The colour tables the design system published, as data.
 *
 * Resolved through the package's `exports` rather than by a hand-built
 * `node_modules/...` path, so a hoisting change cannot silently point this at
 * nothing — and an absent dependency fails here, loudly, naming the package.
 */
function loadColors(): Record<Mode, Record<string, string>> {
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

  const parsed = JSON.parse(readFileSync(path, 'utf8')) as JsonValue;
  const document = asObject(parsed, COLORS_SPECIFIER);

  const colors = {} as Record<Mode, Record<string, string>>;
  for (const mode of MODES) {
    const scheme = asObject(document[mode], `${COLORS_SPECIFIER}:${mode}`);
    const values: Record<string, string> = {};
    for (const [role, value] of Object.entries(scheme)) {
      if (typeof value !== 'string') {
        throw new Error(`${COLORS_SPECIFIER}: ${mode}.${role} is not a string.`);
      }
      values[role] = value;
    }
    colors[mode] = values;
  }
  return colors;
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
function reconcile(colors: Record<Mode, Record<string, string>>, current: string): string {
  const root = asObject(JSON.parse(current) as JsonValue, OUT);

  for (const mode of MODES) {
    const values = colors[mode];
    const key = mode === 'light' ? 'lightMode' : 'darkMode';
    for (const [template, role] of COGNITO_COLORS) {
      const hex = values[role];
      if (hex === undefined) {
        throw new Error(
          `${COLORS_SPECIFIER}: no such semantic role "${role}" in ${mode}. ` +
            'The tokens package dropped or renamed it — update COGNITO_COLORS.',
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

/** Resolved from the module's own location, so behaviour does not vary by cwd. */
function repoRoot(): string {
  return resolve(import.meta.dirname, '..');
}

main(process.argv.slice(2));
