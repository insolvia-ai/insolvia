import { defineConfig } from 'vitest/config';

// The resolve.extensions list is this file's whole reason to exist: it teaches
// Vite the same web-first leaf resolution the tsconfig's `moduleSuffixes` gives
// tsc, so an extensionless './button' lands on button.web.tsx here exactly as
// it does in marketing's Vite build. The tests exercise the WEB leaves only —
// the .native leaves are typechecked by tsconfig.native.json and rendered by
// the app, which owns its own jest-expo harness.
export default defineConfig({
  resolve: {
    extensions: ['.web.tsx', '.web.ts', '.mjs', '.js', '.ts', '.jsx', '.tsx', '.json'],
  },
  test: {
    environment: 'jsdom',
    // `globals` is not for the tests (they import from 'vitest' explicitly) —
    // it is what lets Testing Library register its afterEach auto-cleanup;
    // without it every render leaks into the next test's DOM.
    globals: true,
    setupFiles: ['./vitest.setup.ts'],
  },
});
