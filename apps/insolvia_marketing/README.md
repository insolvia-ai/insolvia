# Insolvia marketing site

The marketing site for `www.insolvia.ai` — React Router v7 framework mode with
SSR, deployed as a Lambda container image + S3 client assets behind CloudFront
(`marketing-prod.yml`). It consumes the **published**
`@insolvia-ai/design-system` package from GitHub Packages — see *Local
design-system debugging* below before you reach for a `file:` path.

## Install

GitHub Packages requires a token even for public packages, so a plain
`npm install` 401s. The committed `.npmrc` expands `NODE_AUTH_TOKEN` from the
environment; export one first (needs the `read:packages` scope):

```sh
export NODE_AUTH_TOKEN="$(gh auth token)"
npm install
```

## Commands

```sh
npm run dev        # dev server
npm run build      # production build (build/client + build/server)
npm run typecheck  # react-router typegen + tsc
npm run lint       # eslint app server
```

## Local design-system debugging

To hack on `packages/insolvia_design_system_react` and see the result here
live, temporarily point the dep at the local package:

```sh
npm install ../../packages/insolvia_design_system_react
```

(or edit `package.json` to the `file:` path yourself). Remember the local
package's `dist/` is gitignored — build it there first.

**Never commit that state.** The committed dependency is the published
`@insolvia-ai/design-system` from GitHub Packages; a `file:` override is an
uncommitted debugging aid only (see the package table in the repo root
`CLAUDE.md`). When you're done, land the design-system change, publish a new
version, and bump the semver range here — that package.json/lockfile bump is
also what re-runs this app's PR gate against the new version.
