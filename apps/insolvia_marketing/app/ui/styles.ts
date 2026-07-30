// Shared style fragments for the site's UI primitives.
//
// These target NATIVE element state (`:focus-visible`, `:disabled`) rather than
// the `data-[disabled]` attributes the previous Base-UI components exposed —
// there is no Base UI here any more, so the browser's own pseudo-classes are
// what fire.

export const focusRing =
  "outline-none focus-visible:ring-2 focus-visible:ring-accent focus-visible:ring-offset-2 focus-visible:ring-offset-bg";

export const disabledStyles =
  "disabled:pointer-events-none disabled:opacity-50 disabled:cursor-not-allowed";
