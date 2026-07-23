import { defineConfig } from 'tsup';

export default defineConfig({
  entry: ['src/index.ts'],
  format: ['esm', 'cjs'],
  dts: true,
  sourcemap: true,
  clean: true,
  splitting: false,
  treeshake: true,
  // Copies src/styles/* verbatim into dist/ — this is how the GENERATED
  // theme.css reaches consumers as `@insolvia-ai/design-system/theme.css`.
  // It is a one-way copy: nothing here ever writes back into src/styles.
  publicDir: 'src/styles',
  external: ['react', 'react-dom', '@base-ui/react'],
});
