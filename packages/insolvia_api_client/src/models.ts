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

// ---------------------------------------------------------------------------
// Case documents — mirrors services/api/src/insolvia_api/core/documents.py
// (`document_json`, `KINDS`, `CONTENT_TYPES`, `STATUSES`, `MAX_BYTE_SIZE`) and
// api/routes/documents.py.
// ---------------------------------------------------------------------------

/**
 * What the uploader says a document is. The exact `KINDS` tuple from
 * `core/documents.py`, in the same order, because the API's 400 message lists
 * them in it.
 *
 * A **runtime array first**, with the type derived from it, rather than a
 * hand-written union: the array is the thing a file picker needs to render its
 * options, and deriving the type means the list and the type cannot drift.
 * (`as const` + an indexed access, not an `enum` — `erasableSyntaxOnly` is on;
 * see {@link CaseStatus}.)
 *
 * It is a **claim**, not a verified fact: nothing server-side reads the bytes,
 * so a PDF labelled `pay_stub` may be anything. A caller that genuinely does
 * not know sends `'other'`.
 */
export const DOCUMENT_KINDS = [
  'credit_report',
  'pay_stub',
  'bank_statement',
  'tax_return',
  'identification',
  'court_notice',
  'other',
] as const;

/** One of {@link DOCUMENT_KINDS}. */
export type DocumentKind = (typeof DOCUMENT_KINDS)[number];

/**
 * Narrows an arbitrary string to a {@link DocumentKind} — for a caller holding
 * a value from outside the type system (a picker's `value`, a stored draft).
 */
export function isDocumentKind(value: string): value is DocumentKind {
  return (DOCUMENT_KINDS as readonly string[]).includes(value);
}

/**
 * The content types the API accepts, verbatim from `CONTENT_TYPES` in
 * `core/documents.py`. An allowlist there and here: anything else is a 400
 * with a `contentType` field message.
 *
 * The API lowercases what it is sent and signs the **normalised** spelling, so
 * these are lowercase and a caller must not send `Application/PDF` — the
 * mismatch would surface as an opaque `SignatureDoesNotMatch` on the PUT.
 * Use {@link isDocumentContentType} to narrow a `File.type`, which is a plain
 * `string` and already lowercase per the File API.
 */
export const DOCUMENT_CONTENT_TYPES = [
  'application/pdf',
  'image/jpeg',
  'image/png',
  'image/heic',
  'image/tiff',
] as const;

/** One of {@link DOCUMENT_CONTENT_TYPES}. */
export type DocumentContentType = (typeof DOCUMENT_CONTENT_TYPES)[number];

/**
 * Narrows an arbitrary string — typically a `File.type` — to a
 * {@link DocumentContentType}, so a rejected file can be reported at the
 * picker instead of after a round trip.
 */
export function isDocumentContentType(value: string): value is DocumentContentType {
  return (DOCUMENT_CONTENT_TYPES as readonly string[]).includes(value);
}

/**
 * 50 MiB — `MAX_BYTE_SIZE` in `core/documents.py`. Exported so a picker can
 * refuse an oversized file before a request, and because the limit is bound
 * into the presigned signature: a larger body is refused by S3, not by us.
 */
export const MAX_DOCUMENT_BYTE_SIZE = 50 * 1024 * 1024;

/**
 * Whether a document's bytes are known to be in the bucket.
 *
 * - `'pending'` — an upload was authorised and a capability minted. Nothing
 *   knows whether the bytes landed, and `byteSize` is still the client's own
 *   claim.
 * - `'stored'` — the server saw the object. `byteSize` is what S3 counted.
 *
 * **This is the field a client branches on.** A `'pending'` row is not noise
 * to be filtered out — it is the case's record of an upload that did not
 * finish, and the UI showing it as "upload didn't finish, retry" is the whole
 * reason the API lists it.
 *
 * Derived from a runtime array for the same reason {@link DOCUMENT_KINDS} is.
 */
export const DOCUMENT_STATUSES = ['pending', 'stored'] as const;

/** One of {@link DOCUMENT_STATUSES}. */
export type DocumentStatus = (typeof DOCUMENT_STATUSES)[number];

/**
 * A document of a case, as `document_json` renders it:
 * `{"id", "caseId", "kind", "fileName", "contentType", "byteSize",
 * "uploadedAt", "status"}`.
 *
 * `uploadedBy`, `storageRef` and `etag` are stored server-side and
 * deliberately not in the body — the object key and its etag are the API's
 * business, and a client that knew the layout could come to depend on it.
 *
 * {@link kind} and {@link contentType} are plain strings here while the
 * *request* models use the unions above, and the asymmetry is deliberate: a
 * value the caller is about to send should be a compile error when it is not
 * one the API accepts, but a value the API sends back must not be able to
 * break decoding of a whole page of documents if the server's allowlist ever
 * grows. {@link status} is the exception — a closed two-value set the UI
 * branches on, where an unrecognised value means the client cannot tell "you
 * can open this" from "this never uploaded", and failing loudly beats
 * guessing.
 */
export interface Document {
  /** The server-generated document id. */
  readonly id: string;
  /** The case this document belongs to. */
  readonly caseId: string;
  /** The uploader's claim about what this is — see {@link DOCUMENT_KINDS}. */
  readonly kind: string;
  /** The uploader's file name, for display and as a download name. */
  readonly fileName: string;
  /** The validated, lowercased media type — see {@link DOCUMENT_CONTENT_TYPES}. */
  readonly contentType: string;
  /**
   * The size in bytes. **Its meaning changes with {@link status}**: on a
   * `'pending'` record it is what the client said it would send, on a
   * `'stored'` one it is what S3 counted.
   */
  readonly byteSize: number;
  /**
   * When the upload was **authorised** — not when the bytes landed. The
   * server's UTC millisecond-precision ISO-8601 `Z` string, kept verbatim.
   */
  readonly uploadedAt: string;
  /** Whether the bytes are known to be in the bucket. */
  readonly status: DocumentStatus;
}

/**
 * The `POST /v1/cases/{caseId}/documents` request body: `{"kind", "fileName",
 * "contentType", "byteSize"}`, all four required.
 *
 * `contentType` and `byteSize` are validated here and then **bound into the
 * presigned signature**, so they are not merely declarations: an upload whose
 * body is a different length, or whose `Content-Type` header differs, is
 * refused by S3 with a signature error the client cannot interpret. Send the
 * real values.
 *
 * There is no `provenance` field and the API refuses one: provenance describes
 * values extracted *from* a document, not the document itself.
 */
export interface CreateDocumentRequest {
  /** What the uploader says this is. */
  readonly kind: DocumentKind;
  /** The file name, 1-255 characters, no path separators or invisible characters. */
  readonly fileName: string;
  /** The media type, lowercase, from the allowlist. */
  readonly contentType: DocumentContentType;
  /** The exact size of the bytes to be uploaded, 1..{@link MAX_DOCUMENT_BYTE_SIZE}. */
  readonly byteSize: number;
}

/** The `POST /v1/cases/{caseId}/documents` request body, verbatim — no field is optional. */
export function createDocumentRequestToJson(
  request: CreateDocumentRequest,
): Record<string, unknown> {
  return {
    kind: request.kind,
    fileName: request.fileName,
    contentType: request.contentType,
    byteSize: request.byteSize,
  };
}

/**
 * The capability minted alongside a new document record: one HTTP request,
 * to one object, for a few minutes.
 *
 * **{@link headers} is passed through untouched and is not a fixed shape.**
 * The server chooses which headers it signs (today: `Content-Type`, the
 * server-side-encryption header, and the tag that makes an unconfirmed upload
 * reapable), and S3 checks every one. Modelling it as an interface with named
 * keys would mean a header added server-side was silently dropped by every
 * deployed client, turning every upload into a 403 that looks like a bug here.
 * Send this map verbatim, add nothing, and in particular never add an
 * `Authorization` header — the URL is already the credential, and one more
 * would invalidate it.
 *
 * `Content-Length` is signed too and is deliberately *not* in this map: every
 * HTTP client sets it from the body, and browsers forbid JavaScript from
 * setting it at all.
 */
export interface DocumentUpload {
  /** The presigned URL. Treat it as a secret: it is a bearer capability. */
  readonly url: string;
  /** The one verb the signature permits — `PUT`. */
  readonly method: string;
  /** The exact headers the signature demands. Send all of them, change none. */
  readonly headers: Readonly<Record<string, string>>;
  /** When the URL stops working, as a UTC ISO-8601 `Z` string. */
  readonly expiresAt: string;
}

/**
 * The `POST /v1/cases/{caseId}/documents` 201 response:
 * `{"document", "upload"}`.
 *
 * The record is `'pending'` — nothing has landed yet. See
 * {@link InsolviaApiClient.uploadDocument} for what still has to happen, and
 * what it costs to stop here.
 */
export interface CreateDocumentResult {
  /** The created record, always `status: 'pending'`. */
  readonly document: Document;
  /** Where and how to put the bytes. */
  readonly upload: DocumentUpload;
}

/**
 * The `GET /v1/cases/{caseId}/documents/{documentId}/url` 200 response:
 * `{"url", "method", "expiresAt"}` — a short-lived capability to read one
 * document's bytes.
 *
 * Minted on request rather than attached to every listed document, so one of
 * these means one document was actually fetched. It expires in minutes; ask
 * again rather than caching it.
 */
export interface DocumentDownload {
  /** The presigned URL. A bearer capability — do not log it. */
  readonly url: string;
  /** The one verb the signature permits — `GET`. */
  readonly method: string;
  /** When the URL stops working, as a UTC ISO-8601 `Z` string. */
  readonly expiresAt: string;
}

/**
 * Everything {@link InsolviaApiClient.uploadDocument} needs to run the whole
 * create → PUT → complete sequence.
 *
 * **There is no `byteSize`.** It is read from `file.size`, and that is not a
 * convenience: the declared size is bound into the presigned signature, so a
 * number that disagreed with the body would produce a 403 from S3 with nothing
 * in it to explain why. The one value that cannot be wrong is the one taken
 * from the bytes themselves.
 */
export interface UploadDocumentOptions {
  /**
   * The bytes. A `File` from an input or a `Blob` built from one; both carry
   * the `size` this needs.
   */
  readonly file: Blob;
  /**
   * The name to show and to download under. Not taken from `File.name`
   * automatically: the API refuses path separators and invisible characters,
   * and a caller that picked the name deliberately gets a better error than
   * one that inherited it.
   */
  readonly fileName: string;
  /** What the uploader says this is. */
  readonly kind: DocumentKind;
  /**
   * The media type. Required rather than defaulted from `file.type`, which is
   * `string` and can be empty or something the API refuses — narrow it with
   * {@link isDocumentContentType} at the picker, where the user can choose a
   * different file.
   */
  readonly contentType: DocumentContentType;
}
