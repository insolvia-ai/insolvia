import { configDefaults, defineConfig } from 'vitest/config';

// Two projects because the package IS two render targets, and each project's
// resolver mirrors the consumer bundler it stands in for:
//
//   web    — Vite's web-first leaf resolution, exactly as marketing builds:
//            an extensionless './button' lands on button.web.tsx.
//   native — Metro's view of the same imports: native-first extensions, plus
//            `react-native` aliased to react-native-web. That alias is not a
//            test convenience — it is the production pair the app ships on
//            web (react-native-web renders the .native leaves there), so the
//            native tests assert on the very DOM app-web users get.
//
// The extension lists play the role tsconfig's `moduleSuffixes` plays for tsc;
// see tsconfig.json / tsconfig.native.json / tsconfig.native.test.json for the
// typecheck side of the same split.

const webExtensions = ['.web.tsx', '.web.ts', '.mjs', '.js', '.ts', '.jsx', '.tsx', '.json'];
const nativeExtensions = [
  '.native.tsx',
  '.native.ts',
  '.mjs',
  '.js',
  '.ts',
  '.jsx',
  '.tsx',
  '.json',
];

// `globals` is not for the tests (they import from 'vitest' explicitly) — it
// is what lets Testing Library register its afterEach auto-cleanup; without it
// every render leaks into the next test's DOM. Same reasoning in both projects.
export default defineConfig({
  test: {
    projects: [
      {
        resolve: {
          extensions: webExtensions,
        },
        test: {
          name: 'web',
          environment: 'jsdom',
          globals: true,
          setupFiles: ['./vitest.setup.ts'],
          include: ['src/**/*.test.{ts,tsx}'],
          exclude: [...configDefaults.exclude, 'src/**/*.native.test.{ts,tsx}'],
        },
      },
      {
        resolve: {
          extensions: nativeExtensions,
          alias: { 'react-native': 'react-native-web' },
        },
        test: {
          name: 'native',
          environment: 'jsdom',
          globals: true,
          setupFiles: ['./vitest.native.setup.ts'],
          include: ['src/**/*.native.test.{ts,tsx}'],
        },
      },
    ],
  },
});
