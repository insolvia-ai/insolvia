# Insolvia marketing site

The marketing site for `www.insolvia.ai` — React Router v7 framework mode with
SSR, deployed as a Lambda container image + S3 client assets behind CloudFront
(`marketing-prod.yml`). It consumes the **published**
`@insolvia-ai/design-system` package from GitHub Packages — see *Local
design-system debugging* below before you reach for a `file:` path.

## Run it

```bash
scripts/dev-setup.sh            # GitHub Packages auth + npm ci
scripts/dev-up.sh               # the RR7 dev server
```

`dev-setup.sh` handles the GitHub Packages token dance (a plain `npm install`
401s even for public packages). To point the local dev server at a locally-running
API instead of logging waitlist submissions, start the API
(`services/api/scripts/dev-up.sh`) and run
`INSOLVIA_API_BASE_URL=http://localhost:8080 scripts/dev-up.sh`.

Other commands: `npm run build` (production), `npm run typecheck`, `npm run lint`.

## Changing a design-system component

The package is not in this repository. It lives in
[`insolvia-ai/design-system`](https://github.com/insolvia-ai/design-system) and
reaches this site only as a published version.

Change it there, publish (a prerelease is fine), then bump the range here.
There is deliberately **no local link loop** — not a `file:` path, not a
`link:`, not even uncommitted. The app used to have exactly that, as a
workspace symlink, and one package with two live states is what
[ADR 0010](../../docs/adr/0010-design-system-moves-to-its-own-repository.md)
removed.
