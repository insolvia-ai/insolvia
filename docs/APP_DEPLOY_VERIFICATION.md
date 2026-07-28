# Verifying the app deploys — `staging-app` and `app`

The runbook for issues **4.1 (#9)** and **4.2 (#10)**: proving that the Flutter
web app actually deploys and actually serves, on staging first and then on
production. It is written to be executed top to bottom, once per environment.

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

1. builds the web bundle with `--dart-define=INSOLVIA_ENV=staging` on a pinned
   Flutter (`3.44.6`) and uploads it as an artifact,
2. builds the unsigned macOS app (a separate job — it does not gate the deploy),
3. applies `infra/envs/staging` and reads `bucket_name` / `distribution_id` out
   of the outputs,
4. syncs the bundle to S3 in three passes — long-lived payloads, then the
   unhashed entrypoints, then the HTML — and invalidates the distribution.
   The passes live in `.github/actions/web-bundle-sync`, which owns the
   Cache-Control rules for both environments; check 6 below verifies them.

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

**3. Root returns 200, from CloudFront, with the Flutter bundle.**

```bash
curl -sS -D - -o /dev/null https://staging-app.insolvia.ai/
```

Expect `HTTP/2 200`, `content-type: text/html`, an `x-cache: ... from
cloudfront` header, and `cache-control: no-cache, no-store, must-revalidate`.
Then confirm it is the app and not a stray object:

```bash
curl -sS https://staging-app.insolvia.ai/ | grep -o 'flutter_bootstrap.js'
```

Expect one match. Flutter's generated `index.html` loads the app through that
bootstrap script; if it is absent, something other than `build/web` was synced.

**4. A deep link returns 200 — the #11 check.** go_router owns client-side
paths, and S3 has no object at them, so without the CloudFront SPA rewrite this
returns 404 and the app never boots on a shared or bookmarked URL.

```bash
curl -sS -o /dev/null -w '%{http_code}\n' https://staging-app.insolvia.ai/auth/callback
```

Expect `200`. Then confirm the *body* is the app shell rather than an error page
CloudFront happened to number 200:

```bash
curl -sS https://staging-app.insolvia.ai/auth/callback | grep -o 'flutter_bootstrap.js'
```

Expect one match — identical to the root response, because the rewrite serves
`/index.html`. Any path with no S3 object works as a probe; `/auth/callback` is
just the first one that will matter.

If this fails, note that `error_caching_min_ttl = 10` means a wrong answer is
itself cached for ten seconds. Wait, re-run, and only then investigate.

**5. The build being served is the staging build.** The tell is the environment,
compiled in — `apps/insolvia_app/lib/config/environment.dart` maps
`INSOLVIA_ENV` to a `label` and a `host`, and the home screen renders both.

**The authoritative check is visual, and it has to be.** Flutter web paints into
a `<canvas>`, so there is no DOM text for `curl` to read — open
`https://staging-app.insolvia.ai/` in a browser and confirm the header badge
reads **STAGING** and the body line names `staging-app.insolvia.ai`. A prod
bundle served here would say `Production` / `app.insolvia.ai`, and that is
exactly the mistake this check exists to catch.

As scriptable corroboration, the compiled bundle carries the host string:

```bash
curl -sS https://staging-app.insolvia.ai/main.dart.js | grep -c 'staging-app\.insolvia\.ai'
```

Expect a non-zero count. Treat this as supporting evidence only — dart2js
constant-folds the environment switch, so what survives into the bundle is a
compiler detail, and a zero here means "look with your eyes", not "wrong build".

**6. Cache-Control is right on all three classes of object.** This is what makes
a deploy visible immediately without giving up long-lived asset caching.

Keep in mind while reading the results that **Flutter web content-hashes
nothing** — every filename it emits is stable while the bytes behind it change.
So the split is by *what the file is for*, not by whether the name looks hashed.

Long-lived payloads — the binaries whose bytes are stable in practice:

```bash
curl -sS -D - -o /dev/null https://staging-app.insolvia.ai/assets/fonts/MaterialIcons-Regular.otf
```

Expect `cache-control: public, max-age=31536000`, and **no** `immutable`. The
name is not content-addressed, so a hard reload has to remain able to recover.

Unhashed entrypoints — cached, but revalidated every request:

```bash
curl -sS -D - -o /dev/null https://staging-app.insolvia.ai/main.dart.js
curl -sS -D - -o /dev/null https://staging-app.insolvia.ai/flutter_bootstrap.js
```

Expect `cache-control: no-cache`. This is the class fixed in #49: these change
on every deploy under an unchanging name, so anything with a `max-age` here
pins a returning browser to a whole old build. Spot-check the rest of the
class the same way — `flutter.js`, `flutter_service_worker.js`, `version.json`,
`manifest.json`, `assets/AssetManifest.bin`.

HTML — never stored:

```bash
curl -sS -D - -o /dev/null https://staging-app.insolvia.ai/index.html
```

Expect `cache-control: no-cache, no-store, must-revalidate`. If HTML comes back
with a `max-age`, the sync passes ran in the wrong order or the `--exclude`
filters drifted, and browsers will pin a stale `index.html` for a year — the
CloudFront invalidation will not save you, because the staleness is in the
client. The same reasoning is why nothing in the entrypoint class may carry a
`max-age` either.

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
pinned Flutter version, "same source" also means "same compiler" — which is what
makes a rebuild an acceptable stand-in for the digest promotion the Lambda
services use. The app rebuilds rather than promotes because it selects its
environment at compile time; the staging bundle has `staging` baked into its JS
and can never be the prod bundle.

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

The workflow builds with `--dart-define=INSOLVIA_ENV=production`, syncs through
the same `web-bundle-sync` action, invalidates, and runs its own `curl` smoke
check against the root. That smoke check proves the host answers; it does not
prove the right bundle is on it. Run the six checks from Part 1 against
`app.insolvia.ai` anyway — check 5 especially.

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
| A returning browser shows an old build after a green deploy | An unhashed entrypoint went up with a `max-age` — Part 1, check 6. CloudFront invalidation cannot reach a client-side cache. |
