import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";
// vitest/config, not vite: the `test` block below is Vitest's extension of
// the Vite config type, invisible to plain `vite`'s defineConfig.
import { defineConfig } from "vitest/config";

export default defineConfig({
  plugins: [tailwindcss(), react()],
  resolve: {
    // Platform-split resolution, copied from apps/insolvia_marketing's config
    // (whose comment owns the full argument): @insolvia-ai/design-system
    // publishes SOURCE with extensionless platform-split leaf imports, and
    // `.web.tsx` first is what keeps react-native out of this bundle.
    extensions: [
      ".web.tsx",
      ".web.ts",
      ".web.jsx",
      ".web.js",
      ".mjs",
      ".js",
      ".mts",
      ".ts",
      ".jsx",
      ".tsx",
      ".json",
    ],
  },
  optimizeDeps: {
    // The DEV dependency pre-bundler resolves node_modules imports itself and
    // does NOT apply resolve.extensions — so the design system's extensionless
    // `.web` leaf imports fail there (42 UNRESOLVED_IMPORTs at dev-server
    // start) while the production build, which uses the full pipeline, is
    // fine. Excluding the package routes it through the normal transform
    // pipeline in dev too. Its two runtime deps stay optimizable as usual.
    exclude: ["@insolvia-ai/design-system"],
  },
  server: {
    // PINNED: Google's OAuth redirect URIs are exact-match and the dev client
    // registers http://localhost:3100/auth/callback. strictPort makes a taken
    // port a hard error instead of a silent hop to a port Google refuses.
    port: 3100,
    strictPort: true,
  },
  // Vitest configuration lives here rather than a separate file so the
  // resolve.extensions above apply to tests too — a test importing a
  // design-system component must resolve the same .web leaf the bundle does.
  test: {
    environment: "jsdom",
    setupFiles: ["./src/test-setup.ts"],
    globals: false,
  },
});
