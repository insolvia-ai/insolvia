// The jobs-to-be-done from docs/business-plan.html §6, presented as the path
// the product is being built along. The product is pre-launch, so the core
// jobs are labeled "building now" and e-filing / notice tracking are labeled
// as roadmap — never as shipped features.
const jobs = [
  {
    step: "01",
    status: "Building now",
    title: "Intake without the re-typing",
    body:
      "Capture a client's full financial picture quickly and accurately — starting from what " +
      "already lives in MyCase instead of a blank form. AI extraction reads credit reports and " +
      "pay stubs so the data arrives structured, ready for review.",
  },
  {
    step: "02",
    status: "Building now",
    title: "Compliant petitions, schedules, and the means test",
    body:
      "A deterministic forms engine assembles the petition packet — schedules, statements, and " +
      "the rule-based means-test calculation — with minimal re-keying. An AI review pass " +
      "cross-checks the packet and flags gaps like missing creditors before anything is filed: " +
      "a second set of eyes on every petition.",
  },
  {
    step: "03",
    status: "On the roadmap",
    title: "E-file to the court",
    body:
      "Where Insolvia is headed next: filing to the court through CM/ECF and bringing case " +
      "numbers and receipts back into the case — without an export step.",
  },
  {
    step: "04",
    status: "On the roadmap",
    title: "Track notices and deadlines",
    body:
      "Further along the same path: court notices, deadlines, and Proof-of-Claim events " +
      "tracked against the case, so a filed matter stays on top of the calendar instead of in " +
      "a folder.",
  },
];

export function Jobs() {
  return (
    <section id="product" className="mx-auto w-full max-w-5xl scroll-mt-xl px-lg py-xxl">
      <div className="flex flex-col gap-xl">
        <div className="flex flex-col gap-sm">
          <p className="text-sm font-medium uppercase tracking-wide text-accent">The product</p>
          <h2 className="max-w-2xl font-heading text-3xl font-semibold text-ink">
            Built along the path a consumer case actually travels
          </h2>
          <p className="max-w-2xl text-muted">
            The urgent job — compliant petitions you can trust — comes first. Filing and
            tracking follow on the roadmap.
          </p>
        </div>
        <ol className="grid list-none grid-cols-1 gap-lg p-0 sm:grid-cols-2">
          {jobs.map((job) => (
            <li key={job.step} className="flex flex-col gap-sm border-t border-line pt-md">
              <div className="flex items-baseline justify-between gap-md">
                <span className="font-heading text-sm font-semibold text-accent">{job.step}</span>
                <span className="rounded-pill border border-line px-sm py-xs text-xs font-medium uppercase tracking-wide text-muted">
                  {job.status}
                </span>
              </div>
              <h3 className="font-heading text-xl font-semibold text-ink">{job.title}</h3>
              <p className="text-sm text-muted">{job.body}</p>
            </li>
          ))}
        </ol>
      </div>
    </section>
  );
}
