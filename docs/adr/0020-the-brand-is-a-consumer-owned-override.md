# ADR 0020 — The brand is a consumer-owned override, generated from one file

- **Status:** Accepted
- **Date:** 2026-09-05
- **Relates to:** follows from
  [ADR 0010](0010-design-system-moves-to-its-own-repository.md) — the design
  system is consumed by published version, so a change to it cannot be made
  here; extends [ADR 0006](0006-owned-cross-platform-design-system.md), whose
  platform split is what makes the brand need two different renderings.

## Decision

**Insolvia's colour palette lives in this repository, at
[`brand/colors.json`](../../brand/colors.json), as a set of *partial overrides*
layered on top of `@insolvia-ai/tokens` — and every surface that wears it is
generated from that one file by `npm run tokens`.**

Two halves, and they were decided separately:

1. **The design system's new base is adopted as shipped** — square corners and
   headings set in the body sans. Those are the visible change; they are not
   overridden.
2. **The colours are not.** The base theme retired navy and brass for a neutral
   ramp; Insolvia's palette is put back through the override seam the package
   deliberately leaves empty.

Four surfaces are generated, because the palette has four consumers and no
shared import between any of them:

| Generated output | Wearer |
|---|---|
| `apps/insolvia_app/src/theme/brand-colors.ts` | the app — `themeFor()` for our own components, `ThemeProvider` for the package's `.native` leaves |
| `apps/insolvia_marketing/app/styles/brand.css` | the marketing site |
| `apps/insolvia_admin/src/styles/brand.css` | the admin portal |
| `infra/modules/auth/managed-login-settings.json` | Cognito's hosted sign-in page |

None of the four may be hand-edited. `npm run tokens:check` — already a required
PR check — fails when one has drifted.

## Context — the base theme stopped being ours

Until `@insolvia-ai/tokens` 0.5.0, Insolvia's brand *was* the design system's
default. Navy and brass were the package's own values, so every consumer here
got the brand by installing the package and overriding nothing. There was no
brand file because there was no need for one.

0.5.0 ended that on purpose. The base theme became monochrome: `primary`,
`accent` and `brand` all resolve to neutral-12, and the chrome is steps on a
neutral ramp. The release notes are explicit that this is the intent — *"the
default theme states no brand at all, which is the point… the seam is
deliberately left empty."*

That is the right call for a package with more than one potential wearer, and it
is the same reasoning as ADR 0010: a shared thing should not carry one
consumer's specifics. But it relocates a fact rather than deleting it. The brand
still exists; it now has to live somewhere, and the only correct somewhere is
the consumer.

### Why not simply re-brand the package

The tempting shortcut is to put navy and brass back into
`insolvia-ai/design-system` as the defaults, and change nothing here. It was
rejected: that reintroduces exactly what 0.5.0 removed, makes the package
un-re-brandable for any future surface, and — under ADR 0010 — puts a
this-repo concern behind a publish cycle in another repository. A colour change
would become: PR there → publish → bump here. It is one file here.

### Why one file and a generator, rather than four hand-maintained copies

The four consumers cannot share an import, and this is structural rather than
incidental:

- `apps/insolvia_marketing` and `apps/insolvia_admin` are **deliberately outside
  the npm workspace**, each with its own lockfile (the root `package.json` owns
  that reasoning), so neither can import a module from this tree.
- Two of the four need **CSS**, which cannot read JSON at all.
- The fourth is an **AWS document** under `infra/`.

So the realistic alternative was twenty-six values written out four times, in
four languages, with nothing able to notice when one fell behind. That failure
is not hypothetical here — ADR 0010 records the design system sitting several
minor versions behind on one surface precisely because the surface people looked
at was current by construction. A palette split four ways would fail the same
way and be harder to see, because a wrong colour looks like a design choice.

Generating also cost nothing to gate. `npm run tokens:check` already existed as
a required check for the Cognito document; it now checks four outputs instead of
one.

### Why the whole palette, not just the accent

A narrower option was available and was considered: keep the new monochrome
chrome and re-brand only `primary`/`accent`/`brand`, which is the shape the
release notes describe. It was not taken. The full palette is restored — cream
canvas, navy dark canvas, brass — so the colour change from this work is
**none**, and the visible change is confined to corners and heading typeface.

This is worth recording because it is the half most likely to be re-litigated:
adopting a monochrome chrome is a brand decision, not a dependency-bump
decision, and it was not in scope for taking a release.

## Consequences

**Overrides are partial, and must stay partial.** `brand/colors.json` names only
the roles Insolvia actually moves. The status colours, `dangerText`, the overlay
values and the twelve ramp steps stay the package's, so a tokens release that
adds a role or re-measures a contrast reaches all four surfaces with no edit
here. `dangerText` is the concrete case: the package measures it at 6.06:1 light
and 6.78:1 dark, and an override would be an unmeasured guess. **Restating a
package default in this file silently opts out of every future improvement to
it** — that is the failure mode this file invites, and the reason it explains
itself at the top.

**The brand renders two different ways, and the derived states are the seam.**
On web, `theme.css` declares `--color-primary-hover` as a `color-mix()` over its
base role, so overriding `--color-primary` moves the hover with it. On React
Native there is no live blend — `ThemeProvider` merges values, not formulas — so
`primary` would move and its hover would stay behind. The stylesheets therefore
**omit** the hover/active states and the app's module **keeps** them.
`tool/brand-palette.ts`'s `DERIVED` set is the single owner of that split. This
is ADR 0006's platform split reaching the theme layer, and it is why one
generator emits two shapes rather than one.

**The app must supply the brand in every arm.** `ThemePreferenceProvider` used
to pass no overrides at all when the preference was `system`, which was correct
while the package's defaults *were* Insolvia's. It no longer is: passing nothing
now renders the package's monochrome chrome beside our navy. The `system`
preference still lets the OS decide, by handing each slot its own scheme.

**Dark mode depends on source order.** The generated stylesheets override
`[data-theme='dark']` at equal specificity with the design system's own block,
so ours wins only by being imported after `theme.css`. An import placed before
it does nothing, silently and in dark mode only. Both entry stylesheets say so
at the import.

**The `Cognito branding` check now gates more than Cognito, and keeps its
name.** `protect-main` matches a required check by exact name (see the
`insolvia-branch-protection` skill), so renaming the job to match its widened
scope would orphan the gate on `main`. The name is stale on purpose; the
workflow's header explains why.

**A colour change is now a one-file change with a mandatory second step.** Edit
`brand/colors.json`, run `npm run tokens`, commit what it writes. Two values
remain outside the pipeline because nothing can import into them —
`apps/insolvia_app/public/manifest.json` and `themeColor` in `app.config.ts` —
and must be checked by hand. The `insolvia-design-system-bump` skill carries the
procedure.

**Revisit if a second brand appears.** Everything above assumes one wearer in
this repository. A white-label surface, or a per-firm theme, makes
`brand/colors.json` a *default* rather than *the* palette, and the generator
would need to emit a set rather than a single answer. That is a different
decision and should supersede this one rather than grow inside it.
