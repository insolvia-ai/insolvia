/**
 * Waitlist submission forwarding (issue #47).
 *
 * The SSR action POSTs to the Insolvia API's public `POST /v1/waitlist`
 * endpoint (services/api) instead of touching AWS itself — the marketing
 * Lambda is a client like any other, and per ADR 0001
 * (docs/adr/0001-client-stays-dumb-trust-boundary.md) no client holds AWS
 * credentials or calls an AWS service directly. Storage, the item schema, and
 * the DynamoDB grant all live with the API now.
 *
 * Validation here is a UX nicety: instant per-field errors without a network
 * round-trip. The API re-validates with the same rules (its limits were
 * copied from this file), so nothing depends on this layer being right.
 */

const EMAIL_RE = /^[^@\s]+@[^@\s]+\.[^@\s]+$/;

/** Per-field caps: mirror services/api core/waitlist.py exactly. */
const MAX = {
  name: 200,
  firm: 200,
  email: 320,
  currentSoftware: 100,
  message: 2000,
} as const;

export interface WaitlistFields {
  name: string;
  firm: string;
  email: string;
  currentSoftware: string;
  message: string;
}

export type WaitlistErrors = Partial<Record<keyof WaitlistFields, string>>;

function clean(form: FormData, key: keyof WaitlistFields): string {
  const value = form.get(key);
  return typeof value === "string" ? value.trim() : "";
}

/**
 * Validate the posted form. Returns the (trimmed) values either way so the
 * form can re-render with the visitor's input preserved, plus per-field
 * errors, or `null` when the submission is valid.
 */
export function parseWaitlistForm(form: FormData): {
  values: WaitlistFields;
  errors: WaitlistErrors | null;
} {
  const values: WaitlistFields = {
    name: clean(form, "name"),
    firm: clean(form, "firm"),
    email: clean(form, "email"),
    currentSoftware: clean(form, "currentSoftware"),
    message: clean(form, "message"),
  };

  const errors: WaitlistErrors = {};
  if (!values.name) errors.name = "Please tell us your name.";
  if (!values.firm) errors.firm = "Please tell us your firm's name.";
  if (!values.email) {
    errors.email = "A work email is required.";
  } else if (!EMAIL_RE.test(values.email)) {
    errors.email = "That doesn't look like a valid email address.";
  }
  for (const key of Object.keys(MAX) as (keyof typeof MAX)[]) {
    if (!errors[key] && values[key].length > MAX[key]) {
      errors[key] = `Please keep this under ${MAX[key]} characters.`;
    }
  }

  return { values, errors: Object.keys(errors).length > 0 ? errors : null };
}

/**
 * Thrown when the API rejects the submission with per-field validation
 * errors (HTTP 400). Should be rare — this layer validates with the same
 * rules first — but if the two ever drift, the API's verdict wins and the
 * form surfaces its field messages.
 */
export class WaitlistValidationError extends Error {
  constructor(readonly fields: WaitlistErrors) {
    super("The API rejected the submission");
    this.name = "WaitlistValidationError";
  }
}

/**
 * Forward one waitlist submission to the API.
 *
 * `INSOLVIA_API_BASE_URL` names the API origin — https://api.insolvia.ai in
 * production (set on the SSR Lambda by infra). When it is unset (local dev
 * without the API running), the submission is logged server-side and treated
 * as accepted rather than crashing the form. For a real local round-trip,
 * `services/api/scripts/dev-up.sh` serves the API at http://localhost:8080,
 * backed by this machine's real AWS dev table (./scripts/dev-aws-setup.sh):
 *
 *   INSOLVIA_API_BASE_URL=http://localhost:8080 npm run dev
 *
 * Responses: 201 → resolved; 400 → WaitlistValidationError with the API's
 * per-field messages; anything else (5xx, network failure) → the error
 * propagates and the route renders its generic try-again state.
 */
export async function putWaitlistSubmission(
  fields: WaitlistFields,
  host: string,
): Promise<void> {
  const body: Record<string, string> = {
    name: fields.name,
    firm: fields.firm,
    email: fields.email,
    host,
  };
  // Optional fields are omitted rather than sent as empty strings, matching
  // the API's omit-when-empty storage convention.
  if (fields.currentSoftware) body.currentSoftware = fields.currentSoftware;
  if (fields.message) body.message = fields.message;

  const baseUrl = process.env.INSOLVIA_API_BASE_URL;
  if (!baseUrl) {
    console.log(
      "[waitlist] INSOLVIA_API_BASE_URL is not set — logging submission instead of calling the API:",
      JSON.stringify(body),
    );
    return;
  }

  const response = await fetch(
    `${baseUrl.replace(/\/$/, "")}/v1/waitlist`,
    {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    },
  );

  if (response.status === 201) {
    // Log only the server-generated id — never the visitor's details.
    const { id } = (await response.json()) as { id?: string };
    console.log(`[waitlist] submission accepted by the API: ${id}`);
    return;
  }

  if (response.status === 400) {
    const payload = (await response.json().catch(() => null)) as {
      fields?: WaitlistErrors;
    } | null;
    if (payload?.fields) throw new WaitlistValidationError(payload.fields);
  }

  throw new Error(`API responded ${response.status}`);
}
