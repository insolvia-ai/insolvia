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
apps/       insolvia_app (Expo / React Native, web) · insolvia_admin (staff portal, Vite SPA) · insolvia_marketing (React SSR)
packages/   insolvia_api_client · insolvia_core (Python — shared by the services, not an npm member)
services/   api · admin · mailer · mcp    (Python on Lambda)
forms/      official-form field specs (B101, B106*, B107) — data + checker, no build
infra/      Terraform: ci-trust · shared · staging · prod
brand/      colors.json · fonts.json — Insolvia's palette and typefaces, the ONE place each is written down
tool/       brand-palette.ts · render-brand-theme.ts · reconcile-cognito-branding.ts
docs/       plan.md · reference/ · runbooks/ · adr/ · business/
```

**The design system is not in this repo.** `@insolvia-ai/design-system` and
`@insolvia-ai/tokens` live in
[`insolvia-ai/design-system`](https://github.com/insolvia-ai/design-system) and
arrive here as published dependencies. See
[ADR 0010](docs/adr/0010-design-system-moves-to-its-own-repository.md).

Every app / package / service, plus `infra/` and `docs/`, has its **own
`CLAUDE.md`** (that area's rules — it auto-loads when you work there; read it
before editing) and a `README.md` (for humans). One owner per fact — link,
never restate.

### One npm workspace, with an explicit member list

`package.json` at the root is the only workspace. **Read its `//` comments before
adding a member** — they own the reasoning; two consequences are worth knowing
before you touch anything:

- **The member list is explicit, and marketing is not on it.** Adding
  `apps/insolvia_marketing` would put it on this root's lockfile; its own
  lockfile is what proves a clean registry install works.
- **Never add the design system back as a member**, and never point it at a
  local checkout with `file:`/`link:`. It *was* both a member and published —
  the app read its source through the symlink while marketing installed the
  published version — which gave one package two simultaneous truths, and is
  exactly what [ADR 0010](docs/adr/0010-design-system-moves-to-its-own-repository.md)
  removed. To try an unpublished change, publish a prerelease from that repo.
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
| adding/removing a **human** IAM user or changing their groups | [`infra/envs/account-access/`](infra/envs/account-access/main.tf) — human-applied; CI holds no IAM user/group permissions at all |
| rotating an IAM user's **MFA device** | [`docs/runbooks/iam-mfa-rotation.md`](docs/runbooks/iam-mfa-rotation.md) — a console procedure, deliberately **not** Terraform (the TOTP seed would land in state) |
| adding a new package/app/service | `insolvia-new-package` skill |
| **opening a PR / writing or editing its description** | `insolvia-pr-description` skill — the body is the durable record, not a review request; CI is the only gate |
| **writing or changing any test**, or asked to "improve coverage" | `insolvia-testing` skill — the shape differs by area on purpose · [ADR 0008](docs/adr/0008-testing-shape-follows-the-code-it-tests.md) |
| **building or changing any UI** in the app, admin portal, or marketing — picking a component, calling it, wondering whether the package already has one | `design-system-catalogue` skill **first**, before writing the screen. The package ships 43 components; the habit to break is hand-rolling one it already owns |
| a component **renders unstyled**, resolves to the wrong leaf, or `react-native` turns up in the marketing bundle | `design-system-platforms` skill — the `.web`/`.native` split, and why this app renders `.native` on web too |
| **re-branding**: changing a brand colour or font the design system owns | `design-system-theming` skill — the override seams, and why `primaryHover` follows the base colour on web but is pre-computed on native |
| wiring the packages into a **new** consumer (401/404 on the `@insolvia-ai` scope, Tailwind seeing `node_modules`, dark mode) | `design-system-setup` skill — both consumers here are already wired; this is for when one breaks |
| **changing a shared component (Button, Field, …) or a design token** | **Not in this repo** — [`insolvia-ai/design-system`](https://github.com/insolvia-ai/design-system). Change it there, publish, then bump the dependency here · [ADR 0010](docs/adr/0010-design-system-moves-to-its-own-repository.md) · [ADR 0006](docs/adr/0006-owned-cross-platform-design-system.md) |
| **touching the firm or case domain, token verification, or their stores** — item shapes, permissions, `FirmStore`, the case stores, JWKS | [`packages/insolvia_core/CLAUDE.md`](packages/insolvia_core/CLAUDE.md) — shared by the Python services; the item shapes have one owner · [ADR 0012](docs/adr/0012-shared-python-domain-package.md) · [ADR 0016](docs/adr/0016-mcp-server-is-its-own-service.md) |
| **taking a new design-system / tokens version** (bumping the dependency) | `insolvia-design-system-bump` skill — bump every consumer's manifest and lockfile (app, admin portal, marketing), and regenerate the Cognito branding |
| changing the app-local components (`apps/insolvia_app/src/components/`) | [`apps/insolvia_app/CLAUDE.md`](apps/insolvia_app/CLAUDE.md) · [ADR 0005](docs/adr/0005-expo-app-layout.md) — no version bump, not published; shared Button/Field come from the package, row above |
| **changing a brand typeface**, or adding a weight | [`brand/fonts.json`](brand/fonts.json) — the companion to `colors.json`, for the same reason: the design system's base theme states no display face on purpose. Naming a family does not load it — the app self-hosts the faces in `apps/insolvia_app/public/fonts` via [`scripts/fetch-brand-fonts.sh`](scripts/fetch-brand-fonts.sh), and marketing/admin receive the names but not yet the faces |
| **changing a brand colour**, or wondering why a surface renders monochrome | [`brand/colors.json`](brand/colors.json) — the design system's base theme is deliberately *unbranded* (tokens 0.5.0+), so Insolvia's navy and brass are overrides layered on top. Four surfaces are generated from this one file; `npm run tokens` regenerates, `npm run tokens:check` gates it. **Never hand-edit a generated output** · [ADR 0020](docs/adr/0020-the-brand-is-a-consumer-owned-override.md) |
| **the sign-in page's colours** (`infra/modules/auth/managed-login-settings.json`) | [`tool/reconcile-cognito-branding.ts`](tool/reconcile-cognito-branding.ts) — one of the four outputs above, so it is the row above you want; this is the mapping onto AWS's document |
| changing branch protection / required PR checks on `main` | `insolvia-branch-protection` skill — run `scripts/update-ruleset.sh`, don't click through settings and **never hard-code a ruleset id** |
| publishing a package / bumping versions | [`docs/reference/package-publishing.md`](docs/reference/package-publishing.md) |
| touching env model, hosting, or PR-gate design | [`docs/reference/architecture.md`](docs/reference/architecture.md) |
| **touching the case schema** — case CRUD, intake fields, extraction output, anything storing case data | [`docs/reference/case-data-model.md`](docs/reference/case-data-model.md) — provenance and confirm-before-entry are invariants, not conventions |
| **touching the official-form field specs** — form fields, revisions, the forms engine's input | [`forms/CLAUDE.md`](forms/CLAUDE.md) — the AcroForm dumps are ground truth; `forms/scripts/dev-test.sh` gates every edit |
| touching Terraform state / naming / deploy order | [`docs/reference/terraform.md`](docs/reference/terraform.md) |
| doing one-time AWS/GitHub bootstrap | [`docs/runbooks/aws-bootstrap.md`](docs/runbooks/aws-bootstrap.md) |
| working on mail / SES | [`docs/reference/email.md`](docs/reference/email.md) · [`docs/runbooks/ses-production-access.md`](docs/runbooks/ses-production-access.md) |
| **adding, moving, or rewriting anything in `docs/`** | [`docs/CLAUDE.md`](docs/CLAUDE.md) — four kinds, one per directory; and what belongs in a skill instead |
| looking for any other runbook | [`docs/README.md`](docs/README.md) |

## `.agents/skills/` — installed skills, and which ones apply here

**These files are installed, not committed.** `skills-lock.json` is the tracked
manifest; `scripts/dev-setup.sh` installs from it with the
[`skills`](https://github.com/vercel-labs/skills) CLI; `.agents/skills/` and the
symlinks it drops in `.claude/skills/` are gitignored. **A fresh clone has none
of them until dev-setup runs** — if a skill you expect is missing, that is why:

```bash
./scripts/dev-setup.sh
```

**A git worktree is a fresh clone for this purpose**, and this is the common
case: a worktree checks out *tracked* files, and both halves of an install are
ignored, so it starts with the `insolvia-*` skills and none of the rest. Nothing
errors — the skills are just absent from the list, which is a quiet way to lose
`design-system-catalogue` on exactly the branch where a screen is being written.
The `SessionStart` hook now repairs this automatically by symlinking them from
the primary checkout; if you are ever in a worktree missing them, that repair is
one offline command, and it does **not** rewrite `skills-lock.json`:

```bash
./scripts/dev-skills.sh --link
```

They were vendored once, and 131 files of other people's documentation sat in
the tree being reviewed as though we owned it and updated by hand. One
consequence is worth keeping in mind: the installer takes each source at its
current HEAD, so **dev-setup can leave `skills-lock.json` dirty**. That diff is
a real signal that an upstream skill moved — read it, don't discard it.

Never edit anything under `.agents/skills/` — the next install overwrites it.
Fix a *design-system* skill in
[`insolvia-ai/design-system`](https://github.com/insolvia-ai/design-system);
for a third-party one, note the disagreement here in the table instead.

The directory holds **two kinds**, and they behave differently:

- **The four `design-system-*` skills are ours**, published from
  [`insolvia-ai/design-system`](https://github.com/insolvia-ai/design-system)
  alongside the packages themselves. Because they are symlinked into
  `.claude/skills/`, they **do** load — invoke them by name. They document the
  package *as a consumer sees it*, which is exactly this repo's position.
  **Use them: they are what stops a screen hand-rolling a component the package
  already ships.** The catalog above says which to reach for when.
- **Everything else is third-party** (Expo). It is installed the same way and
  symlinked the same way, so it is equally reachable — the table below is what
  tells you which of it to ignore.

The third-party half is also confident, well-written, and — for a third of its
contents — pointed directly away from the decisions in this repo. Hence the
table. A "do not use" here always has a reason, because a bare prohibition
invites the next agent to overrule it.

| Skill | Verdict | Why |
|---|---|---|
| `design-system-catalogue` | **Use — reach for it first on any UI change** | Ours. What the package ships and how to call it. Prevents writing a component the package already owns, in a repo whose own rules forbid a third-party one. |
| `design-system-platforms` | **Use** | Ours. Why this app renders the `.native` leaves on web too, and what "renders unstyled" means — the invariant `apps/insolvia_app/metro.config.js` and marketing's `vite.config.ts` each hold up. |
| `design-system-theming` | **Use** | Ours. The override seams for a re-brand. Note tokens stay the source here — the sign-in page's colours are generated from them (`npm run tokens`). |
| `design-system-setup` | **Use**, rarely | Ours, but both consumers here are already wired (`.npmrc`, `@source`, bundler config). It is the reference when one of those breaks, or for a *new* consumer — not a checklist to re-run. |
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
| `gluestack-ui-v5` | **Uninstalled** — do not reinstall | It described a component library this codebase deliberately does not have, and its first principle was *"gluestack components over React Native primitives"* — the exact inversion of our decision. It is gone from `skills-lock.json`, so dev-setup no longer installs it. [ADR 0004](docs/adr/0004-react-native-replaces-flutter.md) keeps the measurements that rejected the library, including two accessibility defects it caused. |
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
