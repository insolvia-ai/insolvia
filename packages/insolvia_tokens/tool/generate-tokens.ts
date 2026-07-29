// Renders `packages/insolvia_tokens/tokens.json` into every consuming stack.
//
//   npm run tokens
//   npm run tokens:check
//
// `--check` regenerates in memory and exits non-zero if any committed output
// has drifted from the JSON, so CI can gate hand-edits to generated files.
//
// Deliberately dependency-free (`node:fs` + `node:path` only): at this token
// count a ~400-line script is easier to read, debug, and vendor than Style
// Dictionary plus a build pipeline. Node >=24 strips TypeScript types
// natively, so the port keeps that property at zero cost — no loader, no
// bundler, no devDependency. The price is that type-stripping cannot execute
// `enum`, `namespace`, or constructor parameter properties; `erasableSyntaxOnly`
// in `tsconfig.base.json` is what turns that into a typecheck error instead of
// a runtime one.
//
// This is a port of `tool/generate_tokens.dart`, which still exists and still
// runs its own `--check` in CI for exactly one PR. Both generators must emit
// byte-identical bytes, so wherever Dart and JavaScript disagree the Dart
// behaviour wins and the divergence is commented at the site that handles it.

import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';

const TOKENS_PATH = 'packages/insolvia_tokens/tokens.json';
const TOKENS_README = 'packages/insolvia_tokens/README.md';
const DART_REGEN_COMMAND = 'dart run packages/insolvia_tokens/tool/generate_tokens.dart';
const REGEN_COMMAND = 'npm run tokens';

// Still names the *Dart* command, deliberately — not an oversight of the port.
// These four outputs live inside `insolvia_design_system`, a version-gated
// package: rewriting one byte of their banners would drag this PR into
// `insolvia-design-system-pr` territory (own PR + version bump) for no user-
// visible gain. The banner follows the Dart outputs; it changes when they do.
const DART_BANNER =
  `// DO NOT EDIT — generated from ${TOKENS_PATH}\n` +
  `// Regenerate with: ${DART_REGEN_COMMAND}\n`;

// The banners differ, deliberately — this is not an oversight to tidy up.
//
// `theme.css` is the other output that lands inside a version-gated
// design-system package, so its bytes are expensive too. Naming the README
// rather than a command makes the banner toolchain-agnostic, which is exactly
// what let this port happen at all: re-implementing the generator in another
// language rewrote none of these bytes and needed no design-system PR.
const CSS_BANNER =
  `/* DO NOT EDIT — generated from ${TOKENS_PATH}\n` + ` * Regenerate: see ${TOKENS_README}\n */\n`;

// `src/tokens.ts` lives in this package, which is neither published nor
// version-gated, so its banner is free to name a command.
const TS_BANNER =
  `// DO NOT EDIT — generated from ${TOKENS_PATH}\n` + `// Regenerate with: ${REGEN_COMMAND}\n`;

const DART_OUT_DIR = 'packages/insolvia_design_system/lib/src/tokens';
const CSS_OUT = 'packages/insolvia_design_system_react/src/styles/theme.css';
const TS_OUT = 'packages/insolvia_tokens/src/tokens.ts';

/** The brightnesses every semantic token must declare a value for. */
const MODES = ['light', 'dark'] as const;
type Mode = (typeof MODES)[number];

function main(args: string[]): void {
  const check = args.includes('--check');
  const root = repoRoot();

  const source = readFileSync(join(root, TOKENS_PATH), 'utf8');
  assertNumbersRenderIdentically(source);
  const tokens = asObject(JSON.parse(source) as JsonValue, TOKENS_PATH);

  const outputs: Array<readonly [string, string]> = [
    [`${DART_OUT_DIR}/colors.dart`, renderColors(tokens)],
    [`${DART_OUT_DIR}/spacing.dart`, renderSpacing(tokens)],
    [`${DART_OUT_DIR}/radii.dart`, renderRadii(tokens)],
    [`${DART_OUT_DIR}/semantics.dart`, renderSemantics(tokens)],
    [CSS_OUT, renderCss(tokens)],
    [TS_OUT, renderTypeScript(tokens)],
  ];

  const drifted: string[] = [];
  for (const [path, contents] of outputs) {
    const file = join(root, path);
    const current = readIfExists(file);
    if (current === contents) continue;
    if (check) {
      drifted.push(path);
    } else {
      mkdirSync(dirname(file), { recursive: true });
      writeFileSync(file, contents);
      process.stdout.write(`wrote ${path}\n`);
    }
  }

  if (!check) {
    process.stdout.write(`Generated ${outputs.length} file(s) from ${TOKENS_PATH}.\n`);
    return;
  }

  if (drifted.length === 0) {
    process.stdout.write(`Tokens are in sync (${outputs.length} file(s) checked).\n`);
    return;
  }

  process.stderr.write(`Generated token output has drifted from ${TOKENS_PATH}:\n`);
  for (const path of drifted) {
    process.stderr.write(`  - ${path}\n`);
  }
  process.stderr.write(`\nEdit ${TOKENS_PATH} (never the generated file), then run:\n`);
  process.stderr.write(`  ${REGEN_COMMAND}\n`);
  process.exitCode = 1;
}

/**
 * The script lives at `<root>/packages/insolvia_tokens/tool/`, so the repo
 * root is three directories up — resolved from the module's own location
 * rather than the cwd so the generator behaves identically from anywhere.
 */
function repoRoot(): string {
  return resolve(import.meta.dirname, '..', '..', '..');
}

function readIfExists(file: string): string | null {
  try {
    return readFileSync(file, 'utf8');
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === 'ENOENT') return null;
    throw error;
  }
}

// ─────────────────────────── token model ───────────────────────────

type JsonValue = string | number | boolean | null | JsonValue[] | { [key: string]: JsonValue };
type JsonObject = { [key: string]: JsonValue };

/**
 * One token, as a `[name, body]` pair.
 *
 * An array rather than a `Map`/object, on purpose: emission order is part of
 * the output bytes, and JavaScript reorders integer-like keys of a plain
 * object ahead of the rest. Every token name is a non-numeric identifier
 * today, but pairs make the ordering guarantee structural instead of lucky.
 */
type TokenEntry = readonly [string, JsonObject];

function asObject(value: JsonValue | undefined, where: string): JsonObject {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) {
    throw new Error(`Expected an object at ${where}, got ${JSON.stringify(value)}`);
  }
  return value;
}

function asString(value: JsonValue | undefined, where: string): string {
  if (typeof value !== 'string') {
    throw new Error(`Expected a string at ${where}, got ${JSON.stringify(value)}`);
  }
  return value;
}

function asNumber(value: JsonValue | undefined, where: string): number {
  if (typeof value !== 'number' || !Number.isFinite(value)) {
    throw new Error(`Expected a number at ${where}, got ${JSON.stringify(value)}`);
  }
  return value;
}

/**
 * Every JSON number must render the same in Dart and in JavaScript.
 *
 * Dart's `jsonDecode` keeps the int/double distinction the *source text*
 * carries — `4` decodes to `int` and prints `4`, `4.0` decodes to `double` and
 * prints `4.0`. `JSON.parse` collapses both to the same `number`, so
 * `String(n)` would emit `4` for a JSON `4.0` and the two generators would
 * disagree by one byte in `spacing.dart` / `radii.dart`.
 *
 * The distinction is unrecoverable after parsing, so it is checked before:
 * every numeric literal in the source must already be spelled the way
 * `String(Number(...))` spells it. `4` and `4.5` pass (Dart prints them
 * identically); `4.0`, `4.00` and `1e3` are rejected with an explanation
 * rather than silently rendered wrong.
 */
function assertNumbersRenderIdentically(source: string): void {
  const withoutStrings = source.replace(/"(?:[^"\\]|\\.)*"/g, '');
  for (const match of withoutStrings.matchAll(/-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?/g)) {
    const text = match[0];
    if (text === undefined || String(Number(text)) === text) continue;
    throw new Error(
      `${TOKENS_PATH} contains the number literal \`${text}\`, which Dart and ` +
        `JavaScript render differently (Dart keeps the JSON int/double ` +
        `distinction, JavaScript does not). Write it as \`${String(Number(text))}\`.`,
    );
  }
}

/**
 * Entries whose key starts with `$` are documentation for humans reading the
 * JSON, not tokens.
 */
function group(tokens: JsonObject, name: string): TokenEntry[] {
  const raw = asObject(tokens[name], name);
  return Object.entries(raw)
    .filter(([key]) => !key.startsWith('$'))
    .map(([key, value]) => [key, asObject(value, `${name}.${key}`)] as const);
}

/** Resolves a `{palette.foo}` alias to its raw hex value. */
function resolveAlias(tokens: JsonObject, ref: string): string {
  if (!ref.startsWith('{') || !ref.endsWith('}')) return ref;
  let node: JsonValue = tokens;
  for (const part of ref.slice(1, -1).split('.')) {
    // Annotated, not inferred: `node` is reassigned from `next` at the end of
    // the loop body, so inference here would be circular (TS7022).
    const next: JsonValue | undefined = asObject(node, ref)[part];
    if (next === undefined || next === null) {
      throw new Error(`Unresolvable token alias: ${ref}`);
    }
    node = next;
  }
  return asString(asObject(node, ref)['value'], `${ref}.value`);
}

type Rgba = readonly [number, number, number, number];

/** `#RRGGBB` or `#RRGGBBAA` → `[r, g, b, a]`, each 0-255. */
function parseHex(hex: string): Rgba {
  const body = hex.replace('#', '');
  // Dart's `int.parse` throws on anything non-hex; `parseInt` would happily
  // stop at the first bad character and return a plausible-looking number, so
  // the shape is validated up front instead.
  if ((body.length !== 6 && body.length !== 8) || !/^[0-9a-fA-F]+$/.test(body)) {
    throw new Error(`Expected #RRGGBB or #RRGGBBAA, got "${hex}"`);
  }
  return [
    parseInt(body.slice(0, 2), 16),
    parseInt(body.slice(2, 4), 16),
    parseInt(body.slice(4, 6), 16),
    body.length === 8 ? parseInt(body.slice(6, 8), 16) : 255,
  ];
}

/**
 * Dart's `num.round()` breaks ties AWAY FROM ZERO; `Math.round` breaks them
 * toward +Infinity, so they disagree on every negative half (`(-0.5).round()`
 * is `-1` in Dart, `Math.round(-0.5)` is `-0`). Channel blends are never
 * negative today, but this function is what makes three stacks agree on a
 * pixel — it reproduces Dart's rule rather than relying on that staying true.
 */
function dartRound(value: number): number {
  return value < 0 ? -Math.round(-value) : Math.round(value);
}

/**
 * The sRGB blend CSS `color-mix(in srgb, <base> <100-amount>%, <mix> <amount>%)`
 * produces, pre-computed so the Dart and React Native sides match the browser
 * exactly.
 */
function mix(baseHex: string, mixName: string, amount: number): string {
  const [br, bg, bb, ba] = parseHex(baseHex);
  const [mr, mg, mb, ma] =
    mixName === 'white' ? ([255, 255, 255, 255] as const) : ([0, 0, 0, 255] as const);
  const t = amount / 100;
  const out: Rgba = [
    dartRound(br * (1 - t) + mr * t),
    dartRound(bg * (1 - t) + mg * t),
    dartRound(bb * (1 - t) + mb * t),
    dartRound(ba * (1 - t) + ma * t),
  ];
  const hex = out.map((channel) => channel.toString(16).padStart(2, '0')).join('');
  return `#${hex.slice(0, 6).toUpperCase()}${out[3] === 255 ? '' : hex.slice(6).toUpperCase()}`;
}

/** Resolves every semantic + derived token for one brightness. */
function valuesFor(tokens: JsonObject, mode: Mode): Map<string, string> {
  const resolved = new Map<string, string>();
  for (const [name, body] of group(tokens, 'semantic')) {
    resolved.set(name, resolveAlias(tokens, asString(body[mode], `semantic.${name}.${mode}`)));
  }
  for (const [name, body] of group(tokens, 'semanticDerived')) {
    const from = asString(body['from'], `semanticDerived.${name}.from`);
    const base = resolved.get(from);
    if (base === undefined) {
      throw new Error(`semanticDerived.${name} derives from unknown semantic "${from}"`);
    }
    const spec = asObject(body[mode], `semanticDerived.${name}.${mode}`);
    resolved.set(
      name,
      mix(
        base,
        asString(spec['mix'], `semanticDerived.${name}.${mode}.mix`),
        asNumber(spec['amount'], `semanticDerived.${name}.${mode}.amount`),
      ),
    );
  }
  return resolved;
}

function lookup(values: Map<string, string>, name: string): string {
  const value = values.get(name);
  if (value === undefined) throw new Error(`No resolved value for "${name}"`);
  return value;
}

// ─────────────────────────── output buffer ───────────────────────────

/** The `StringBuffer` the Dart generator builds each output with. */
class Out {
  #parts: string[] = [];

  write(text: string): void {
    this.#parts.push(text);
  }

  writeln(text = ''): void {
    this.#parts.push(text, '\n');
  }

  toString(): string {
    return this.#parts.join('');
  }
}

// ─────────────────────────── Dart output ───────────────────────────

function dartColor(hex: string): string {
  const [r, g, b, a] = parseHex(hex);
  // `<<` in JavaScript is a 32-bit SIGNED operation: `255 << 24` is
  // -16777216, whose `toString(16)` is "-1000000". Dart's ints are 64-bit and
  // give 4278190080. `>>> 0` reinterprets the result as unsigned, which is
  // what reproduces Dart here.
  const argb = (((a << 24) | (r << 16) | (g << 8) | b) >>> 0).toString(16).toUpperCase();
  return `Color(0x${argb.padStart(8, '0')})`;
}

function doc(description: string, indent = '  '): string {
  return `${indent}/// ${description}\n`;
}

function renderColors(tokens: JsonObject): string {
  const out = new Out();
  out.write(DART_BANNER);
  out.writeln();
  out.writeln("import 'package:flutter/material.dart';");
  out.writeln();
  out.writeln('/// Raw brand color values — the primitive layer of the palette.');
  out.writeln('///');
  out.writeln('/// UI code should not read these directly; go through');
  out.writeln('/// [InsolviaSemanticColors] or the theme so that light/dark and');
  out.writeln('/// future re-branding stay a single-file change.');
  out.writeln('abstract final class InsolviaPalette {');
  out.writeln('  const InsolviaPalette._();');

  for (const [name, body] of group(tokens, 'palette')) {
    out.writeln();
    out.write(doc(asString(body['description'], `palette.${name}.description`)));
    out.writeln(
      `  static const Color ${name} = ` +
        `${dartColor(asString(body['value'], `palette.${name}.value`))};`,
    );
  }

  out.writeln('}');
  return out.toString();
}

function renderSpacing(tokens: JsonObject): string {
  const out = new Out();
  out.write(DART_BANNER);
  out.writeln();
  out.writeln('/// Spacing scale — a 4pt base grid. Use these instead of magic');
  out.writeln('/// numbers so layout rhythm stays consistent across every');
  out.writeln('/// Insolvia surface.');
  out.writeln('abstract final class InsolviaSpacing {');
  out.writeln('  const InsolviaSpacing._();');

  for (const [name, body] of group(tokens, 'spacing')) {
    out.writeln();
    out.write(doc(asString(body['description'], `spacing.${name}.description`)));
    out.writeln(
      `  static const double ${name} = ` + `${asNumber(body['value'], `spacing.${name}.value`)};`,
    );
  }

  out.writeln('}');
  return out.toString();
}

function renderRadii(tokens: JsonObject): string {
  const radii = group(tokens, 'radii');
  const out = new Out();
  out.write(DART_BANNER);
  out.writeln();
  out.writeln("import 'package:flutter/widgets.dart';");
  out.writeln();
  out.writeln('/// Corner-radius scale for Insolvia surfaces and controls.');
  out.writeln('abstract final class InsolviaRadii {');
  out.writeln('  const InsolviaRadii._();');

  for (const [name, body] of radii) {
    out.writeln();
    out.write(doc(asString(body['description'], `radii.${name}.description`)));
    out.writeln(
      `  static const double ${name} = ` + `${asNumber(body['value'], `radii.${name}.value`)};`,
    );
  }

  for (const [name, body] of radii) {
    if (body['borderRadius'] !== true) continue;
    out.writeln();
    out.write(doc(`[${name}] as a [BorderRadius] on all four corners.`));
    out.writeln(
      `  static const BorderRadius ${name}All = ` + `BorderRadius.all(Radius.circular(${name}));`,
    );
  }

  out.writeln('}');
  return out.toString();
}

function renderSemantics(tokens: JsonObject): string {
  const semantic = group(tokens, 'semantic');
  const derived = group(tokens, 'semanticDerived');
  const entries = [...semantic, ...derived];

  const out = new Out();
  out.write(DART_BANNER);
  out.writeln();
  out.writeln("import 'package:flutter/material.dart';");
  out.writeln();
  out.writeln("import 'colors.dart';");
  out.writeln();
  out.writeln('/// The semantic layer: the only color vocabulary UI code and');
  out.writeln('/// themes should speak.');
  out.writeln('///');
  out.writeln('/// Raw [InsolviaPalette] entries map onto these roles, and a');
  out.writeln('/// re-brand swaps the mapping rather than every call site. This');
  out.writeln('/// mirrors the CSS custom properties generated into');
  out.writeln('/// `insolvia_design_system_react`, so both stacks stay one');
  out.writeln('/// vocabulary.');
  out.writeln('@immutable');
  out.writeln('class InsolviaSemanticColors {');
  out.writeln('  const InsolviaSemanticColors({');
  for (const [name] of entries) {
    out.writeln(`    required this.${name},`);
  }
  out.writeln('  });');

  for (const [name, body] of entries) {
    out.writeln();
    out.write(doc(asString(body['description'], `${name}.description`)));
    out.writeln(`  final Color ${name};`);
  }

  for (const mode of MODES) {
    const values = valuesFor(tokens, mode);
    out.writeln();
    out.write(doc(`The ${mode}-mode mapping of palette onto semantics.`));
    out.writeln(`  static const InsolviaSemanticColors ${mode} = ` + 'InsolviaSemanticColors(');
    for (const [name] of entries) {
      out.writeln(`    ${name}: ${dartColor(lookup(values, name))},`);
    }
    out.writeln('  );');
  }

  out.writeln('}');
  return out.toString();
}

// ─────────────────────────── CSS output ───────────────────────────

function kebab(name: string): string {
  return name.replace(/[A-Z]/g, (letter) => `-${letter.toLowerCase()}`).replace(/-+/g, '-');
}

/**
 * Opaque colors stay hex; translucent ones become `rgb(r g b / a)`, which is
 * how the reference design system authors alpha.
 */
function cssColor(hex: string): string {
  const [r, g, b, a] = parseHex(hex);
  const rgb = hex.replace('#', '').slice(0, 6).toLowerCase();
  if (a === 255) {
    return `#${rgb}`;
  }
  return `rgb(${r} ${g} ${b} / ${trim(a / 255)})`;
}

function renderCss(tokens: JsonObject): string {
  const semantic = group(tokens, 'semantic');
  const derived = group(tokens, 'semanticDerived');
  const out = new Out();
  out.write(CSS_BANNER);
  out.writeln(`/*
 * Tailwind v4 design tokens for Insolvia's React/web surfaces.
 *
 * Consuming apps import this from their Tailwind v4 CSS entrypoint:
 *
 *   @import "tailwindcss";
 *   @import "@insolvia-ai/design-system/theme.css";
 *
 * Dark mode is toggled by setting \`data-theme="dark"\` on a root element
 * (e.g. <html data-theme="dark">) — every component reads the SEMANTIC
 * tokens below, so nothing in component source changes per theme.
 *
 * Brand yourself by overriding the semantic variables after importing this
 * file. Never depend on raw palette names (ink/brass/paper): they are an
 * implementation detail of the default mapping and are not emitted here.
 */`);
  out.writeln();
  out.writeln('@theme {');

  const section = (comment: string): void => {
    out.writeln();
    out.writeln(`  /* ${comment} */`);
  };

  section('Typography. Apps can override these to apply their own brand.');
  for (const [name, body] of group(tokens, 'fonts')) {
    out.writeln(`  --font-${kebab(name)}: ${asString(body['value'], `fonts.${name}.value`)};`);
  }

  section('Semantic colors — the light-mode default mapping.');
  for (const [name, body] of semantic) {
    const value = resolveAlias(tokens, asString(body['light'], `semantic.${name}.light`));
    out.writeln(`  --color-${kebab(name)}: ${cssColor(value)};`);
  }

  section('Derived states. Overriding the base semantic moves these too.');
  for (const [name, body] of derived) {
    const spec = asObject(body['light'], `semanticDerived.${name}.light`);
    const from = asString(body['from'], `semanticDerived.${name}.from`);
    out.writeln(`  --color-${kebab(name)}: ${colorMix(from, spec, name)};`);
  }

  section('Spacing — a 4pt base grid.');
  for (const [name, body] of group(tokens, 'spacing')) {
    const rem = asNumber(body['value'], `spacing.${name}.value`) / 16;
    out.writeln(`  --spacing-${kebab(name)}: ${trim(rem)}rem;`);
  }

  section('Corner radii.');
  for (const [name, body] of group(tokens, 'radii')) {
    out.writeln(`  --radius-${kebab(name)}: ${asString(body['css'], `radii.${name}.css`)};`);
  }

  section('Elevation.');
  for (const [name, body] of group(tokens, 'shadows')) {
    out.writeln(`  --shadow-${kebab(name)}: ${asString(body['value'], `shadows.${name}.value`)};`);
  }

  out.writeln('}');
  out.writeln();
  out.writeln("[data-theme='dark'] {");

  for (const [name, body] of semantic) {
    const value = resolveAlias(tokens, asString(body['dark'], `semantic.${name}.dark`));
    out.writeln(`  --color-${kebab(name)}: ${cssColor(value)};`);
  }
  out.writeln();
  for (const [name, body] of derived) {
    const spec = asObject(body['dark'], `semanticDerived.${name}.dark`);
    const from = asString(body['from'], `semanticDerived.${name}.from`);
    out.writeln(`  --color-${kebab(name)}: ${colorMix(from, spec, name)};`);
  }

  out.writeln('}');
  return out.toString();
}

function colorMix(from: string, spec: JsonObject, where: string): string {
  const amount = asNumber(spec['amount'], `semanticDerived.${where}.amount`);
  const mixName = asString(spec['mix'], `semanticDerived.${where}.mix`);
  return (
    `color-mix(in srgb, var(--color-${kebab(from)}) ${100 - amount}%, ` + `${mixName} ${amount}%)`
  );
}

function trim(value: number): string {
  // `toFixed` and Dart's `toStringAsFixed` agree here, ties included: both
  // round half away from zero for a positive value (0.03125 → "0.0313").
  const text = value.toFixed(4);
  return text.includes('.') ? text.replace(/0+$/, '').replace(/\.$/, '') : text;
}

// ─────────────────────────── TypeScript output ───────────────────────────

/**
 * Renders a JavaScript string literal the way Prettier would.
 *
 * `src/tokens.ts` sits inside the `prettier --check` target, so the generator
 * has to agree with Prettier byte for byte — including its quote choice, which
 * is whichever quote needs fewer escapes (single on a tie).
 */
function jsString(value: string): string {
  const singles = (value.match(/'/g) ?? []).length;
  const doubles = (value.match(/"/g) ?? []).length;
  const quote = singles > doubles ? '"' : "'";
  const escaped = value.replace(/\\/g, '\\\\').split(quote).join(`\\${quote}`);
  return `${quote}${escaped}${quote}`;
}

function tsDoc(description: string, indent = '  '): string {
  if (description.includes('*/')) {
    throw new Error(`Token description cannot contain "*/": ${description}`);
  }
  return `${indent}/** ${description} */\n`;
}

/**
 * The value the React Native app consumes: `#RRGGBB`, or `#RRGGBBAA` when the
 * token carries alpha. Re-rendered from the parsed channels rather than passed
 * through, so an alias authored in lowercase and a `mix()` result cannot end
 * up in the same file spelled two different ways.
 */
function rnColor(hex: string): string {
  const [r, g, b, a] = parseHex(hex);
  const channels = [r, g, b, ...(a === 255 ? [] : [a])];
  return `#${channels
    .map((c) => c.toString(16).padStart(2, '0'))
    .join('')
    .toUpperCase()}`;
}

function renderTypeScript(tokens: JsonObject): string {
  const semantic = group(tokens, 'semantic');
  const derived = group(tokens, 'semanticDerived');
  const entries = [...semantic, ...derived];

  const out = new Out();
  out.write(TS_BANNER);
  out.writeln();
  out.writeln('/**');
  out.writeln(' * Design tokens for the React Native app, consumed as `@insolvia-ai/tokens`');
  out.writeln(' * and used with `StyleSheet.create`.');
  out.writeln(' *');
  out.writeln(' * Plain exported consts — no CSS, no Tailwind, no runtime, no dependencies.');
  out.writeln(' *');
  out.writeln(' * Colors are the SEMANTIC layer only. The raw palette (ink/brass/paper) is');
  out.writeln(' * deliberately absent rather than merely discouraged: nothing here exports');
  out.writeln(' * it, so UI code cannot couple to it by accident and a re-brand stays a');
  out.writeln(' * one-file change.');
  out.writeln(' *');
  out.writeln(' * Derived states are pre-computed with the same sRGB blend that `theme.css`');
  out.writeln(' * expresses as `color-mix(in srgb, ...)` and that `InsolviaSemanticColors`');
  out.writeln(' * bakes into Dart, so all three stacks land on the same pixel.');
  out.writeln(' */');
  out.writeln();

  out.writeln('/** A color scheme the app can render. Every one is declared in `colors`. */');
  out.writeln(`export type ColorSchemeName = ${MODES.map(jsString).join(' | ')};`);
  out.writeln();

  out.writeln('/**');
  out.writeln(' * One complete mapping of semantic role onto an sRGB value.');
  out.writeln(' *');
  out.writeln(' * Every member is required, so a scheme that omits one — a missing');
  out.writeln(' * dark-mode value — is a compile error, never a silent fallback.');
  out.writeln(' */');
  out.writeln('export interface ColorScheme {');
  let first = true;
  for (const [name, body] of entries) {
    if (!first) out.writeln();
    first = false;
    out.write(tsDoc(asString(body['description'], `${name}.description`)));
    out.writeln(`  readonly ${name}: string;`);
  }
  out.writeln('}');
  out.writeln();

  out.writeln('/** The semantic color vocabulary — the only color names UI code speaks. */');
  out.writeln('export type SemanticColorName = keyof ColorScheme;');
  out.writeln();

  out.writeln('/** Every semantic color, per scheme. */');
  out.writeln('export const colors = {');
  for (const mode of MODES) {
    const values = valuesFor(tokens, mode);
    if (mode !== MODES[0]) out.writeln();
    out.write(tsDoc(`The ${mode}-mode mapping of palette onto semantics.`));
    out.writeln(`  ${mode}: {`);
    for (const [name] of entries) {
      out.writeln(`    ${name}: ${jsString(rnColor(lookup(values, name)))},`);
    }
    out.writeln('  },');
  }
  out.writeln('} as const satisfies Record<ColorSchemeName, ColorScheme>;');
  out.writeln();

  out.writeln('/**');
  out.writeln(' * Exhaustiveness guard, in the spirit of the no-default-arm switches in');
  out.writeln(' * `apps/insolvia_app/lib/config/environment.dart`: naming a scheme in');
  out.writeln(' * `ColorSchemeName` without declaring its colors above stops compiling');
  out.writeln(' * rather than resolving to `undefined` at runtime.');
  out.writeln(' */');
  // `DeclaredScheme` is not just a readability alias: it is what keeps the
  // conditional type below inside Prettier's 100-column budget on one line.
  // `src/tokens.ts` is inside the `prettier --check` target, so a line Prettier
  // would re-wrap is a CI failure on a file no human wrote.
  //
  // The tuple wrappers are load-bearing too: a bare `Exclude<…> extends never`
  // distributes over `never` and resolves to `never`, which would make the
  // guard fail even when it should pass.
  out.writeln('type DeclaredScheme = keyof typeof colors;');
  out.writeln(
    'type SchemesExhaustive = [Exclude<ColorSchemeName, DeclaredScheme>] extends [never] ? true : never;',
  );
  out.writeln('const _schemesAreExhaustive: SchemesExhaustive = true;');
  out.writeln();

  renderTsRecord(out, tokens, {
    group: 'spacing',
    interfaceName: 'Spacing',
    keyType: 'SpacingStep',
    constName: 'spacing',
    valueType: 'number',
    interfaceDoc: [
      'Spacing scale — a 4pt base grid. Use these instead of magic numbers so',
      'layout rhythm stays consistent across every Insolvia surface.',
    ],
    keyDoc: 'A step on the spacing scale.',
    value: (body, where) => String(asNumber(body['value'], where)),
  });
  out.writeln();

  renderTsRecord(out, tokens, {
    group: 'radii',
    interfaceName: 'Radii',
    keyType: 'RadiusName',
    constName: 'radii',
    valueType: 'number',
    interfaceDoc: [
      'Corner-radius scale, in density-independent pixels — the unit React',
      "Native's `borderRadius` already speaks.",
    ],
    keyDoc: 'A step on the corner-radius scale.',
    value: (body, where) => String(asNumber(body['value'], where)),
  });
  out.writeln();

  renderTsRecord(out, tokens, {
    group: 'fonts',
    interfaceName: 'Typography',
    keyType: 'FontRole',
    constName: 'typography',
    valueType: 'string',
    interfaceDoc: [
      'Type families, as authored: the same CSS family stacks the web surfaces',
      'use. React Native resolves a single registered family, so until the brand',
      'fonts land (O4) both roles fall back to the platform UI font — which is',
      'exactly where the web stacks are today.',
    ],
    keyDoc: 'A type role.',
    value: (body, where) => jsString(asString(body['value'], where)),
  });

  return out.toString();
}

/** Shared shape for the non-color token groups: interface, key type, const. */
function renderTsRecord(
  out: Out,
  tokens: JsonObject,
  spec: {
    group: string;
    interfaceName: string;
    keyType: string;
    constName: string;
    valueType: string;
    interfaceDoc: string[];
    keyDoc: string;
    value: (body: JsonObject, where: string) => string;
  },
): void {
  const entries = group(tokens, spec.group);

  out.writeln('/**');
  for (const line of spec.interfaceDoc) {
    out.writeln(` * ${line}`);
  }
  out.writeln(' */');
  out.writeln(`export interface ${spec.interfaceName} {`);
  let first = true;
  for (const [name, body] of entries) {
    if (!first) out.writeln();
    first = false;
    out.write(tsDoc(asString(body['description'], `${spec.group}.${name}.description`)));
    out.writeln(`  readonly ${name}: ${spec.valueType};`);
  }
  out.writeln('}');
  out.writeln();

  out.writeln(`/** ${spec.keyDoc} */`);
  out.writeln(`export type ${spec.keyType} = keyof ${spec.interfaceName};`);
  out.writeln();

  out.writeln(`export const ${spec.constName} = {`);
  for (const [name, body] of entries) {
    out.writeln(`  ${name}: ${spec.value(body, `${spec.group}.${name}.value`)},`);
  }
  out.writeln(`} as const satisfies ${spec.interfaceName};`);
}

main(process.argv.slice(2));
