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

## Local design-system debugging

To hack on `packages/insolvia_design_system_react` and see it live here,
temporarily point the dep at the local package
(`npm install ../../packages/insolvia_design_system_react`, or a `file:` path) —
build the local package's gitignored `dist/` first. **Never commit that state**;
the rules for landing a design-system change are in [`CLAUDE.md`](CLAUDE.md).
