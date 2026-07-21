import { Card } from "@insolvia/design-system";

// The four customer-facing pillars, translated from docs/business-plan.html §7.
// §7 lists five; the first ("warm MyCase distribution") is internal go-to-market
// strategy, not a customer benefit, so public copy carries the other four.
// Competitive framing stays qualitative — no market-share numbers, pricing, or
// vendor statistics, per the plan's own sourcing caveats.
const pillars = [
  {
    title: "Native MyCase integration, no double entry",
    body:
      "Best Case is a disconnected silo bolted onto the side of your practice — every client " +
      "gets typed once into practice management and again into petition prep. Insolvia is " +
      "built natively on MyCase, so petition data flows with the practice you already run.",
  },
  {
    title: "AI kills the data entry — never the filed numbers",
    body:
      "AI reads credit reports, pay stubs, and bank statements so your staff doesn't re-key " +
      "them, and reviews the petition as a second set of eyes. The forms and the means-test " +
      "math stay rule-based and deterministic: nothing AI-generated is ever filed.",
  },
  {
    title: "A compliance-forward forms engine",
    body:
      "A wrong or stale form is a filed error. Insolvia is built around versioned federal and " +
      "local forms with a visible update cadence and change log, so you can see exactly which " +
      "revision you're filing — and trust it.",
  },
  {
    title: "Migration that isn't a project",
    body:
      "Your history shouldn't be the incumbent's lock-in. Insolvia's AI-assisted import is " +
      "designed to parse your existing Best Case exports, so switching means bringing your " +
      "cases with you — not starting over.",
  },
];

export function Pillars() {
  return (
    <section id="why" className="w-full scroll-mt-xl bg-surface-alt">
      <div className="mx-auto flex w-full max-w-5xl flex-col gap-xl px-lg py-xxl">
        <div className="flex flex-col gap-sm">
          <p className="text-sm font-medium uppercase tracking-wide text-accent">Why Insolvia</p>
          <h2 className="max-w-2xl font-heading text-3xl font-semibold text-ink">
            Four reasons a MyCase firm leaves a disconnected petition tool behind
          </h2>
        </div>
        <div className="grid grid-cols-1 gap-lg sm:grid-cols-2">
          {pillars.map((pillar) => (
            <Card.Root key={pillar.title} elevation="flat">
              <Card.Title>{pillar.title}</Card.Title>
              <Card.Body>{pillar.body}</Card.Body>
            </Card.Root>
          ))}
        </div>
      </div>
    </section>
  );
}
