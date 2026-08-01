# insolvia_marketing — agent rules

React Router v7 SSR marketing site for `www.insolvia.ai`. Human docs:
[`README.md`](README.md). Run it with `scripts/dev-up.sh`.

- **Consume the published `@insolvia-ai/design-system`**, never a committed
  `file:` path. A local `file:` override is an uncommitted debugging aid only —
  land the design-system change, publish a new version, then bump the range here
  (that `package.json`/lockfile bump is what re-runs this app's PR gate).
- **The design system is the cross-platform package** (0.2.x+): it publishes
  SOURCE — per-component `.web.tsx` / `.native.tsx` leaf pairs behind
  extensionless imports, with the consumer's bundler picking the leaf. The web
  leaves are plain React DOM + Tailwind — no third-party UI library underneath.
  Navigation styled as a button uses `buttonClass` on a
  `<Link>`/`<a>` — the web `Button` is a real `<button>` only, no `render`
  polymorphism.
- **Three pieces of wiring are load-bearing for that package; break any one and
  the site breaks quietly:**
  - `vite.config.ts` `resolve.extensions` (`.web.tsx` first) is the ONLY thing
    steering Vite to the web leaf — without it, extensionless leaf imports
    don't resolve (or worse, resolve native). `tsconfig.json`'s
    `moduleSuffixes: [".web", ""]` is tsc's spelling of the same rule.
  - `vite.config.ts` `ssr.noExternal` must always include the package: it
    ships raw `.ts/.tsx` that Node cannot resolve at SSR runtime, so Vite must
    bundle it — dev and build alike (the file's comments cover why
    clsx/tailwind-merge join only for the production build).
  - `app/styles/app.css` `@source` points Tailwind v4 at the package's source
    under `node_modules` — Tailwind doesn't scan `node_modules` by default, so
    without it every component class silently purges and components render
    unstyled while everything still "works".
- **The RNW grep guard in `marketing-pr.yml`** fails the build if the string
  `react-native` appears in `build/client` — the standing insurance that the
  platform split's core invariant holds (the web bundle never contains a native
  leaf). If it fires, fix the resolution leak; never delete the guard.
- **No `.npmrc` tricks, and keep it that way.** No peer-handling flags: a
  workaround here once hid a design-system packaging bug from the Docker build.
  A clean `npm ci` must work everywhere, the Docker packaging check included —
  the design system declares no react-native/tokens peers (see that package's
  `CLAUDE.md`), so if an install here ever demands react-native, the package
  regressed. Fix the package, not flags here.
- **Staging must stay non-indexable.** `app/lib/seo.ts` allowlists exactly
  `www.insolvia.ai`; never broaden it. A crawlable staging copy competes with
  prod for its own keywords. `app/lib/seo.test.ts` and
  `app/routes/robots-and-sitemap.test.ts` pin this — including that the check
  fails *closed* for an unanticipated host.
- **Tests are Vitest, colocated, `node` environment** (`npm test`, gated by the
  `Marketing site` job). They cover the SSR halves — the `.server.ts` libraries
  and route `loader`/`action` functions — which is where this app's behaviour
  lives; Lighthouse and the RNW guard only ever inspect the *built* output.
  There is no jsdom environment yet: add one when the first component test
  arrives, not before. Shape and conventions:
  [ADR 0008](../../docs/adr/0008-testing-shape-follows-the-code-it-tests.md)
  and the `insolvia-testing` skill.
- **The waitlist field caps in `app/lib/waitlist.server.ts` mirror
  `services/api` `core/waitlist.py` and nothing mechanical keeps them in step.**
  `waitlist.server.test.ts` table-drives every cap as the drift alarm — if you
  change a limit on either side, change it on both.
- **Only prod owns the apex.** Staging passes `apex_domain = null`; the module
  drops the apex alias, records, and 301.
- **CSRF gotcha:** list the public hosts in `allowedActionOrigins`
  (`react-router.config.ts`) or POST actions 401 behind CloudFront → API Gateway
  (the Lambda sees the gateway host, not the public one).
- **The Lighthouse / Core Web Vitals budget** (`lighthouserc.json`) is enforced
  in CI. This is the one public surface and Core Web Vitals feed search
  ranking; the site must stay fast on a throttled phone — don't regress it.
