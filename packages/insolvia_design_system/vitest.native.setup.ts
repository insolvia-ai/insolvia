// Harness for the NATIVE vitest project: the same jsdom + Testing Library rig
// as the web project, plus the one thing react-native-web needs that jsdom
// does not provide — `window.matchMedia`. react-native-web's Appearance module
// captures `window.matchMedia('(prefers-color-scheme: dark)')` ONCE at module
// load, so the mock must exist before any test module (and its react-native-web
// import) is evaluated; that is why it lives here and not inside a test. Tests
// drive the scheme through `setPrefersColorScheme`, imported from this file —
// same module registry, same instance.
import '@testing-library/jest-dom/vitest';
import { beforeEach } from 'vitest';

type SchemeListener = (event: { matches: boolean }) => void;

let prefersDark = false;
const listeners = new Set<SchemeListener>();

/** Point the mocked `prefers-color-scheme` media query at a scheme. */
export function setPrefersColorScheme(scheme: 'light' | 'dark'): void {
  prefersDark = scheme === 'dark';
  for (const listener of listeners) listener({ matches: prefersDark });
}

/**
 * The rgb triplet of a color in `#rrggbb`, `rgb()`, or `rgba()` notation.
 * react-native-web serializes token hexes into `rgba()` inline styles and
 * jsdom re-serializes them again — comparing triplets sidesteps both formats.
 */
export function rgb(color: string): [number, number, number] {
  if (color.startsWith('#')) {
    return [
      parseInt(color.slice(1, 3), 16),
      parseInt(color.slice(3, 5), 16),
      parseInt(color.slice(5, 7), 16),
    ];
  }
  const parts = color.match(/[\d.]+/g)?.map(Number) ?? [];
  return [parts[0] ?? -1, parts[1] ?? -1, parts[2] ?? -1];
}

// Every test starts light — the scheme a test sets must not leak into the next.
beforeEach(() => {
  setPrefersColorScheme('light');
});

// Only the surface Appearance actually touches: `matches` plus the legacy
// addListener/removeListener pair it subscribes with.
window.matchMedia = (media: string) =>
  ({
    get matches() {
      return prefersDark && media.includes('prefers-color-scheme: dark');
    },
    media,
    onchange: null,
    addListener: (listener: SchemeListener) => {
      listeners.add(listener);
    },
    removeListener: (listener: SchemeListener) => {
      listeners.delete(listener);
    },
    addEventListener: () => undefined,
    removeEventListener: () => undefined,
    dispatchEvent: () => false,
  }) as unknown as MediaQueryList;
