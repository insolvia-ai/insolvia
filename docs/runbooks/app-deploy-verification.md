# Verifying the app deploys — `staging-app` and `app`

Six read-only checks that prove the web app is actually served, and actually
serves the *right build*, on a given host. Run them after any app deploy you
care about — a green workflow proves the pipeline ran, not that the bundle on
the host is the one you meant to ship.

> **Status: both environments verified.** `staging-app.insolvia.ai` and
> `app.insolvia.ai` have passed all six. This is now a re-verification
> reference, not outstanding work.

**Running a deploy is not covered here** — that is the `insolvia-deploy`
skill. Hosts are decision **D2** in
[`../plan.md`](../plan.md), hosting topology and the promote-don't-rebuild model
are in [`../reference/architecture.md`](../reference/architecture.md), apply
order is in [`../reference/terraform.md`](../reference/terraform.md).

---

## Prerequisites

Each of these fails a deploy in a way that looks like something else.

**1. The domain is delegated to Route53.**

```bash
dig +short NS insolvia.ai
```

Expect four `awsdns` nameservers. Anything else and stop here.

**2. `infra/envs/shared` has applied and the wildcard cert is `ISSUED`.** Both
`staging` and `prod` look it up with `statuses = ["ISSUED"]`, so a pending cert
fails at plan time with a data-source error that says nothing about certificates.

```bash
aws acm list-certificates --region us-east-1 --certificate-statuses ISSUED --query "CertificateSummaryList[?DomainName=='*.insolvia.ai']"
```

Expect exactly one entry.

**3. `AWS_ROLE_ARN` exists in the GitHub Environment the job declares.**
Environment-scoped secrets are invisible to a job declaring a different
environment — they resolve to an empty string and OIDC fails unhelpfully.
`app-staging.yml` declares `insolvia-staging`; `app-prod.yml` and
`infra-prod.yml` declare `insolvia-production`.

```bash
gh api repos/insolvia-ai/insolvia/environments --jq '.environments[].name'
gh api repos/insolvia-ai/insolvia/environments/insolvia-staging/secrets --jq '.secrets[].name'
```

For credential trouble once a run starts, read the `insolvia-aws-auth` skill
rather than guessing.

---

## The six checks

Set the host once and run them all against it. A fresh CloudFront distribution
takes minutes to deploy even after Terraform returns, so a failure in the first
few minutes is worth re-testing before investigating.

```bash
HOST=staging-app.insolvia.ai     # or app.insolvia.ai
```

**1. DNS resolves.** The record is an A-alias to the distribution, so this
returns CloudFront's addresses directly, not a CNAME.

```bash
dig +short "$HOST" A
```

Expect four IPv4 addresses. Empty means the alias record was not created — check
the apply step, not DNS.

**2. TLS covers this host.** The wildcard is the whole reason the subdomain map
is flat (`staging-app`, not `app.staging`) — a nested host would not match
`*.insolvia.ai` and fails here with a name mismatch.

```bash
openssl s_client -connect "$HOST:443" -servername "$HOST" </dev/null 2>/dev/null | openssl x509 -noout -subject -dates -ext subjectAltName
```

Expect a SAN list containing `*.insolvia.ai` and a `notAfter` comfortably out.

**3. Root returns 200, from CloudFront, with the app bundle.**

```bash
curl -sS -D - -o /dev/null "https://$HOST/"
curl -sS "https://$HOST/" | grep -o '_expo/static/js/web/entry-[^"]*\.js'
```

Expect `HTTP/2 200`, `content-type: text/html`, an `x-cache: … from cloudfront`
header, `cache-control: no-cache, no-store, must-revalidate`, and exactly one
entry-chunk match ending in a content hash. If the chunk is absent, something
other than `apps/insolvia_app/dist` was synced. **Keep the filename** — checks 5
and 6 use it.

**4. A deep link returns 200.** Expo Router owns client-side paths and S3 has no
object at them, so without the CloudFront SPA rewrite this 404s and the app
never boots on a bookmarked URL. This is why the app exports with
`web.output: "single"`.

```bash
curl -sS -o /dev/null -w '%{http_code}\n' "https://$HOST/auth/callback"
curl -sS "https://$HOST/auth/callback" | grep -o '_expo/static/js/web/entry-[^"]*\.js'
```

Expect `200` and the **same** filename as check 3, because the rewrite serves
`/index.html`. A mismatch means the two responses came from different
`index.html` objects — a deploy landed between the requests, or an invalidation
is unfinished. `error_caching_min_ttl = 10` means a wrong answer is itself
cached for ten seconds: wait, re-run, then investigate.

**5. The build being served is the build you meant.** The environment is inlined
at build time — `apps/insolvia_app/src/config/environment.ts` maps
`EXPO_PUBLIC_INSOLVIA_ENV` to a `label` and `host`, and the home screen renders
both. This is the check that catches the Metro cache trap, and the one to run
most carefully.

**The authoritative check is visual.** Under `web.output: "single"` there is no
prerendering — `index.html` is an empty shell and the badge only exists after
the bundle runs, so `curl` cannot see it. Open `https://$HOST/` in a browser and
confirm the badge and body line name *this* environment. A prod bundle served on
staging is exactly the mistake this check exists to catch. (The badge is real DOM
text, so this is automatable with a headless browser when that is worth wiring.)

Scriptable corroboration only, using the entry filename from check 3:

```bash
curl -sS "https://$HOST/_expo/static/js/web/entry-<hash>.js" | grep -c "$HOST"
```

A zero means "look with your eyes", not "wrong build" — whether a string
survives minification is a bundler detail. But non-zero for the *other*
environment's host and zero for this one is conclusive and bad.

**6. Cache-Control is right on both classes of object.** Expo content-hashes
static assets and nothing else, so the split is by *whether the name is
content-addressed* — two tiers, no more.

```bash
curl -sS -D - -o /dev/null "https://$HOST/_expo/static/js/web/entry-<hash>.js"   # expect: public, max-age=31536000, immutable
curl -sS -D - -o /dev/null "https://$HOST/index.html"                            # expect: no-cache, no-store, must-revalidate
curl -sS -D - -o /dev/null "https://$HOST/manifest.json"                         # same
curl -sS -D - -o /dev/null "https://$HOST/favicon.png"                           # same
curl -sS -D - -o /dev/null "https://$HOST/auth/callback"                         # served from index.html — must carry the HTML headers
```

If an unhashed object comes back with a `max-age`, the second sync pass lost its
`--exclude "_expo/*"` and is being overwritten by the immutable pass — browsers
then pin a stale `index.html` for a year, and CloudFront invalidation cannot
save you because the staleness is client-side.

**Do not "simplify" this into one immutable pass.** The marketing site does sync
its whole bucket that way, correctly — its bucket holds nothing but hashed
assets, because its HTML comes from a Lambda. This bucket holds `index.html`.
Order matters too: the hashed pass runs **first**, because `index.html` names
the chunks it loads.

One caveat reading `x-cache` here: the managed `CachingOptimized` policy has a
Min TTL of 1 second, which overrides `no-cache`/`no-store` from the origin. A
`Hit from cloudfront` on `index.html` is that floor, not a broken header — read
`cache-control` itself, not hit/miss.

---

## Two things that differ on production

Worth knowing before you run the checks against `app.insolvia.ai`:

- **`app-prod.yml` does not apply Terraform.** It only *reads* `infra/envs/prod`
  outputs, so prod infra must already exist or the run fails on `terraform
  output` with an empty state and no useful message. `infra-prod.yml` is the
  only path that mutates prod infrastructure.
- **The app rebuilds rather than promotes.** Every other service promotes the
  staging-validated digest; the app inlines its environment at build time, so
  the staging bundle has `staging` baked into its JS and can never be the prod
  bundle. The pinned exact Expo SDK is what makes "same source" also mean "same
  bundler". `.github/actions/verified-commit` still requires the
  `insolvia/staging-release` commit status on that exact SHA for a
  hand-dispatched deploy; in a release run the `needs` chain is the proof.

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
| Prod dispatch blocked, "no successful insolvia/staging-release status" | Working as designed. The commit has not deployed green to staging. |
| A returning browser shows an old build after a green deploy | An unhashed object (`index.html` above all) went up with a `max-age` — check 6. CloudFront invalidation cannot reach a client-side cache. |
| The badge says `Staging` on `app.insolvia.ai`, or vice versa | The Metro transform cache served a bundle built for the other environment. Confirm the export ran with `--clear`; do not re-dispatch without it, because a warm cache reproduces the bug. |
| Browser console: *"Expected a JavaScript module script but the server responded with … text/html"* | A chunk `index.html` references is not in the bucket, and the SPA rewrite answered with HTML and a 200. Usually a `--delete` added to the hashed sync pass, which prunes chunks a still-open page is about to request. |
