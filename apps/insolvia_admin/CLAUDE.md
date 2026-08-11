# apps/insolvia_admin — agent rules

The internal staff portal (#214): a Vite + React SPA, deliberately NOT an
Expo app — it is web-only forever, so ADR 0004's cross-platform machinery
does not apply. Human docs: [`README.md`](README.md).

- **Outside the npm workspace, own lockfile** — the same posture and reason
  as `apps/insolvia_marketing` (root `package.json`'s comment owns it).
  Never add it to `workspaces`; never point the design system at a local
  checkout (ADR 0010).
- **Design system `.web` leaves via `resolve.extensions`** (Vite) and
  `moduleSuffixes` (tsc) — both copied from marketing, plus one of our own:
  `optimizeDeps.exclude` for the package, because the DEV pre-bundler
  ignores `resolve.extensions`. The CI build greps the bundle for
  `react-native` — the platform split failing shows up there.
- **Sign-in is Google Workspace, PKCE, memory-only tokens** (#209,
  ADR 0011): no password form ever, no refresh token anywhere, tokens die
  with the tab, expiry means re-authenticate. `sessionStorage` holds ONLY
  the redirect handshake (state/verifier/returnTo). The client ids and API
  hosts are a committed map in `src/config/environment.ts` — all public
  values; the build injects exactly one variable, `VITE_INSOLVIA_ENV`.
- **The dev server is PINNED to :3100** (`strictPort`): Google's redirect
  URIs are exact-match and the app owns :3000. Local loop:
  `./services/admin/scripts/dev-up.sh` (service on :8090) +
  `./apps/insolvia_admin/scripts/dev-up.sh` here + your real Workspace
  account — or both at once via the root `./scripts/dev-up.sh`.
- **`RequireStaff` is a courtesy, never a control** — the admin service
  verifies every request; the guard just routes to sign-in.
- Tests are Vitest: contract pins against literal admin-service JSON, and
  the OAuth URL assertions that are checkable nowhere else (PKCE sent, no
  offline scope). Follow the `insolvia-testing` skill.
