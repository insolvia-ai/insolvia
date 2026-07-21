import { Link } from "react-router";
import { Button } from "@insolvia/design-system";

// Every claim here traces to docs/business-plan.html §1 (the wedge is
// seamlessness: MyCase-native, no double entry; AI kills data entry while the
// filed numbers stay rule-based). The product is pre-launch, so the posture is
// early access — no shipped-feature or outcome claims.
export function Hero() {
  return (
    <section className="mx-auto flex w-full max-w-5xl flex-col gap-xl px-lg pb-xxl pt-xxl">
      <div className="flex flex-col gap-md">
        <p className="text-sm font-medium uppercase tracking-wide text-accent">
          For consumer-bankruptcy firms on MyCase
        </p>
        <h1 className="max-w-3xl font-heading text-4xl font-semibold leading-tight text-ink sm:text-5xl">
          The bankruptcy engine, native to your practice
        </h1>
        <p className="max-w-2xl text-lg text-muted">
          Insolvia is bankruptcy case preparation — Chapters 7, 11, and 13 — built to live inside
          your MyCase practice instead of beside it. Petition data flows with the practice you
          already run, AI takes over the re-keying, and everything you file stays rule-based.
        </p>
      </div>
      <div className="flex flex-wrap items-center gap-md">
        <Button
          intent="primary"
          size="lg"
          nativeButton={false}
          render={<Link to="/waitlist" />}
        >
          Join the early-access list
        </Button>
        <Button intent="ghost" size="lg" nativeButton={false} render={<a href="#product" />}>
          See what we&rsquo;re building
        </Button>
      </div>
      <p className="text-sm text-muted">
        Insolvia is in active development, built in the open with consumer-bankruptcy firms.
      </p>
    </section>
  );
}
