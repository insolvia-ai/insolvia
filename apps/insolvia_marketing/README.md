# Insolvia marketing site

The marketing site for `www.insolvia.ai` — React Router v7 framework mode with
SSR, deployed as a Lambda container image + S3 client assets behind CloudFront
(`marketing-prod.yml`).

The site owns its UI components in [`app/ui/`](app/ui/), themed off
[`app/styles/theme.css`](app/styles/theme.css) — a Tailwind v4 `@theme` layer
generated from `@insolvia-ai/tokens`. There is no shared design-system package;
see [ADR 0006](../../docs/adr/0006-theming-over-design-system.md) and
[`CLAUDE.md`](CLAUDE.md).

## Run it

```bash
scripts/dev-setup.sh            # npm ci (public packages only)
scripts/dev-up.sh               # the RR7 dev server
```

To point the local dev server at a locally-running API instead of logging
waitlist submissions, start the API (`services/api/scripts/dev-up.sh`) and run
`INSOLVIA_API_BASE_URL=http://localhost:8080 scripts/dev-up.sh`.

Other commands: `npm run build` (production), `npm run typecheck`, `npm run
lint`, `npm run test` (the Vitest suite for the interactive components).

## Changing the theme

Colours, spacing, radii and fonts come from `@insolvia-ai/tokens`. To change
one, edit `packages/insolvia_tokens/tokens.json` and run `npm run tokens` at the
repo root — never hand-edit `app/styles/theme.css`, which is generated and opens
with a `DO NOT EDIT` banner (`npm run tokens:check` fails the PR on drift).
