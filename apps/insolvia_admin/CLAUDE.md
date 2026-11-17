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
- **Sign-in is Google Workspace via Google Identity Services, memory-only
  tokens** (#209, ADR 0011): Google's own button
  (`src/session/google-identity.ts`) hands the session an ID token in-page —
  no password form ever, no refresh token anywhere, no redirect, and since
  the GIS move NOTHING in any storage at all; tokens die with the tab and
  expiry means re-authenticate. **Not PKCE, and that is not a preference:**
  Google web clients demand the client secret at the token endpoint even
  with PKCE (`client_secret is missing.`), so the original redirect flow
  could never complete — google-identity.ts's header owns the story. Each
  environment's portal origin must be in the OAuth client's **Authorized
  JavaScript origins**. The client ids and API hosts are a committed map in
  `src/config/environment.ts` — all public values; the build injects exactly
  one variable, `VITE_INSOLVIA_ENV`.
- **The dev server is PINNED to :3100** (`strictPort`): Google's JavaScript
  origins are exact-match and the app owns :3000. Local loop:
  `./services/admin/scripts/dev-up.sh` (service on :8090) +
  `./apps/insolvia_admin/scripts/dev-up.sh` here + your real Workspace
  account — or both at once via the root `./scripts/dev-up.sh`.
- **`RequireStaff` is a courtesy, never a control** — the admin service
  verifies every request; the guard renders the sign-in screen in place (no
  redirect leaves the page, so there is no return-to to remember).
- Tests are Vitest: contract pins against literal admin-service JSON, and
  the sign-in assertions that are checkable nowhere else (this environment's
  client id, the `hd` hint, no `auto_select`). Follow the `insolvia-testing`
  skill.
