// Request/response models mirroring the Insolvia API's exact JSON contract.
//
// Field names match the wire format (camelCase, e.g. `currentSoftware`,
// `submittedAt`) as produced by `services/api` — index.ts names the files
// these mirror. The tests in this package pin that contract; change these
// models only together with the API.
//
// Plain interfaces rather than classes: the wire shape *is* the type, so a
// `toJson` on a response model would be the identity function. The one
// serializer that carries real behaviour — omitting absent optional request
// fields — is `waitlistSubmissionToJson` below.

/**
 * The `GET /health` response:
 * `{"status", "service", "version", "environment"}`.
 */
export interface HealthStatus {
  /** `"ok"` when the service is healthy. */
  readonly status: string;
  /** The service name (e.g. `insolvia-api`). */
  readonly service: string;
  /** The deployed `insolvia_api` package version. */
  readonly version: string;
  /**
   * The environment the API believes it is running in
   * (`local` / `staging` / `production`).
   */
  readonly environment: string;
}

/**
 * The `POST /v1/waitlist` request body.
 *
 * `name`, `firm`, and `email` are required by the API; the rest are optional
 * and omitted from the JSON entirely when absent (the API treats absent and
 * empty identically, but omitting keeps requests minimal and mirrors what
 * the marketing form sends).
 *
 * The optional fields are typed `string | undefined` rather than leaning on
 * `exactOptionalPropertyTypes` to forbid an explicit `undefined`: a form
 * hands you `string | undefined` values, and making every call site build
 * the object conditionally would scatter the omit-when-absent rule across
 * the codebase. The rule lives in exactly one place instead —
 * {@link waitlistSubmissionToJson}, which treats absent and `undefined`
 * identically and never emits `null` or `""`.
 */
export interface WaitlistSubmission {
  /** The submitter's name. Required; max 200 characters. */
  readonly name: string;
  /** The submitter's firm. Required; max 200 characters. */
  readonly firm: string;
  /** A work email address. Required; max 320 characters. */
  readonly email: string;
  /** The bankruptcy software the firm uses today. Optional; max 100. */
  readonly currentSoftware?: string | undefined;
  /** A free-text message. Optional; max 2000 characters. */
  readonly message?: string | undefined;
  /**
   * The serving host the submission came from (set server-to-server by the
   * marketing SSR action, not visitor input). Optional; max 253.
   */
  readonly host?: string | undefined;
}

/** The `POST /v1/waitlist` 201 response: `{"id", "submittedAt"}`. */
export interface WaitlistConfirmation {
  /** The server-generated submission id (a UUID). */
  readonly id: string;
  /**
   * The server's UTC submission timestamp, kept verbatim as the wire's
   * millisecond-precision ISO-8601 `Z` string (it doubles as a sort key
   * server-side). Use {@link submittedAtUtc} for a parsed value.
   */
  readonly submittedAt: string;
}

/**
 * The `POST /v1/waitlist` request body, with absent optional fields omitted
 * from the JSON entirely — never sent as `null` or `""`.
 */
export function waitlistSubmissionToJson(submission: WaitlistSubmission): Record<string, string> {
  const json: Record<string, string> = {
    name: submission.name,
    firm: submission.firm,
    email: submission.email,
  };
  // Explicit `if`s, not a conditional spread: this is the rule the contract
  // test pins ("omitted when absent"), and it should read as a rule.
  if (submission.currentSoftware !== undefined) {
    json.currentSoftware = submission.currentSoftware;
  }
  if (submission.message !== undefined) {
    json.message = submission.message;
  }
  if (submission.host !== undefined) {
    json.host = submission.host;
  }
  return json;
}

/** A {@link WaitlistConfirmation}'s `submittedAt` parsed as a `Date`. */
export function submittedAtUtc(confirmation: WaitlistConfirmation): Date {
  return new Date(confirmation.submittedAt);
}
