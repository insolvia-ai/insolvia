# Verifying the app deploys — `staging-app` and `app`

The runbook for issues **4.1 (#9)** and **4.2 (#10)**: proving that the web app
actually deploys and actually serves, on staging first and then on production.
It is written to be executed top to bottom, once per environment.

**This is a human runbook on purpose.** Deploys run in CI and never from a CLI
(the `insolvia-deploy` skill, `infra/CLAUDE.md`), and the production dispatch is
gated behind the `insolvia-production` GitHub Environment. Nothing here can be
delegated to an agent — the dispatch is the deliberate act, and these checks are
what make it more than a green tick.

Everything below is read-only apart from the two dispatches, both explicitly
marked. Hosts and workflow mechanics are owned elsewhere and only referenced
here: the subdomain map is decision **D2** in [`MVP_PLAN.md`](MVP_PLAN.md), the
hosting topology and the promote-don't-rebuild model are in
[`ARCHITECTURE.md`](ARCHITECTURE.md), and apply order is in
[`TERRAFORM_ARCHITECTURE.md`](TERRAFORM_ARCHITECTURE.md).

---

## Prerequisites — check these before dispatching anything

Each one fails the deploy in a way that looks like something else, so spend the
minute.

**1. The domain is delegated to Route53.** If the `.ai` registrar still points
elsewhere, Terraform will happily write records nobody resolves.

```bash
dig +short NS insolvia.ai
```

Expect four `awsdns` nameservers. Anything else and stop here.

**2. `infra/envs/shared` has applied, and the wildcard certificate is `ISSUED`.**
Both `staging` and `prod` look the cert up by name with `statuses = ["ISSUED"]`,
so a pending or missing cert fails the apply at plan time with a data-source
error rather than anything about certificates.

```bash
aws acm list-certificates --region us-east-1 --certificate-statuses ISSUED --query "CertificateSummaryList[?DomainName=='*.insolvia.ai']"
```

Expect exactly one entry. Empty means `shared` has not applied or DNS validation
has not completed — `shared-infra-deploy.yml` owns that apply.

**3. `AWS_ROLE_ARN` exists in the GitHub Environment the job declares.**
Environment-scoped secrets are invisible to a job that declares a different
environment (or none) — they resolve to an empty string and the OIDC step fails
with an unhelpful message. `app-staging.yml`'s deploy job declares
`insolvia-staging`; `app-prod.yml` and `infra-prod.yml` declare
`insolvia-production`.

```bash
gh api repos/insolvia-ai/insolvia/environments --jq '.environments[].name'
```

Expect `insolvia-shared`, `insolvia-staging`, `insolvia-production`. Then, per
environment, confirm the secret is set (the value is never readable — presence is
the check):

```bash
gh api repos/insolvia-ai/insolvia/environments/insolvia-staging/secrets --jq '.secrets[].name'
```

If credentials misbehave once a run starts, read the `insolvia-aws-auth` skill
rather than guessing — the failure modes there are easy to misdiagnose.

---

## Part 1 — staging (`staging-app.insolvia.ai`, issue #9)

### What the workflow does

`app-staging.yml` runs on every push to `main` touching `apps/**`,
`packages/**`, `infra/envs/staging/**`, `infra/modules/**`, or itself, and is
also `workflow_dispatch`-able. In one run it:

1. exports the web bundle with `EXPO_PUBLIC_INSOLVIA_ENV=staging` on the pinned
   Expo SDK and uploads `apps/insolvia_app/dist` as an artifact,
2. applies `infra/envs/staging` and reads `bucket_name` / `distribution_id` out
   of the outputs,
3. syncs the bundle to S3 in **two** passes — hashed `_expo/` assets first and
   cached forever, then everything else with no caching at all — and invalidates
   the distribution. Check 6 below verifies both.

Two things in that build step are load-bearing and easy to "tidy" into a bug:

- **The export runs with `--clear`.** Metro's transform cache keys on file
  content and config, *not* on the environment the build ran in, so two exports
  of an unchanged tree with different `EXPO_PUBLIC_INSOLVIA_ENV` can emit
  byte-identical output. The failure mode is silent and severe — a bundle with
  `staging` compiled into it, served from the production bucket. `--clear` makes
  the environment impossible to inherit.
- **There is no macOS or Windows build job.** There used to be; decision D9
  removed the desktop targets entirely.

Unlike prod, this workflow **does** apply Terraform. That is why merging an
infra change to `main` is enough to stand staging up.

### Trigger it

A merge to `main` is the normal path. To run it deliberately for this
verification:

```bash
gh workflow run app-staging.yml --repo insolvia-ai/insolvia --ref main
```

Watch it, and note the run ID — the apply step's output is where the bucket and
distribution IDs come from if you need them later:

```bash
gh run watch --repo insolvia-ai/insolvia --exit-status
```

### The checks

Run these against `staging-app.insolvia.ai` only after the run is green. A fresh
CloudFront distribution takes several minutes to finish deploying even after
Terraform returns, so a failure in the first few minutes is worth re-testing
before investigating.

**1. DNS resolves.** The Route53 record is an A-alias to the distribution, so
this returns CloudFront's addresses directly, not a CNAME.

```bash
dig +short staging-app.insolvia.ai A
```

Expect four IPv4 addresses. Empty means the alias record was not created — check
the apply step, not DNS.

**2. TLS is valid and actually covers this host.** The wildcard is the whole
reason the subdomain map is flat (`staging-app`, not `app.staging`) — a nested
host would not match `*.insolvia.ai` and would fail here with a name mismatch.

```bash
openssl s_client -connect staging-app.insolvia.ai:443 -servername staging-app.insolvia.ai </dev/null 2>/dev/null | openssl x509 -noout -subject -dates -ext subjectAltName
```

Expect a SAN list containing `*.insolvia.ai` and a `notAfter` comfortably in the
future.

**3. Root returns 200, from CloudFront, with the app bundle.**

```bash
curl -sS -D - -o /dev/null https://staging-app.insolvia.ai/
```

Expect `HTTP/2 200`, `content-type: text/html`, an `x-cache: ... from
cloudfront` header, and `cache-control: no-cache, no-store, must-revalidate`.
Then confirm it is the app and not a stray object:

```bash
curl -sS https://staging-app.insolvia.ai/ | grep -o '_expo/static/js/web/entry-[^"]*\.js'
```

Expect one match, ending in a content hash. Expo's generated `index.html` loads
the app through that entry chunk; if it is absent, something other than
`apps/insolvia_app/dist` was synced. **Keep the returned filename** — checks 5
and 6 use it.

**4. A deep link returns 200 — the #11 check.** Expo Router owns client-side
paths, and S3 has no object at them, so without the CloudFront SPA rewrite this
returns 404 and the app never boots on a shared or bookmarked URL. This is why
the app exports with `web.output: "single"`: one `index.html` plus client
routing, which is exactly the shape the existing rewrite was built for.

```bash
curl -sS -o /dev/null -w '%{http_code}\n' https://staging-app.insolvia.ai/auth/callback
```

Expect `200`. Then confirm the *body* is the app shell rather than an error page
CloudFront happened to number 200:

```bash
curl -sS https://staging-app.insolvia.ai/auth/callback | grep -o '_expo/static/js/web/entry-[^"]*\.js'
```

Expect the **same** filename as the root response, because the rewrite serves
`/index.html`. Any path with no S3 object works as a probe; `/auth/callback` is
just the first one that will matter.

A mismatch between the two is worth stopping on: it means the two responses came
from different `index.html` objects, i.e. a deploy landed between the requests
or the invalidation has not finished.

If this fails, note that `error_caching_min_ttl = 10` means a wrong answer is
itself cached for ten seconds. Wait, re-run, and only then investigate.

**5. The build being served is the staging build.** The tell is the environment,
inlined at build time — `apps/insolvia_app/src/config/environment.ts` maps
`EXPO_PUBLIC_INSOLVIA_ENV` to a `label` and a `host`, and the home screen
renders both. This is the check that catches the Metro cache trap described
above, and it is the one to run most carefully.

**The authoritative check is still visual, for a different reason than before.**
Under `web.output: "single"` there is no prerendering: `index.html` is an empty
shell and the badge only exists after the bundle runs. So `curl` on `/` cannot
see it. Open `https://staging-app.insolvia.ai/` in a browser and confirm the
header badge reads **STAGING** and the body line names
`staging-app.insolvia.ai`. A prod bundle served here would say `Production` /
`app.insolvia.ai` — exactly the mistake this check exists to catch.

What *has* improved: the app now renders real DOM rather than painting into a
`<canvas>`, so the badge is inspectable text and this check is automatable with
a headless browser whenever it is worth wiring up. It was not automatable at all
before.

As scriptable corroboration, the bundle carries the host string. Use the entry
filename from check 3:

```bash
curl -sS "https://staging-app.insolvia.ai/_expo/static/js/web/entry-<hash>.js" \
  | grep -c 'staging-app\.insolvia\.ai'
```

Expect a non-zero count. Treat it as supporting evidence only — whether a string
survives minification is a bundler detail, so a zero here means "look with your
eyes", not "wrong build". A count that is non-zero for `app.insolvia.ai` *and*
zero for `staging-app.insolvia.ai` on the staging host, on the other hand, is
conclusive and bad.

**6. Cache-Control is right on both classes of object.** This is what makes a
deploy visible immediately without giving up long-lived asset caching.

**The rule inverted at the Expo migration, so read this before comparing against
older notes.** Flutter web content-hashed *nothing* — every filename it emitted
was stable while the bytes changed — which is why `web-bundle-sync` needed three
tiers split by what a file was *for*, and why issue #49 had to strip `immutable`
from everything. Expo hashes: `_expo/static/js/web/entry-<hash>.js`,
`_expo/static/css/global-<hash>.css`. So the split is now by *whether the name is
content-addressed*, which is the split it should always have been, and it needs
only two tiers.

Hashed assets — cached forever. Use the entry filename from check 3:

```bash
curl -sS -D - -o /dev/null "https://staging-app.insolvia.ai/_expo/static/js/web/entry-<hash>.js"
```

Expect `cache-control: public, max-age=31536000, immutable`. This is safe here
and was *not* safe under Flutter: the name changes when the bytes change, so a
returning browser cannot be pinned to stale code.

Everything else — `index.html` and whatever was copied out of the app's
`public/` — is unhashed and never stored:

```bash
curl -sS -D - -o /dev/null https://staging-app.insolvia.ai/index.html
curl -sS -D - -o /dev/null https://staging-app.insolvia.ai/manifest.json
curl -sS -D - -o /dev/null https://staging-app.insolvia.ai/favicon.png
```

Expect `cache-control: no-cache, no-store, must-revalidate` on all three. If any
of them comes back with a `max-age`, the second sync pass lost its
`--exclude "_expo/*"` and is being overwritten by the immutable pass — browsers
will then pin a stale `index.html` for a year, and the CloudFront invalidation
cannot save you because the staleness is in the client.

**Do not "simplify" this into one immutable pass.** The marketing site does sync
its whole bucket that way, and that is correct *there* — its bucket holds nothing
but hashed assets, because its HTML comes from a Lambda. This bucket holds
`index.html`. The two are not the same shape.

Order matters in the other direction too: the hashed pass runs **first**, because
`index.html` names the chunks it loads. HTML first would leave a window where the
live page referenced a chunk that had not been uploaded yet.

One caveat when reading `x-cache` on these: the managed `CachingOptimized`
policy has a Min TTL of 1 second, which overrides `no-cache`/`no-store` from the
origin. A `Hit from cloudfront` on `index.html` is that one-second floor, not a
broken header — read `cache-control` itself, not the hit/miss.

Check the deep-link response too: it is served from `/index.html`, so it must
carry the HTML headers, not the long-lived ones.

```bash
curl -sS -D - -o /dev/null https://staging-app.insolvia.ai/auth/callback
```

When all six pass, #9 is done: record the run URL on the issue and close it.

---

## Part 2 — production (`app.insolvia.ai`, issue #10)

Same six checks, against `app.insolvia.ai`, with **Production** /
`app.insolvia.ai` expected in check 5. What differs is everything before them.

### Three differences that matter

**1. `app-prod.yml` does not apply Terraform.** It only reads
`infra/envs/prod`'s outputs (`bucket_name`, `distribution_id`, `url`).
`infra-prod.yml` is the only path that mutates prod infrastructure — deliberately,
so a routine code deploy cannot drag unrelated infra drift into production. The
consequence for this runbook is an **order**: prod infra must exist before
`app-prod.yml` can read it, or the run fails on `terraform output` with an empty
state and no useful message.

**2. It is `workflow_dispatch`-only, behind `insolvia-production`.** Nothing
reaches prod on a push. The dispatch is the deliberate act, and the environment
is what scopes the AWS role to it.

**3. It refuses commits staging never blessed.** `.github/actions/verified-commit`
requires a *successful* `app-staging.yml` run for that exact SHA, then checks the
build out at that commit rather than at whatever `main` points to now. With the
pinned exact Expo SDK, "same source" also means "same bundler" — which is what
makes a rebuild an acceptable stand-in for the digest promotion the Lambda
services use. The app rebuilds rather than promotes because it inlines its
environment at build time; the staging bundle has `staging` baked into its JS and
can never be the prod bundle.

So: **run Part 1 first, on the commit you intend to ship.** It is not a courtesy
step — it is the gate.

### Step 1 — stand up prod infra (`infra-prod.yml`)

Plan first. `plan` is the default mode and writes the plan to the job summary;
it is the only place in CI that shows a plan against real prod state.

```bash
./scripts/prod-deploy.sh prod-infra
```

Read the summary. On a first run expect the `web_hosting` bucket, OAC,
distribution, bucket policy and Route53 alias to be created. Then apply:

```bash
./scripts/prod-deploy.sh prod-infra --input mode=apply
```

The `Outputs` step prints `bucket_name`, `distribution_id` and `url`. If those
are absent, `app-prod.yml` has nothing to read and will fail — do not proceed.

### Step 2 — confirm the commit is staging-green

The dispatch will reject it otherwise, but checking first turns a failed run
into a five-second answer:

```bash
gh run list --repo insolvia-ai/insolvia --workflow app-staging.yml --commit "$(git rev-parse HEAD)" --status success
```

Expect at least one row. If it is empty, merge and let staging deploy first.
`force: true` exists for shipping a hotfix while staging is broken for unrelated
reasons; it is loud in the job summary and is not the normal path.

### Step 3 — dispatch the app deploy

```bash
./scripts/prod-deploy.sh app
```

To pin an explicit commit rather than main's HEAD:

```bash
./scripts/prod-deploy.sh app --input sha=<full-40-char-sha>
```

The workflow exports with `EXPO_PUBLIC_INSOLVIA_ENV=production` (and `--clear`,
for the reason in Part 1), runs the same two-tier sync, invalidates, and runs its
own `curl` smoke check against the root. That smoke check proves the host
answers; it does not prove the right bundle is on it. Run the six checks from
Part 1 against `app.insolvia.ai` anyway — check 5 especially, since the Metro
cache trap is precisely a wrong-environment bundle that every other check
passes.

When they pass, #10 is done.

---

## Failure modes worth recognising

| Symptom | Cause |
|---|---|
| Apply fails on the ACM or ECR data source | `infra/envs/shared` has not applied, or the cert is not yet `ISSUED`. Apply order is `ci-trust` → `shared` → `staging`/`prod`. |
| OIDC step fails, `AWS_ROLE_ARN` looks empty | The job's `environment:` does not match the environment holding the secret. Never borrow another env's name. |
| `AccessDenied` naming a specific IAM action | The deploy role is missing a permission. That change cannot be applied by CI — see the `insolvia-deploy-role-permissions` skill. |
| TLS name mismatch on a staging host | A nested host (`app.staging.insolvia.ai`) was used somewhere. The wildcard covers one label; the flat map in D2 is load-bearing. |
| Deep link 404s | The CloudFront SPA rewrite is missing from the distribution — `infra/modules/web_hosting/main.tf` lines 56–69, and confirm the module is instantiated for that env. |
| `terraform output` empty in `app-prod.yml` | Prod infra has never been applied. Run `infra-prod.yml` with `mode=apply` first. |
| Prod dispatch blocked, "no successful app-staging.yml run" | Working as designed. The commit has not deployed green to staging. |
| A returning browser shows an old build after a green deploy | An unhashed object (`index.html` above all) went up with a `max-age` — Part 1, check 6. CloudFront invalidation cannot reach a client-side cache. |
| The badge says `Staging` on `app.insolvia.ai`, or vice versa | The Metro transform cache served a bundle built for the other environment. Confirm the export ran with `--clear`; do not re-dispatch without it, because a warm cache reproduces the bug. |
| Browser console: *"Expected a JavaScript module script but the server responded with … text/html"* | A chunk `index.html` references is not in the bucket, and the SPA rewrite answered with HTML and a 200. Usually a `--delete` added to the hashed sync pass, which prunes chunks a still-open page is about to request. |
