import { Button } from "@insolvia/design-system";

// The /waitlist route lands in a later PR of this stack; until then this is a
// plain link that 404s gracefully rather than anything that breaks the build.
export function Cta() {
  return (
    <section className="mx-auto w-full max-w-5xl px-lg py-xxl">
      <div className="flex flex-col items-start gap-lg rounded-lg bg-primary p-xl text-primary-text sm:p-xxl">
        <div className="flex flex-col gap-sm">
          <h2 className="max-w-2xl font-heading text-3xl font-semibold">
            Be first in line
          </h2>
          <p className="max-w-2xl text-base opacity-90">
            Insolvia is being built with consumer-bankruptcy firms on MyCase. Join the
            early-access list to follow the build and get a spot when doors open.
          </p>
        </div>
        <Button
          intent="secondary"
          size="lg"
          nativeButton={false}
          render={<a href="/waitlist" />}
        >
          Join the early-access list
        </Button>
      </div>
    </section>
  );
}
