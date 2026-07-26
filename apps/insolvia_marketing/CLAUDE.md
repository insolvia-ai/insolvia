# insolvia_marketing — agent rules

React Router v7 SSR marketing site for `www.insolvia.ai`. Human docs:
[`README.md`](README.md). Run it with `scripts/dev-up.sh`.

- **Consume the published `@insolvia-ai/design-system`**, never a committed
  `file:` path. A local `file:` override is an uncommitted debugging aid only —
  land the design-system change, publish a new version, then bump the range here
  (that `package.json`/lockfile bump is what re-runs this app's PR gate).
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
