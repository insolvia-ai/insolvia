import { Form, Link, data, useNavigation } from "react-router";
import { Button, Field } from "@insolvia/design-system";

import { seo } from "../lib/seo";
import {
  parseWaitlistForm,
  putWaitlistSubmission,
  WaitlistValidationError,
  type WaitlistErrors,
  type WaitlistFields,
} from "../lib/waitlist.server";
import type { Route } from "./+types/waitlist";

export const meta: Route.MetaFunction = () =>
  seo({
    title: "Join the early-access waitlist",
    description:
      "Get a spot when Insolvia opens its doors. Join the early-access waitlist for " +
      "bankruptcy case preparation built for consumer-bankruptcy firms on MyCase.",
    path: "/waitlist",
  });

/** The honeypot input's name. Humans never see the field; naive bots fill it. */
const HONEYPOT_FIELD = "website";

/** One shape for every failed submission, so the component narrows cleanly. */
interface ActionFailure {
  ok: false;
  errors: WaitlistErrors;
  values: WaitlistFields;
  /** Set only when storage itself failed (not a validation problem). */
  formError?: string;
}

export async function action({ request }: Route.ActionArgs) {
  const form = await request.formData();

  // Honeypot tripped → pretend success and drop the submission. Silence is the
  // point: an error message would tell the bot what to fix.
  if (String(form.get(HONEYPOT_FIELD) ?? "").trim() !== "") {
    return { ok: true as const };
  }

  const { values, errors } = parseWaitlistForm(form);
  if (errors) {
    const failure: ActionFailure = { ok: false, errors, values };
    return data(failure, { status: 400 });
  }

  // Same host precedence as isProductionHost in app/lib/seo.ts: behind
  // CloudFront → API Gateway the viewer's real host only arrives via
  // X-Forwarded-Host, so prefer it over the Lambda's own host.
  const forwarded = request.headers.get("x-forwarded-host");
  const host = forwarded?.split(",")[0]?.trim() || new URL(request.url).host;

  try {
    await putWaitlistSubmission(values, host);
  } catch (error) {
    // The API re-validates with the same rules; a 400 here means the two
    // layers drifted — surface the API's per-field verdicts.
    if (error instanceof WaitlistValidationError) {
      const failure: ActionFailure = { ok: false, errors: error.fields, values };
      return data(failure, { status: 400 });
    }
    console.error("[waitlist] failed to submit to the API:", error);
    const failure: ActionFailure = {
      ok: false,
      errors: {},
      values,
      formError: "Something went wrong on our end. Please try again.",
    };
    return data(failure, { status: 500 });
  }

  return { ok: true as const };
}

export default function Waitlist({ actionData }: Route.ComponentProps) {
  const navigation = useNavigation();
  const submitting = navigation.state !== "idle";

  if (actionData?.ok) {
    return (
      <section className="mx-auto w-full max-w-2xl px-lg py-xxl">
        <div className="flex flex-col items-start gap-md rounded-lg border border-line bg-card p-xl">
          <h1 className="font-heading text-3xl font-semibold text-ink">
            You&rsquo;re on the list
          </h1>
          <p className="text-base text-muted">
            Thanks — we&rsquo;ll be in touch as the build progresses, and you&rsquo;ll get a
            spot when doors open. No spam, no sales sequence.
          </p>
          <Button intent="secondary" size="md" nativeButton={false} render={<Link to="/" />}>
            Back home
          </Button>
        </div>
      </section>
    );
  }

  const errors = actionData?.errors;
  const values = actionData?.values;
  const formError = actionData?.formError;

  return (
    <section className="mx-auto w-full max-w-2xl px-lg py-xxl">
      <div className="flex flex-col gap-sm">
        <p className="text-sm font-medium uppercase tracking-wide text-accent">Early access</p>
        <h1 className="font-heading text-3xl font-semibold text-ink sm:text-4xl">
          Join the early-access list
        </h1>
        <p className="max-w-xl text-base text-muted">
          Insolvia is being built in the open with consumer-bankruptcy firms on MyCase. Leave
          your details and we&rsquo;ll keep you posted — and save you a spot when doors open.
        </p>
      </div>

      {/* Progressive enhancement: a plain document POST works without JS —
          the action re-renders this route with errors or the success state. */}
      <Form method="post" className="mt-xl flex flex-col gap-lg">
        {/* Honeypot: hidden from humans (and screen readers), tempting to bots.
            The action silently accepts-and-drops any submission that fills it. */}
        <div aria-hidden="true" className="absolute left-[-9999px] h-px w-px overflow-hidden">
          <label>
            Leave this field empty
            <input
              type="text"
              name={HONEYPOT_FIELD}
              tabIndex={-1}
              autoComplete="off"
              defaultValue=""
            />
          </label>
        </div>

        <div className="grid gap-lg sm:grid-cols-2">
          <Field.Root name="name" invalid={Boolean(errors?.name)}>
            <Field.Label>Name</Field.Label>
            <Field.Control
              required
              maxLength={200}
              autoComplete="name"
              defaultValue={values?.name}
              placeholder="Alex Alvarez"
            />
            {errors?.name ? <Field.Error match>{errors.name}</Field.Error> : null}
          </Field.Root>

          <Field.Root name="firm" invalid={Boolean(errors?.firm)}>
            <Field.Label>Firm name</Field.Label>
            <Field.Control
              required
              maxLength={200}
              autoComplete="organization"
              defaultValue={values?.firm}
              placeholder="Alvarez Law, PLLC"
            />
            {errors?.firm ? <Field.Error match>{errors.firm}</Field.Error> : null}
          </Field.Root>
        </div>

        <Field.Root name="email" invalid={Boolean(errors?.email)}>
          <Field.Label>Work email</Field.Label>
          <Field.Control
            type="email"
            required
            maxLength={320}
            autoComplete="email"
            defaultValue={values?.email}
            placeholder="you@firm.com"
          />
          <Field.Description>Only used for build updates and your invite.</Field.Description>
          {errors?.email ? <Field.Error match>{errors.email}</Field.Error> : null}
        </Field.Root>

        <Field.Root name="currentSoftware" invalid={Boolean(errors?.currentSoftware)}>
          <Field.Label>Current bankruptcy software (optional)</Field.Label>
          <Field.Control
            defaultValue={values?.currentSoftware ?? ""}
            render={
              <select>
                <option value="">Select one</option>
                <option value="Best Case">Best Case (Stretto)</option>
                <option value="NextChapter">NextChapter</option>
                <option value="Jubilee">Jubilee</option>
                <option value="CINcompass">CINcompass</option>
                <option value="Other">Other</option>
                <option value="None yet">None yet</option>
              </select>
            }
          />
          {errors?.currentSoftware ? (
            <Field.Error match>{errors.currentSoftware}</Field.Error>
          ) : null}
        </Field.Root>

        <Field.Root name="message" invalid={Boolean(errors?.message)}>
          <Field.Label>Anything you want us to know? (optional)</Field.Label>
          <Field.Control
            maxLength={2000}
            defaultValue={values?.message}
            render={<textarea rows={4} className="h-auto resize-y py-xs" />}
          />
          <Field.Description>
            Please don&rsquo;t include any client, debtor, or case information here — just tell
            us about your practice.
          </Field.Description>
          {errors?.message ? <Field.Error match>{errors.message}</Field.Error> : null}
        </Field.Root>

        {formError ? <p className="text-sm text-danger">{formError}</p> : null}

        <div className="flex flex-wrap items-center gap-md">
          <Button type="submit" intent="primary" size="lg" disabled={submitting}>
            {submitting ? "Joining…" : "Join the waitlist"}
          </Button>
          <span className="text-sm text-muted">Free to join · leave any time</span>
        </div>
      </Form>
    </section>
  );
}
