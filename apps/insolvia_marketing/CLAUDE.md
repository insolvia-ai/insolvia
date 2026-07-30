# insolvia_marketing — agent rules

React Router v7 SSR marketing site for `www.insolvia.ai`. Human docs:
[`README.md`](README.md). Run it with `scripts/dev-up.sh`.

- **The site owns its UI.** Components live in `app/ui/` as ordinary themed
  React — no shared design-system package, published or otherwise
  ([ADR 0006](../../docs/adr/0006-theming-over-design-system.md)). They style off
  the generated `app/styles/theme.css`, a Tailwind v4 `@theme` layer rendered
  from `@insolvia-ai/tokens`. **Never hand-edit `theme.css`** — it opens with a
  `DO NOT EDIT` banner; change `tokens.json` and run `npm run tokens` at the repo
  root to regenerate it (`npm run tokens:check` fails the PR on drift).
- **The interactive components have a Vitest suite** (`npm test`, `vitest run`).
  Button, Field and Accordion carry `*.test.tsx` beside them — the Accordion in
  particular hand-ports the open/close behaviour Base UI used to provide, so its
  test is load-bearing. Add a test with any new interactive component.
- **Staging must stay non-indexable.** `app/lib/seo.ts` allowlists exactly
  `www.insolvia.ai`; never broaden it. A crawlable staging copy competes with
  prod for its own keywords.
- **Only prod owns the apex.** Staging passes `apex_domain = null`; the module
  drops the apex alias, records, and 301.
- **CSRF gotcha:** list the public hosts in `allowedActionOrigins`
  (`react-router.config.ts`) or POST actions 401 behind CloudFront → API Gateway
  (the Lambda sees the gateway host, not the public one).
- **The Lighthouse / Core Web Vitals budget** (`lighthouserc.json`) is enforced
  in CI. Being far lighter than Flutter-web is the whole reason this is React —
  don't regress it.
