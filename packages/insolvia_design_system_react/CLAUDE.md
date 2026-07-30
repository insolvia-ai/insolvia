# insolvia_design_system_react — agent rules

Marketing-site UI (Base UI + Tailwind v4), published as `@insolvia-ai/design-system`.
Human docs: [`README.md`](README.md). Publishing flow:
[`docs/PACKAGE_PUBLISHING.md`](../../docs/PACKAGE_PUBLISHING.md).

- **Hard cap: six components** — `Button, Card, NavBar, Footer, Accordion,
  Field`. The `src/index.ts` barrel is the source of truth (read it, don't count
  directories). Everything the marketing site doesn't render is a maintenance
  surface with no consumer — the app has its own design system and shares only
  token values, never components (ADR 0004).
- **Adding a seventh is a scope decision, not a feature-PR call.** In the same
  PR: name the marketing page that renders it, update
  [`README.md`](README.md), this file, and decision D4 in
  [`docs/MVP_PLAN.md`](../../docs/MVP_PLAN.md); give it a behavioural test.
- **`src/styles/theme.css` is generated** from `packages/insolvia_tokens` —
  never hand-edit; change tokens there.
- **Style from semantic Tailwind tokens** (`bg-bg`, `text-ink`, `border-line`,
  `bg-primary`, …) — never a hard-coded hex.
- **The npm scope is `@insolvia-ai`** (must equal the org login; GitHub Packages
  rejects any other with a misleading "installation does not exist" 403).
- **Every exported component has ≥1 behavioural test** (Vitest + Testing
  Library). No snapshot tests.
- **Any change to this package is its OWN PR, with a `version` bump in
  `package.json`** (CI-enforced) — never bundled with app/docs/infra work; the
  version-bump gate fires on *any* file here. See the `insolvia-design-system-pr`
  skill.
