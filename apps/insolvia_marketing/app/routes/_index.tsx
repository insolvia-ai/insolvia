import type { MetaFunction } from "react-router";
import { Button, Card } from "@insolvia/design-system";

export const meta: MetaFunction = () => [
  { title: "Insolvia — Modern bankruptcy case preparation" },
  {
    name: "description",
    content:
      "Bankruptcy case preparation and e-filing for consumer-bankruptcy law firms, on desktop and web.",
  },
];

// Placeholder hero — real content lands in the next PR. Everything here is
// styled from semantic tokens only (bg-bg, text-ink, text-muted, ...).
export default function Home() {
  return (
    <section className="mx-auto flex max-w-4xl flex-col gap-xl px-lg pb-xxl pt-xxl">
      <div className="flex flex-col gap-md">
        <p className="text-sm font-medium uppercase tracking-wide text-accent">
          Bankruptcy case preparation
        </p>
        <h1 className="font-heading text-4xl font-semibold leading-tight text-ink sm:text-5xl">
          Modern case prep and e-filing for consumer-bankruptcy firms
        </h1>
        <p className="max-w-2xl text-lg text-muted">
          One platform for the whole petition — on your desktop and on the web.
        </p>
      </div>
      <Card.Root elevation="raised" className="max-w-xl">
        <Card.Title>Coming soon</Card.Title>
        <Card.Body>
          Insolvia is under active development. The full site — and the product — are on their way.
        </Card.Body>
        <Card.Footer>
          <Button intent="primary" size="lg">
            Learn more
          </Button>
        </Card.Footer>
      </Card.Root>
    </section>
  );
}
