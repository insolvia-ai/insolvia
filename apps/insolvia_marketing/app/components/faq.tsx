import { Accordion } from "@insolvia/design-system";

// Answers trace to docs/business-plan.html: the deterministic-core AI posture
// (§4), the MyCase integration wedge (§1/§7), Best Case migration (§7/§9),
// data handling (§4/§10), and the D8 web-first stance (docs/MVP_PLAN.md) —
// desktop is mentioned quietly, never led with.
const faqs = [
  {
    value: "ai-drafting",
    question: "Does AI draft the documents we file?",
    answer:
      "No — and that's a design principle, not a caveat. Insolvia's forms and means-test " +
      "calculations are rule-based and deterministic; AI never generates a number or legal " +
      "text that gets filed. AI is confined to extraction — reading credit reports, pay stubs, " +
      "and bank statements — and to review, flagging gaps like missing creditors before " +
      "filing. AI does the typing; the law does the math.",
  },
  {
    value: "mycase",
    question: "How does the MyCase integration work?",
    answer:
      "Insolvia is being built natively on MyCase's API. Client and matter data flows between " +
      "your practice and your petitions instead of being re-keyed into a separate tool — " +
      "ending that double entry is the point of the product.",
  },
  {
    value: "best-case",
    question: "We're on Best Case today. What does switching look like?",
    answer:
      "Migration is designed in from day one, not bolted on: Insolvia's AI-assisted import is " +
      "built to parse Best Case exports, so your existing case data comes with you rather " +
      "than being re-typed.",
  },
  {
    value: "data",
    question: "How is our clients' data handled?",
    answer:
      "Bankruptcy data is as sensitive as data gets — Social Security numbers and complete " +
      "financial pictures. Insolvia is being built to handle it accordingly: encryption in " +
      "transit and at rest, least-privilege access, and audit logging. AI processing runs " +
      "through an enterprise API whose terms exclude training on your data.",
  },
  {
    value: "platform",
    question: "Is Insolvia a web app or a desktop app?",
    answer:
      "Insolvia runs in your browser — that's the primary way firms will use it. A native " +
      "desktop option for offline, keyboard-driven drafting is also being built for firms " +
      "that want it.",
  },
  {
    value: "when",
    question: "When can my firm use it?",
    answer:
      "Insolvia is pre-launch and in active development. Join the early-access list and " +
      "we'll be in touch as early-access spots open.",
  },
];

export function Faq() {
  return (
    <section id="faq" className="w-full scroll-mt-xl bg-surface-alt">
      <div className="mx-auto flex w-full max-w-5xl flex-col gap-xl px-lg py-xxl">
        <div className="flex flex-col gap-sm">
          <p className="text-sm font-medium uppercase tracking-wide text-accent">FAQ</p>
          <h2 className="max-w-2xl font-heading text-3xl font-semibold text-ink">
            Fair questions, straight answers
          </h2>
        </div>
        <Accordion.Root className="w-full max-w-3xl">
          {faqs.map((faq) => (
            <Accordion.Item key={faq.value} value={faq.value}>
              <Accordion.Header>
                <Accordion.Trigger>{faq.question}</Accordion.Trigger>
              </Accordion.Header>
              <Accordion.Panel>
                <p className="max-w-2xl pb-md">{faq.answer}</p>
              </Accordion.Panel>
            </Accordion.Item>
          ))}
        </Accordion.Root>
      </div>
    </section>
  );
}
