import { Link } from "react-router";
import { Button } from "@insolvia-ai/design-system";

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
          render={<Link to="/waitlist" />}
        >
          Join the early-access list
        </Button>
      </div>
    </section>
  );
}
