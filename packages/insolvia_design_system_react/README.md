# `@insolvia-ai/design-system`

The React design system for Insolvia's **marketing site**. Built on
[Base UI](https://base-ui.com/react) and Tailwind v4, styled entirely from the
semantic design tokens generated out of `packages/insolvia_tokens/tokens.json`.

## Scope: six components, deliberately

| Component | Marketing job |
|---|---|
| `Button` | Calls to action (`primary` / `secondary` / `ghost`) |
| `Card` | Feature grids, pricing tiles |
| `NavBar` | Site header |
| `Footer` | Site footer |
| `Accordion` | The FAQ section |
| `Field` | The waitlist email capture (label + input + description + error) |

**Do not add a seventh component without a corresponding scope decision.**

This package is deliberately small — not a large library of Base UI wrappers.
It serves the marketing site only. `app.insolvia.ai` has its own design system
(bare React Native primitives in `apps/insolvia_app/src/components/`, per ADR
0004) and does not consume this one — the two share token *values*, never
components. Every component added here that the marketing site does not actually
render is a maintenance surface with no consumer, and it will drift. The
marketing site's needs are the ceiling, not the floor.

## Theming — `theme.css` is generated

`src/styles/theme.css` opens with a `DO NOT EDIT` banner and it means it. It is
rendered from `packages/insolvia_tokens/tokens.json` by

```sh
npm run tokens
```

To change a color, radius, spacing step, or font: **edit `tokens.json` and
regenerate.** `npm run tokens:check` fails CI on drift, so a hand-edit will be
caught rather than quietly shipped.

Components read only the **semantic** layer — `bg-bg`, `bg-card`,
`bg-surface-alt`, `text-ink`, `text-muted`, `text-brand`, `border-line`,
`bg-primary`/`text-primary-text`, `text-accent`, `text-danger`, plus the
`spacing-*`, `radius-*`, and `shadow-*` scales. There are **no hard-coded hex
values anywhere in `src/components/`**, and the raw palette names
(`ink`/`brass`/`paper`) are not exported — a re-brand is a one-file change.

Dark mode is `data-theme="dark"` on any ancestor element; nothing in component
source is theme-aware.

## Consuming it

Published to **GitHub Packages**, not npmjs.org, so an install needs a token
with `read:packages` — there is no anonymous install. See
[`docs/PACKAGE_PUBLISHING.md`](../../docs/PACKAGE_PUBLISHING.md) for consumer
auth, how to cut a release, and the Vite `ssr.noExternal` trick that keeps the
marketing site's runtime Lambda image free of any registry credential.

```sh
export NODE_AUTH_TOKEN=…        # PAT with read:packages
npm install @insolvia-ai/design-system
```

From the app's Tailwind v4 CSS entrypoint:

```css
@import 'tailwindcss';
@import '@insolvia-ai/design-system/theme.css';
```

```tsx
import { Button, Card, Field } from '@insolvia-ai/design-system';
```

`react` and `react-dom` are peer dependencies (18 or 19).

## Working on it

```sh
scripts/dev-setup.sh    # npm ci
scripts/dev-up.sh       # Storybook on :6006
```

Individual scripts once set up: `npm run lint` (eslint incl. jsx-a11y),
`npm run typecheck` (tsc --noEmit), `npm run test` (vitest + Testing Library),
`npm run build` (tsup → dist/), `npm run build-storybook` (static, as CI runs it).

### Conventions

- Every exported component has at least one **behavioural** test — the accordion
  actually opens, the button actually fires `onClick`, the input is actually
  reachable by its label. No snapshot tests. The app's design system holds itself
  to the same rule.
- Components are `forwardRef` wrappers with a `cn()` class merge and a variant
  map, and each lives in `src/components/<name>/` behind an `index.ts` barrel.
- Landmarks are real: `NavBar` renders a named `<nav>`, `Footer` renders
  `<footer>` with a named `<nav>` per link group, and `Field` relies on Base UI's
  label/control wiring rather than manual `htmlFor` bookkeeping.
