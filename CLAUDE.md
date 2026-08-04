# Insolvia — agent guide

Bankruptcy case-prep & e-filing: one **TypeScript** monorepo on AWS. The app is
**React Native on Expo** (`apps/insolvia_app`), the marketing site is React
Router v7 + Vite (`apps/insolvia_marketing`), the services are Python on Lambda.

**Web is the only target that ships.** No desktop builds; nothing committed under
`ios/`/`android/` — `expo prebuild` generates those on demand. **Expo free tier
only**: no EAS Build/Submit/Update/Hosting, no Expo account, and a CI guard that
fails the build on an `eas.json`. Decision **D9** in
[`docs/plan.md`](docs/plan.md) and
[ADR 0004](docs/adr/0004-react-native-replaces-flutter.md) own this — read 0004
before proposing NativeWind, Tailwind-in-the-app, or a UI library.

**This file is a catalog.** It says where things live and what to open before
touching them — read the linked file when your task needs it. Detail lives there,
not here.

**Always (before you look anything up):**
- This repo is **public** — never commit secrets, credentials, real mailbox
  addresses, or customer/case data.
- Never commit to `main` — work on a branch (`claude/<name>-<id>`) and open a PR.
  *(A `PreToolUse` hook enforces this.)*
- **Three environments, every time: local, staging, prod.** A change is not
  finished when staging and prod are wired — say what it does on a developer's
  machine, and do that too. `infra/envs/dev` is a real environment
  (per-machine, applied by `scripts/dev-aws-*`), not a nicety.
- **Everything must be testable locally**, unless there is a reason good enough
  to write down. If a thing genuinely cannot be — it needs the OIDC deploy
  role, a real CloudFront distribution, an SES production identity — say so
  explicitly in the PR and give the nearest local approximation. "You'll see it
  on staging" is not a plan; it moves the feedback loop from seconds to a
  deploy, and it hides failures that only reproduce with real data.

## The map

```
apps/       insolvia_app (Expo / React Native, web) · insolvia_marketing (React SSR)
packages/   insolvia_tokens · insolvia_design_system · insolvia_api_client
services/   api · mailer            (Python on Lambda)
infra/      Terraform: ci-trust · shared · staging · prod
docs/       plan.md · reference/ · runbooks/ · adr/ · business/
```

Every app / package / service, plus `infra/` and `docs/`, has its **own
`CLAUDE.md`** (that area's rules — it auto-loads when you work there; read it
before editing) and a `README.md` (for humans). One owner per fact — link,
never restate.

### One npm workspace, with an explicit member list

`package.json` at the root is the only workspace. **Read its `//` comments before
adding a member** — they own the reasoning; two consequences are worth knowing
before you touch anything:

- **The member list is explicit, and marketing is not on it.**
  `packages/insolvia_design_system` is deliberately *both* a member (the app
  consumes its source through the symlink) and published (marketing consumes
  `@insolvia-ai/design-system` *by published version*). Adding
  `apps/insolvia_marketing` would symlink it to local source too — a broken
  package would then pass CI and only break after publishing.
- **Node resolution walks UP the tree.** A dependency that
  `apps/insolvia_marketing` forgot to declare can resolve from the root
  `node_modules` and pass locally. It is deliberately outside the workspace,
  with its own lockfile and its own CI job installing from it — that is what
  catches this, so don't consolidate it in.

## Catalog — need this? read that

| When you're… | Open |
|---|---|
| working in any app/package/service/infra | that directory's `CLAUDE.md` |
| running or building anything | `insolvia-scripts` skill → [`scripts/README.md`](scripts/README.md) |
| deploying / shipping / applying to staging or prod | `insolvia-deploy` skill — **deploys run in CI, never from your CLI** |
| hitting AWS auth / credential errors | `insolvia-aws-auth` skill |
| changing the CI deploy role's IAM | `insolvia-deploy-role-permissions` skill |
| adding a new package/app/service | `insolvia-new-package` skill |
| **writing or changing any test**, or asked to "improve coverage" | `insolvia-testing` skill — the shape differs by area on purpose · [ADR 0008](docs/adr/0008-testing-shape-follows-the-code-it-tests.md) |
| **changing `packages/insolvia_design_system`** — the shared components (Button, Field) both surfaces render | `insolvia-design-system-pr` skill — **its own PR + a version bump** · [ADR 0006](docs/adr/0006-owned-cross-platform-design-system.md) |
| changing the app-local components (`apps/insolvia_app/src/components/`) / tokens | [`apps/insolvia_app/CLAUDE.md`](apps/insolvia_app/CLAUDE.md) · [ADR 0005](docs/adr/0005-expo-app-layout.md) — no version bump, not published; shared Button/Field live in the package, row above |
| changing branch protection / required PR checks on `main` | `insolvia-branch-protection` skill — run `scripts/update-ruleset.sh`, don't click through settings and **never hard-code a ruleset id** |
| publishing a package / bumping versions | [`docs/reference/package-publishing.md`](docs/reference/package-publishing.md) |
| touching env model, hosting, or PR-gate design | [`docs/reference/architecture.md`](docs/reference/architecture.md) |
| **touching the case schema** — case CRUD, intake fields, extraction output, anything storing case data | [`docs/reference/case-data-model.md`](docs/reference/case-data-model.md) — provenance and confirm-before-entry are invariants, not conventions |
| touching Terraform state / naming / deploy order | [`docs/reference/terraform.md`](docs/reference/terraform.md) |
| doing one-time AWS/GitHub bootstrap | [`docs/runbooks/aws-bootstrap.md`](docs/runbooks/aws-bootstrap.md) |
| working on mail / SES | [`docs/reference/email.md`](docs/reference/email.md) · [`docs/runbooks/ses-production-access.md`](docs/runbooks/ses-production-access.md) |
| **adding, moving, or rewriting anything in `docs/`** | [`docs/CLAUDE.md`](docs/CLAUDE.md) — four kinds, one per directory; and what belongs in a skill instead |
| looking for any other runbook | [`docs/README.md`](docs/README.md) |

## `.agents/skills/` — vendor Expo skills, and which ones apply here

That directory is **third-party, installed by the user, and not ours to edit.**
It does **not** auto-load, so nothing in it reaches you unless you go and read
it. Read freely; never modify.

It is also confident, well-written, and — for a third of its contents — pointed
directly away from the decisions in this repo. Hence the table. A "do not use"
here always has a reason, because a bare prohibition invites the next agent to
overrule it.

| Skill | Verdict | Why |
|---|---|---|
| `expo-router` | **Use** | We use Expo Router with `web.output: "single"`; this is the routing reference. |
| `expo-project-structure` | **Use**, with two exceptions | It *is* our layout ([ADR 0005](docs/adr/0005-expo-app-layout.md)) — minus `eas.json` and minus `src/server`/`app/api`. See below. |
| `expo-data-fetching` | **Use** | Fetching against `services/api`; the caching and error-handling guidance is stack-neutral and correct. |
| `expo-upgrade` | **Use** | The SDK is pinned exact, so an upgrade is a deliberate task and this is the procedure for it. |
| `eas-hosting` | **Do not use** | It deploys to Expo-managed Cloudflare Workers — exactly what `infra/modules/web_hosting` already does for free, under Terraform, in our own account. Adopting it would fork the deploy path. |
| `eas-app-stores` | **Do not use** | `eas build`/`eas submit` are paid, need an Expo account in CI, and need paid Apple/Google accounts. We ship no store builds. |
| `eas-workflows` | **Do not use** | A second CI system alongside `.github/workflows/`, billed per job. Our required-check contract lives in GitHub Actions ([`docs/reference/architecture.md`](docs/reference/architecture.md)). |
| `eas-observe` | **Do not use** | Paid production metrics ingestion, and it wants `expo-observe` wired into the app root. Not on the free tier. |
| `eas-simulator` | **Do not use** | Paid cloud simulators for native builds we do not produce. `allowed-tools: Bash(eas *)` — it will try to run the CLI. |
| `eas-update-insights` | **Do not use** | Reports on EAS Update, which we do not publish. Also `allowed-tools: Bash(eas *)`. |
| `gluestack-ui-v5` | **Do not use** | It describes a component library this codebase deliberately does not have, and its first principle is *"gluestack components over React Native primitives"* — the exact inversion of our decision. [ADR 0004](docs/adr/0004-react-native-replaces-flutter.md) has the measurements, including two accessibility defects that came from this library. |
| `expo-tailwind-setup` | **Do not use** | **There is no Tailwind in the app at all** (it stays on the web side: the design system's `.web` leaves and marketing). It also pins `react-native-css@0.0.0-nightly.5ce6396`, whose own npm metadata reads *"Outdated SDK 54 era nightly… cannot resolve on Expo SDK 55 or newer"* — we are on SDK 57. |
| everything else | **Case by case** | Not evaluated. Check the frontmatter first: **"EAS service (paid)" or an `allowed-tools: Bash(eas *)` line means it is out of scope**, whatever the task looks like. |

**All six `eas-*` skills are out of scope for one reason:** we are on Expo's free
tier by decision, with no Expo account anywhere in the pipeline. `app-pr.yml`
enforces it — an EAS config file, the EAS command-line tool as a dependency, an
Expo access token, or the over-the-air update client fails the build — so
following one of these does not merely violate a preference, it turns the App
check red. (This sentence deliberately avoids spelling the exact package and
secret names, because the guard greps tracked files for them and would flag its
own description.)

**The two exceptions to `expo-project-structure`**, both from
[ADR 0004](docs/adr/0004-react-native-replaces-flutter.md):

- **No `eas.json`.** Free tier; and see the guard above.
- **No `src/server/` and no `+api.ts` routes.** The API is Python on Lambda in
  `services/api` — one trust boundary, one place that touches the data stores
  ([ADR 0001](docs/adr/0001-client-stays-dumb-trust-boundary.md)). Shipping
  Expo Router API routes would also need EAS Hosting.
