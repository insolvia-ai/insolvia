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
 * The `GET /v1/me` 200 response: the signed-in caller's identity, derived
 * purely from claims the access token already proved. The API makes no call
 * to Cognito to build it.
 *
 * **The body is camelCase, like every other endpoint** — `{"subject",
 * "username", "clientId", "scopes", "expiresAt"}`, matching `submittedAt` on
 * the waitlist route. The underlying JWT claims are snake_case (`client_id`,
 * `exp`), and the API translates at its edge rather than leaking claim
 * spelling into its own wire format. Models here mirror that wire exactly and
 * never translate again, so this file shows what actually goes over the
 * socket.
 *
 * **There is no email.** The Cognito pool sets
 * `username_attributes = ["email"]`, so an access token's `username` claim is
 * a pool-generated UUID and no address appears in any access-token claim.
 * Treat {@link username} as an opaque correlation id — never render it as an
 * email address. The app displays the address from the ID token it holds.
 */
export interface Principal {
  /**
   * The `sub` claim: the pool's stable, immutable user id. The only value
   * that should ever key user-owned data.
   */
  readonly subject: string;
  /**
   * Cognito's `username` claim — a generated UUID, **not** an email address.
   * `null` when the token carried no username.
   */
  readonly username: string | null;
  /** The Cognito app client the token was minted for. */
  readonly clientId: string;
  /** OAuth scopes on the token. Nothing enforces them server-side today. */
  readonly scopes: readonly string[];
  /**
   * The token's `exp` as a Unix timestamp in **seconds** (not milliseconds —
   * it is the JWT claim verbatim), or `null` when absent.
   */
  readonly expiresAt: number | null;
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

/**
 * The bankruptcy chapter a case is filed under. A union of numeric literals
 * rather than `number`, so an invalid chapter is a compile error at the call
 * site, not just a server-side rejection.
 */
export type CaseChapter = 7 | 11 | 12 | 13;

/**
 * A case's position in the filing workflow.
 *
 * A union of string literals rather than an `enum`, because `erasableSyntaxOnly`
 * is on (see the root `tsconfig.base.json`) — the same reason
 * {@link UnauthorizedSource} in `exceptions.ts` is one.
 */
export type CaseStatus = 'intake' | 'ready_to_file' | 'filed';

/**
 * A case, as returned by every `/v1/cases` endpoint:
 * `{"id", "chapter", "district", "status", "createdAt", "updatedAt"}`.
 */
export interface Case {
  /** The server-generated case id. */
  readonly id: string;
  /** The bankruptcy chapter. */
  readonly chapter: CaseChapter;
  /** The filing district. */
  readonly district: string;
  /** Where the case sits in the filing workflow. */
  readonly status: CaseStatus;
  /** The server's UTC creation timestamp, kept verbatim as the wire string. */
  readonly createdAt: string;
  /** The server's UTC last-update timestamp, kept verbatim as the wire string. */
  readonly updatedAt: string;
}

/** The `POST /v1/cases` request body: `{"chapter", "district"}`, both required. */
export interface CreateCaseRequest {
  /** The bankruptcy chapter. */
  readonly chapter: CaseChapter;
  /** The filing district. */
  readonly district: string;
}

/** The `POST /v1/cases` request body, verbatim — both fields are required. */
export function createCaseRequestToJson(request: CreateCaseRequest): Record<string, unknown> {
  return {
    chapter: request.chapter,
    district: request.district,
  };
}

/**
 * `GET /v1/cases` query options. Both optional and, per this package's rule,
 * omitted from the query string entirely when absent — see
 * {@link listCasesQuery}.
 */
export interface ListCasesOptions {
  /** Maximum number of cases to return. */
  readonly limit?: number | undefined;
  /** An opaque pagination cursor from a previous {@link ListCasesResult.nextCursor}. */
  readonly cursor?: string | undefined;
}

/**
 * `GET /v1/cases` query options rendered as `URLSearchParams`, with absent
 * fields omitted entirely — never sent as an empty or literal `"undefined"`
 * value. Mirrors {@link waitlistSubmissionToJson}'s omit-when-absent rule, at
 * the query string instead of the body.
 */
export function listCasesQuery(options: ListCasesOptions): URLSearchParams {
  const params = new URLSearchParams();
  if (options.limit !== undefined) {
    params.set('limit', String(options.limit));
  }
  if (options.cursor !== undefined) {
    params.set('cursor', options.cursor);
  }
  return params;
}

/**
 * The `GET /v1/cases` 200 response: `{"cases", "nextCursor"?}`.
 *
 * {@link nextCursor} is **absent, not `null`**, when there are no more pages —
 * mirroring the wire exactly, the same convention this package's request
 * models use for absent optional fields.
 */
export interface ListCasesResult {
  /** The page of cases. */
  readonly cases: readonly Case[];
  /** An opaque cursor for the next page, or absent on the last page. */
  readonly nextCursor?: string | undefined;
}

/**
 * The `PATCH /v1/cases/{caseId}` request body: any subset of `{"chapter",
 * "district", "status"}`. An omitted key means "leave unchanged" — the client
 * must not send keys the caller did not supply, so {@link updateCaseChangesToJson}
 * omits them rather than sending `null`.
 */
export interface UpdateCaseChanges {
  /** A new chapter, or omit to leave it unchanged. */
  readonly chapter?: CaseChapter | undefined;
  /** A new district, or omit to leave it unchanged. */
  readonly district?: string | undefined;
  /** A new status, or omit to leave it unchanged. */
  readonly status?: CaseStatus | undefined;
}

/**
 * The `PATCH /v1/cases/{caseId}` request body, with absent optional fields
 * omitted from the JSON entirely — never sent as `null`.
 */
export function updateCaseChangesToJson(changes: UpdateCaseChanges): Record<string, unknown> {
  const json: Record<string, unknown> = {};
  // Explicit `if`s, not a conditional spread: matches
  // `waitlistSubmissionToJson`'s style, and this is the rule the contract
  // test pins ("omitted keys mean leave unchanged").
  if (changes.chapter !== undefined) {
    json.chapter = changes.chapter;
  }
  if (changes.district !== undefined) {
    json.district = changes.district;
  }
  if (changes.status !== undefined) {
    json.status = changes.status;
  }
  return json;
}
