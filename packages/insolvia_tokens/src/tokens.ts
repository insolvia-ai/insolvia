// DO NOT EDIT — generated from packages/insolvia_tokens/tokens.json
// Regenerate with: npm run tokens

/**
 * Design tokens for the React Native app, consumed as `@insolvia-ai/tokens`
 * and used with `StyleSheet.create`.
 *
 * Plain exported consts — no CSS, no Tailwind, no runtime, no dependencies.
 *
 * Colors are the SEMANTIC layer only. The raw palette (ink/brass/paper) is
 * deliberately absent rather than merely discouraged: nothing here exports
 * it, so UI code cannot couple to it by accident and a re-brand stays a
 * one-file change.
 *
 * Derived states are pre-computed with the same sRGB blend that `theme.css`
 * expresses as `color-mix(in srgb, ...)`. The browser defers that blend and
 * React Native cannot, so it is resolved here — both stacks land on the
 * same pixel.
 */

/** A color scheme the app can render. Every one is declared in `colors`. */
export type ColorSchemeName = 'light' | 'dark';

/**
 * One complete mapping of semantic role onto an sRGB value.
 *
 * Every member is required, so a scheme that omits one — a missing
 * dark-mode value — is a compile error, never a silent fallback.
 */
export interface ColorScheme {
  /** App background "paper". */
  readonly bg: string;

  /** Raised surface behind cards, sheets, and dialogs. */
  readonly card: string;

  /** Inset/alternate surface — table stripes, wells, code blocks. */
  readonly surfaceAlt: string;

  /** Body text on [bg]. */
  readonly ink: string;

  /** The brand ink used by headers and the wordmark. */
  readonly brand: string;

  /** Primary action color (filled CTAs, focus, selection). */
  readonly primary: string;

  /** Text/icon color that sits on top of [primary]. */
  readonly primaryText: string;

  /** Warm brass accent (highlights, focus, key CTAs). */
  readonly accent: string;

  /** De-emphasized text (captions, metadata). */
  readonly muted: string;

  /** Thin divider/border color. */
  readonly line: string;

  /** Success/confirmed state. */
  readonly success: string;

  /** Warning/attention state. */
  readonly warning: string;

  /** Error/destructive state. */
  readonly danger: string;

  /** Hovered [primary]. */
  readonly primaryHover: string;

  /** Pressed [primary]. */
  readonly primaryActive: string;

  /** Hovered [accent]. */
  readonly accentHover: string;

  /** Hovered [danger]. */
  readonly dangerHover: string;
}

/** The semantic color vocabulary — the only color names UI code speaks. */
export type SemanticColorName = keyof ColorScheme;

/** Every semantic color, per scheme. */
export const colors = {
  /** The light-mode mapping of palette onto semantics. */
  light: {
    bg: '#FAF9F6',
    card: '#FFFFFF',
    surfaceAlt: '#EFEDE6',
    ink: '#141A1F',
    brand: '#0B2A4A',
    primary: '#0B2A4A',
    primaryText: '#FFFFFF',
    accent: '#B8863B',
    muted: '#5A6672',
    line: '#C9D0D8',
    success: '#2E7D5B',
    warning: '#B8863B',
    danger: '#B3352E',
    primaryHover: '#0A2541',
    primaryActive: '#09223B',
    accentHover: '#A27634',
    dangerHover: '#9E2F28',
  },

  /** The dark-mode mapping of palette onto semantics. */
  dark: {
    bg: '#071B31',
    card: '#141A1F',
    surfaceAlt: '#0B2A4A',
    ink: '#FAF9F6',
    brand: '#FFFFFF',
    primary: '#D2A857',
    primaryText: '#071B31',
    accent: '#D2A857',
    muted: '#C9D0D8',
    line: '#FFFFFF33',
    success: '#2E7D5B',
    warning: '#D2A857',
    danger: '#B3352E',
    primaryHover: '#D7B26B',
    primaryActive: '#B08D49',
    accentHover: '#D7B26B',
    dangerHover: '#BC4D47',
  },
} as const satisfies Record<ColorSchemeName, ColorScheme>;

/**
 * Exhaustiveness guard, in the spirit of the no-default-arm switches in
 * `apps/insolvia_app/src/config/environment.ts`: naming a scheme in
 * `ColorSchemeName` without declaring its colors above stops compiling
 * rather than resolving to `undefined` at runtime.
 */
type DeclaredScheme = keyof typeof colors;
type SchemesExhaustive = [Exclude<ColorSchemeName, DeclaredScheme>] extends [never] ? true : never;
const _schemesAreExhaustive: SchemesExhaustive = true;

/**
 * Spacing scale — a 4pt base grid. Use these instead of magic numbers so
 * layout rhythm stays consistent across every Insolvia surface.
 */
export interface Spacing {
  /** 4 — tightest gutter (icon to label). */
  readonly xs: number;

  /** 8 — inner padding of compact controls. */
  readonly sm: number;

  /** 16 — the default gutter. */
  readonly md: number;

  /** 24 — separation between related blocks. */
  readonly lg: number;

  /** 32 — separation between sections. */
  readonly xl: number;

  /** 48 — page-level breathing room. */
  readonly xxl: number;
}

/** A step on the spacing scale. */
export type SpacingStep = keyof Spacing;

export const spacing = {
  xs: 4,
  sm: 8,
  md: 16,
  lg: 24,
  xl: 32,
  xxl: 48,
} as const satisfies Spacing;

/**
 * Corner-radius scale, in density-independent pixels — the unit React
 * Native's `borderRadius` already speaks.
 */
export interface Radii {
  /** 6 — chips, inputs, small controls. */
  readonly sm: number;

  /** 10 — buttons and the default control radius. */
  readonly md: number;

  /** 16 — cards and raised surfaces. */
  readonly lg: number;

  /** 999 — fully rounded (pills, avatars). */
  readonly pill: number;
}

/** A step on the corner-radius scale. */
export type RadiusName = keyof Radii;

export const radii = {
  sm: 6,
  md: 10,
  lg: 16,
  pill: 999,
} as const satisfies Radii;

/**
 * Type families, as authored: the same CSS family stacks the web surfaces
 * use. React Native resolves a single registered family, so until the brand
 * fonts land (O4) both roles fall back to the platform UI font — which is
 * exactly where the web stacks are today.
 */
export interface Typography {
  /** Display/heading family — apps may override to brand themselves. */
  readonly heading: string;

  /** Body/UI family — apps may override to brand themselves. */
  readonly body: string;
}

/** A type role. */
export type FontRole = keyof Typography;

export const typography = {
  heading: 'ui-serif, Georgia, Cambria, serif',
  body: 'ui-sans-serif, system-ui, sans-serif',
} as const satisfies Typography;
