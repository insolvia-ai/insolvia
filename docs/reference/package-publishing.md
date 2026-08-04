# Publishing and consuming the design system

**One published package, one rule: a consumer outside the npm workspace
installs a published, versioned artifact — never the source by path.**

| Target | Package | Published as |
|---|---|---|
| Web + React Native | `packages/insolvia_design_system/` | npm package `@insolvia-ai/design-system` (0.2.x) on GitHub Packages |

It publishes automatically on merge to `main`, the publish is idempotent by
version, and the PR gate machine-enforces the corollary: **any change to the
package bumps its version in the same PR**, because an unbumped merge publishes
nothing and the published surface silently rots underneath its consumers.

## The package publishes source — deliberately

`@insolvia-ai/design-system` is the owned, platform-split design system. Each
component is three files: `<name>.props.ts` (the shared contract and variant
data — imports nothing platform-specific, ESLint-enforced),
`<name>.web.tsx` (plain React DOM + Tailwind) and `<name>.native.tsx` (React
Native primitives over `@insolvia-ai/tokens`). The per-component index
re-exports the extensionless `./<name>` and the **consumer's bundler picks the
leaf** — Vite resolves `.web.tsx`, Metro resolves `.native.tsx`.

That resolution step is why the package has **no build step and publishes
`src/` as-is** (`files: ["src"]`, an `exports` map pointing at `.ts`). Leaf
selection belongs to the consumer's bundler, so the `.web`/`.native` pairs
must survive into the published artifact verbatim; a package-side build
(tsup, `tsc --emit`) would collapse each pair into one compiled entry and
break the pattern. Do not "fix" this by adding a build — the package's own
`package.json` comment block carries the full reasoning. The flip side is that
every consumer must transpile TypeScript out of `node_modules`: Metro does
that natively, and marketing's Vite does it by bundling the package
(`ssr.noExternal`, below).

`src/styles/theme.css` is **generated** from
`packages/insolvia_tokens/tokens.json` — never hand-edited; `npm run
tokens:check` at the root fails CI on drift. It ships inside the published
package as `@insolvia-ai/design-system/theme.css`.

## Two consumers, two channels

| Consumer | Channel | Leaf resolution |
|---|---|---|
| `apps/insolvia_app` | npm **workspace member symlink** — source, live | Metro picks `.native.tsx` (react-native-web renders it on app-web) |
| `apps/insolvia_marketing` | **published version** from GitHub Packages | Vite picks `.web.tsx` (its `resolve.extensions` lists the `.web` suffixes) |

The app is inside the workspace and tracks source automatically — no install,
no version pin, no consume PR. The marketing site is deliberately **outside**
the workspace, keeps its own lockfile, and installs the published version, so
it imports exactly what an outside consumer would — through the `exports` map,
never reaching into unexported internals — and a broken package fails at
install/build time in its CI instead of passing on a symlink and only breaking
after publishing. This split is why the version-bump rule below protects
exactly one consumer (marketing), and why it is still absolute.

## The other packages are not published

`@insolvia-ai/tokens` and `@insolvia-ai/api-client` are `"private": true`
workspace members, consumed only by symlink inside this repo — no registry
presence, no version-bump gate; nothing below applies to them. The tokens
package remains the single token source: `tokens.json` plus the TypeScript
generator, which emits the design system's `theme.css` (above) and the typed
`tokens.ts` that the `.native` leaves read.

> Orphaned git tags `insolvia_design_system-v0.1.*` remain on the remote from a
> predecessor package; nothing resolves them, and deleting a published tag is
> irreversible, so they stay.

## The registry: `@insolvia-ai/design-system` on GitHub Packages

`packages/insolvia_design_system/` is published as the npm package
**`@insolvia-ai/design-system`** to **GitHub Packages**
(`https://npm.pkg.github.com`), not to npmjs.org.

**The scope is a contract with the registry.** GitHub Packages only accepts an
npm scope equal to the owning org's login — `insolvia-ai`, not `insolvia` —
and rejects any other scope with a misleading E403
(`Permission permission_denied: The requested installation does not exist.`)
that names neither the scope nor the rule. Keep it `@insolvia-ai` everywhere:
`package.json`, `.npmrc`, imports, docs.

| | |
|---|---|
| Package | `@insolvia-ai/design-system` |
| Registry | `https://npm.pkg.github.com` |
| Source | `packages/insolvia_design_system/` |
| Publish workflow | `.github/workflows/design-system-publish.yml` |
| PR gate | `.github/workflows/design-system-pr.yml` — required check `Design system` |

### How publishing works

The workflow runs on **push to `main`** touching the package (plus
`workflow_dispatch`). It:

1. installs the root workspace with `npm ci` (Node 24, matching
   `engines.node`) — the package is a workspace member with no lockfile of its
   own,
2. asks the registry whether `@insolvia-ai/design-system@<version>` already exists,
3. **skips cleanly** if it does — a version bump is the only thing that triggers
   an actual publish,
4. otherwise runs `npm publish`. There is no build step: the published
   artifact is `src/` verbatim, per the section above.

**To ship a new version: bump `version` in
`packages/insolvia_design_system/package.json` and merge to `main`.**
Nothing else. Every other push to `main` lands on the skip path and stays green.

#### Every package change must bump the version

The skip path in step 3 has a failure mode: a PR that changes the package but
not the version merges green, the publish no-ops, and the registry silently
goes stale — the marketing site keeps installing an artifact that no longer
matches `main`, with no error anywhere. (The app is immune — it sees source
through the workspace symlink — which is exactly why marketing is who this
rule protects.) So the rule is: **any change under
`packages/insolvia_design_system/` bumps `version` in the same PR.** This is
machine-enforced by the *Require a version bump when the package changed* step
in `design-system-pr.yml`, which diffs the package directory against the PR
base and fails on an unchanged version (and hard-errors if it cannot read the
base `package.json`, rather than silently passing — with one carve-out for the
PR that first introduces the package).

The flip side of publish-on-every-change: the marketing site **installs the
published package, never the source by path.** A committed `file:` dependency
bypasses the published contract above; a local `file:` override while
debugging is fine, but it never gets committed.

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
- uses: actions/setup-node@v5
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
@source '../node_modules/@insolvia-ai/design-system/src';
```

The `@source` line is not optional. Tailwind v4 scans your own source for class
names, but the design system's classes live in its published `src` inside
`node_modules`, which Tailwind does not scan by default. Omit it and the
components render completely unstyled — the utilities they reference are simply
never generated. This is the classic "why are my styles gone" bug.

```tsx
import { Button, Card, Field } from '@insolvia-ai/design-system';
```

`react` and `react-dom` are peer dependencies (18 or 19). `react-native` and
`@insolvia-ai/tokens` are **optional** peers: a web consumer never resolves the
`.native` leaves, so a plain `npm install` must not — and does not — drag in
React Native or the unpublished tokens package.

A web consumer also owns two pieces of resolution wiring, both in marketing's
`vite.config.ts`: the `.web` suffixes in `resolve.extensions` (so the
extensionless leaf imports pick `<name>.web.tsx`) and the bundling described
next.

### The `ssr.noExternal` trick — and why it is mandatory

The marketing site is server-rendered and deployed as a **Lambda container
image**. By default Vite treats dependencies as **external** for the SSR
build — it leaves the bare `@insolvia-ai/design-system` import in the server
bundle and resolves it from `node_modules` at runtime. Marking the package
`noExternal` tells Vite to **bundle its source into the server build
instead**:

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

Two reasons:

- **It is load-bearing, not just hygiene.** The package publishes
  TypeScript source, and Node cannot import `.ts`/`.tsx` from `node_modules`
  at runtime — so the SSR build *must* transpile and bundle the package.
  Removing `noExternal` does not merely re-introduce a token problem; it
  breaks the server outright.
- **The token stays out of the runtime image.** With the
  package bundled at build time, the private-registry dependency is a
  build-time-only concern: the token lives in the build environment (GitHub
  Actions), never in the deployed image, and the runtime Lambda ships without
  `@insolvia-ai/design-system` in `node_modules` at all. A registry credential
  inside a running Lambda image is exactly the kind of secret you do not want
  to have.

**CSS needs no equivalent trick.** `theme.css` *is* shipped in the published
package (`src/styles/theme.css`, exported as
`@insolvia-ai/design-system/theme.css`), but it is never imported by
JavaScript at runtime: the site's Tailwind entrypoint `@import`s it, Tailwind
resolves that from `node_modules` while compiling, and the output is a plain
CSS file in the client bundle. Build-time-only for the same reason as the JS,
by a different mechanism, and nothing needs configuring.

Two things to watch:

- `react` and `react-dom` stay external in the SSR bundle — they are ordinary
  public-registry deps of the site, which is fine. The `.native` leaves and
  `react-native` never enter the web build at all: the resolver never picks a
  `.native.tsx`, and nothing else may import React Native (the props-module
  lint rule above is what guarantees that).
- If the site ever adds a second `@insolvia-ai/*` package, add it to the same
  `noExternal` array. A regex (`/^@insolvia-ai\//`) is accepted and saves the
  bookkeeping.

## Hacking on the design system and the marketing site together

The published version is the contract, but npm has a sanctioned, *uncommitted*
loop for this. From `apps/insolvia_marketing/`:

```sh
npm install ../../packages/insolvia_design_system   # writes a file: dep
```

No build step is needed — the `exports` map points at `src`, so the link is
live immediately. **Revert it before committing:** a committed `file:`
dependency is exactly the path dependency this document exists to forbid, and
it takes the marketing site off the published contract without any error
appearing anywhere. Restore the version range and re-run `npm ci` so the
lockfile matches.

The app needs no loop at all: it is a workspace member, so edits to the
package are live in the app's dev server as you make them.

## Related

- `packages/insolvia_design_system/README.md` — component scope, the
  three-file pattern, theming, and the local dev loop.
- `packages/insolvia_design_system/CLAUDE.md` — the pattern's rules in
  agent-instruction form (props-module imports, no build step, generated
  `theme.css`, both typecheck halves).
- The `insolvia-design-system-pr` skill — the process rules: its own PR, its
  own version bump, marketing-only consume PRs.
- [`architecture.md`](architecture.md) — the monorepo shape and where these
  packages sit.
- [`plan.md`](../plan.md) — decision D4 (one cross-platform design
  system over one token source).
