// Own config, not a re-export of the root eslint.base.js: this app lives
// OUTSIDE the npm workspace (own lockfile — see package.json's note), so the
// root's shared config and its plugins are not installable here. Same shape
// as apps/insolvia_marketing's, which is outside the workspace for the same
// reason.
import js from "@eslint/js";
import globals from "globals";
import tseslint from "typescript-eslint";

export default tseslint.config(
  { ignores: ["dist/**", "node_modules/**"] },
  js.configs.recommended,
  ...tseslint.configs.recommended,
  {
    files: ["**/*.{ts,tsx,js,mjs}"],
    languageOptions: {
      globals: { ...globals.browser, ...globals.node },
    },
    rules: {
      "@typescript-eslint/no-unused-vars": [
        "error",
        { argsIgnorePattern: "^_", varsIgnorePattern: "^_" },
      ],
    },
  },
);
