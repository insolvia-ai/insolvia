# Publishing and consuming the design system

**One published package, one rule: consumers install a published, versioned
artifact — never the source by path.**

| Target | Package | Published as |
|---|---|---|
| React (marketing) | `packages/insolvia_design_system_react/` | npm package `@insolvia-ai/design-system` on GitHub Packages |

It publishes automatically on merge to `main`, the publish is idempotent by
version, and the PR gate machine-enforces the corollary: **any change to the
package bumps its version in the same PR**, because an unbumped merge publishes
nothing and the published surface silently rots underneath its consumers.

## The app's design system is not published — and shouldn't be

There are still *two* design systems (decision D4, as revised by D9): the React
one above, and the app's own React Native components inside
`apps/insolvia_app/src/components/`. They share token *values* only, generated
from one `packages/insolvia_tokens/tokens.json` — the Tailwind `@theme` CSS and
the typed `tokens.ts` come out of the same TypeScript generator and ship together
as `@insolvia-ai/tokens`. Neither generated file is ever hand-edited; `npm run
tokens:check` at the root fails CI on drift.

Nothing below applies to the app's set, deliberately. A registry boundary earns
its keep for the React package because it has an out-of-tree-shaped consumer and
a `dist` contract worth enforcing; the app's components have exactly one
consumer, in the same directory tree, so publishing them would buy a version
number and cost a release step. If a second consumer ever appears, this is the
document that describes what to do about it.

> **The Flutter half of this document is gone.** `packages/insolvia_design_system`
> was published as an annotated git tag `insolvia_design_system-v<version>` and
> consumed by the app as a version-pinned git dependency. The package, the
> `design-system-pr.yml` gate and the `design-system-publish.yml` workflow are
> all deleted under D9 — read the mechanism at `git show 17d2e37` if you need it.
> **One artefact outlives it: the tag `insolvia_design_system-v0.1.2` is still on
> the remote and is now orphaned.** Nothing resolves it, nothing publishes
> another, and it is harmless; it is left in place because deleting a published
> tag is the one irreversible step here, and a stale tag misleads nobody once
> this paragraph exists.

## The React design system: `@insolvia-ai/design-system` on GitHub Packages

`packages/insolvia_design_system_react/` is published as the npm package
**`@insolvia-ai/design-system`** to **GitHub Packages**
(`https://npm.pkg.github.com`), not to npmjs.org.

**The scope is a contract with the registry.** GitHub Packages only accepts an
npm scope equal to the owning org's login — `insolvia-ai`, not `insolvia` —
and rejects any other scope with a misleading E403
(`Permission permission_denied: The requested installation does not exist.`)
that names neither the scope nor the rule. Keep it `@insolvia-ai` everywhere:
`package.json`, `.npmrc`, imports, docs.

Its consumer — the marketing site (`apps/insolvia_marketing/`, Milestone 3) —
lives in **this same repository**. It is still published and installed rather
than wired up as a path dependency, deliberately:

- **The published `dist` is the contract.** Consuming `@insolvia-ai/design-system`
  by name means the marketing site imports exactly what an outside consumer
  would — the built ESM/CJS output and `dist/theme.css`, through the `exports`
  map. A path dependency would let it reach into `src/`, and the first import of
  an unexported internal would silently become load-bearing.
- **It keeps the package out of the root npm workspace, where it must stay.**
  The root `package.json`'s member list is explicit precisely so that
  `packages/*` never globs this package in: a workspace member would be
  symlinked, marketing would build against local source, and a broken package
  would pass CI and only break after publishing. The registry boundary is what
  makes that unambiguous.
- **A second consumer costs nothing.** Nothing about the arrangement assumes one
  site.

| | |
|---|---|
| Package | `@insolvia-ai/design-system` |
| Registry | `https://npm.pkg.github.com` |
| Source | `packages/insolvia_design_system_react/` |
| Publish workflow | `.github/workflows/design-system-react-publish.yml` |
| PR gate | `.github/workflows/design-system-react-pr.yml` |

### How publishing works

The workflow runs on **push to `main`** touching the package (plus
`workflow_dispatch`). It:

1. installs with `npm ci` (Node 24, matching `engines.node`),
2. asks the registry whether `@insolvia-ai/design-system@<version>` already exists,
3. **skips cleanly** if it does — a version bump is the only thing that triggers
   an actual publish,
4. otherwise builds (`tsup`) and runs `npm publish`.

**To ship a new version: bump `version` in
`packages/insolvia_design_system_react/package.json` and merge to `main`.**
Nothing else. Every other push to `main` lands on the skip path and stays green.

#### Every package change must bump the version

The skip path in step 3 has a failure mode: a PR that changes the package but
not the version merges green, the publish no-ops, and the registry silently
goes stale — consumers keep installing an artifact that no longer matches
`main`, with no error anywhere. So the rule is: **any change under
`packages/insolvia_design_system_react/` bumps `version` in the same PR.** This
is machine-enforced by the *Require a version bump when the package changed*
step in `design-system-react-pr.yml`, which diffs the package directory against
the PR base and fails on an unchanged version (and hard-errors if it cannot
read the base `package.json`, rather than silently passing).

The flip side of publish-on-every-change: consumers — the marketing site —
**install the published package, never the source by path.** A committed
`file:` dependency bypasses the published `dist` contract above; a local
`file:` override while debugging is fine, but it never gets committed.

Auth is `NODE_AUTH_TOKEN: ${{ secrets.GITHUB_TOKEN }}` with
`permissions: { contents: read, packages: write }`. There is no PAT, no
long-lived secret, and no repository secret to rotate.

#### Publishing is not a deploy

Publishing is deliberately outside the deploy machinery — no `environment:`,
no OIDC role, no infra preconditions. Publishing an npm package touches no AWS
account, no Route53 zone and no CloudFront distribution, so nothing on the
AWS side can make a publish fail or make a published package wrong, and no
infra outage should ever block the marketing site from installing a new
design-system version.

### Consuming it (authenticating to install)

GitHub Packages requires authentication for **every** npm read, including
public packages. There is no anonymous install.

#### Local development

Create a classic PAT with the **`read:packages`** scope
(<https://github.com/settings/tokens>), then put the scope mapping in the
consuming project's `.npmrc` and keep the token in your environment:

```ini
# .npmrc — committed. Contains a variable reference, never a token.
@insolvia-ai:registry=https://npm.pkg.github.com
//npm.pkg.github.com/:_authToken=${NODE_AUTH_TOKEN}
```

```sh
export NODE_AUTH_TOKEN=ghp_xxx   # your PAT, in your shell profile — never committed
npm install @insolvia-ai/design-system
```

npm expands `${NODE_AUTH_TOKEN}` when it reads the file, so the committed
`.npmrc` is safe in a public repo. **Never write a literal token into
`.npmrc`.** This repo is public (see `CLAUDE.md`); so is anything that
consumes it via a committed config file.

Only the `@insolvia-ai` scope is redirected — `react`, `tailwindcss` and every
other dependency still resolve from the public npm registry.

#### In the consumer's CI

The marketing site is in **this** repository, so its workflows install with the
automatic `secrets.GITHUB_TOKEN` — no PAT, nothing to rotate:

```yaml
- uses: actions/setup-node@v4
  with:
    node-version: "24"
    cache: npm
- run: npm ci
  env:
    NODE_AUTH_TOKEN: ${{ secrets.GITHUB_TOKEN }}
```

The job needs `permissions: { packages: read }` (plus whatever else it does).

For a consumer in a **different** repository in the same org, the same token
works once the package grants that repo access: package → *Package settings* →
*Manage Actions access*. Only a consumer in a **different org** needs a PAT with
`read:packages` stored as a repository or environment secret. No such consumer
exists today.

#### Using it

```css
/* Tailwind v4 CSS entrypoint */
@import 'tailwindcss';
@import '@insolvia-ai/design-system/theme.css';
@source '../node_modules/@insolvia-ai/design-system/dist';
```

The `@source` line is not optional. Tailwind v4 scans your own source for class
names, but the design system's classes live in its `dist` inside `node_modules`,
which Tailwind does not scan by default. Omit it and the components render
completely unstyled — the utilities they reference are simply never generated.
This is the classic "why are my styles gone" bug (MVP_PLAN 3.2).

```tsx
import { Button, Card, Field } from '@insolvia-ai/design-system';
```

`react` and `react-dom` are peer dependencies (18 or 19).

### The `ssr.noExternal` trick — why the runtime image needs no token

**Apply this in `apps/insolvia_marketing/` when Milestone 3 scaffolds it.** It is
a well-established technique for the same problem — a private-registry
design-system dependency in an SSR build.

The marketing site is server-rendered and deployed as a **Lambda container
image**. The naive arrangement is: install `@insolvia-ai/design-system` at build
time, and let the SSR server `require`/`import` it at runtime from
`node_modules` inside the image. That drags the private-registry dependency
into the **runtime** — the image must ship `node_modules`, and any layer that
rebuilds or reinstalls at runtime needs a registry token in the deployed
artifact. A registry credential inside a running Lambda image is exactly the
kind of secret you do not want to have.

Vite's `ssr.noExternal` removes the problem. By default Vite treats
dependencies as **external** for the SSR build — it leaves the bare
`@insolvia-ai/design-system` import in the server bundle and resolves it from
`node_modules` at runtime. Marking it `noExternal` tells Vite to **bundle the
package's source into the server build instead**:

```ts
// vite.config.ts (apps/insolvia_marketing/)
import { defineConfig } from 'vite';

export default defineConfig({
  ssr: {
    // Bundle the design system INTO the SSR server build. After this, the
    // built server has no runtime dependency on @insolvia-ai/design-system, so
    // the Lambda image never needs a GitHub Packages token.
    noExternal: ['@insolvia-ai/design-system'],
  },
});
```

The consequences, which are the whole point:

- The private-registry dependency is a **build-time-only** concern. The token
  lives in the build environment (GitHub Actions), never in the deployed image.
- The runtime Lambda image can ship **without** `@insolvia-ai/design-system` in
  `node_modules` at all.
- **CSS needs no equivalent trick.** `theme.css` *is* shipped in the published
  package — `tsup` copies it to `dist/theme.css` and the `exports` map publishes
  it as `@insolvia-ai/design-system/theme.css`. `ssr.noExternal` is irrelevant to
  it, because it is never imported by JavaScript at runtime: the site's Tailwind
  entrypoint `@import`s it, Tailwind resolves that from `node_modules` while
  compiling, and the output is a plain CSS file in the client bundle. So the CSS
  is build-time-only for the same reason as the JS, but by a different
  mechanism, and nothing needs configuring.

Two things to watch:

- `noExternal` bundles the package's **published `dist`**, and `tsup` marks
  `react`, `react-dom` and `@base-ui/react` as external. Those stay external in
  the SSR bundle too — they are ordinary public-registry deps of the site, which
  is fine.
- If the site ever adds a second `@insolvia-ai/*` package, add it to the same
  `noExternal` array. A regex (`/^@insolvia-ai\//`) is accepted and saves the
  bookkeeping.

## Hacking on the design system and the marketing site together

The published version is the contract, but npm has a sanctioned, *uncommitted*
loop for this. From `apps/insolvia_marketing/`:

```sh
npm install ../../packages/insolvia_design_system_react   # writes a file: dep
```

Build the package first (`npm run build` in it) — the `exports` map points at
`dist`, so an unbuilt link resolves to nothing. **Revert it before committing:**
a committed `file:` dependency is exactly the path dependency this document
exists to forbid, and it takes the marketing site off the published `dist`
contract without any error appearing anywhere. Restore the version range and
re-run `npm ci` so the lockfile matches.

## Related

- `packages/insolvia_design_system_react/README.md` — component scope, theming,
  and the local dev loop.
- `packages/insolvia_design_system_react/.npmrc` — the scope→registry mapping
  used by this repo's own CI.
- The `insolvia-design-system-pr` skill — the same rules in agent-instruction
  form: its own PR, its own version bump.
- [`ARCHITECTURE.md`](ARCHITECTURE.md) — the monorepo shape and where these
  packages sit.
- [`MVP_PLAN.md`](MVP_PLAN.md) — decision D4 (one token source, two targets) as
  revised by D9.
