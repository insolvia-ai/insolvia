import { reactRouter } from "@react-router/dev/vite";
import tailwindcss from "@tailwindcss/vite";
import { defineConfig } from "vite";

export default defineConfig(({ command }) => ({
  plugins: [tailwindcss(), reactRouter()],
  // Vite 8 resolves tsconfig `paths` natively.
  resolve: { tsconfigPaths: true },
  ssr: {
    // Only for the production build: bundle the design system (the published
    // @insolvia-ai/design-system from GitHub Packages) and its UI dependency
    // subtree into the server build, so the runtime Lambda image needs neither
    // registry auth nor these transitive packages installed. In dev they stay
    // external and load normally from node_modules (bundling CJS deps like
    // use-sync-external-store breaks Vite's dev SSR module runner).
    noExternal:
      command === "build"
        ? [
            "@insolvia-ai/design-system",
            /^@base-ui\//,
            /^@floating-ui\//,
            "@babel/runtime",
            "use-sync-external-store",
            "clsx",
            "tailwind-merge",
          ]
        : [],
  },
}));
