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

// ---------------------------------------------------------------------------
// Debtors — B101 Part 1, and the record the rest of a case hangs off. The
// contract lives in `services/api/src/insolvia_api/core/debtors.py` (the
// shapes and the enums), `.../api/routes/debtors.py` (the two endpoints), and
// `.../core/provenance.py` (the `provenance` map every case-scoped record
// carries).
//
// THESE MODELS ARE snake_case WHERE EVERYTHING ABOVE IS camelCase, and that is
// the wire being mirrored rather than drift in this file. `case_json` emits
// `createdAt`; `debtor_json` emits `created_at`, `filing_role`,
// `other_names_used`. This package's rule is that a model shows what actually
// goes over the socket, so it follows the endpoint it mirrors instead of
// smoothing the two spellings together — a client that translated would be the
// only place in the system where a debtor field has two names.
//
// Renaming them to camelCase here would also be unsafe, for a reason that has
// nothing to do with taste. A debtor's `provenance` map is keyed by DOTTED
// PATHS INTO THIS RECORD — `name.given`, `residence_address.line1`,
// `other_names_used[n1].surname` — and the server's grammar for a path segment
// is `[a-z][a-z0-9_]*`, which rejects a capital letter outright. A client whose
// model said `residenceAddress` would build the key `residenceAddress.line1`,
// the API would refuse it as "Not a field path", and the caller would be stuck:
// the value requires provenance and the only key that describes it is refused.
// One spelling, and it is the server's.
// ---------------------------------------------------------------------------

// The four enums below are declared as `as const` arrays with the union
// *derived* from them, unlike {@link CaseStatus} above, which is a bare union.
// Two reasons: the app renders each of these as a picker and needs the options
// at runtime (a `CaseStatus` is chosen by workflow, not from a list), and
// deriving the type means the runtime list and the compile-time union cannot
// drift apart the way two hand-written copies would.

/**
 * One record per role per case. `FILING_ROLES` mirrors `core/debtors.py`, and
 * the ORDER is meaningful: it is the order the forms print debtors in.
 *
 * `non_filing_spouse` is a role rather than a flag because form 106I's second
 * column may belong to a spouse who is not filing at all.
 */
export const FILING_ROLES = ['debtor_1', 'debtor_2', 'non_filing_spouse'] as const;

/** Which debtor of a case a record is. See {@link FILING_ROLES}. */
export type FilingRole = (typeof FILING_ROLES)[number];

/** B101 line 6. `other` carries the explanation the form asks for. */
export const VENUE_BASES = ['lived_longest_180_days', 'other'] as const;

/** The basis for filing in this district. See {@link VENUE_BASES}. */
export type VenueBasis = (typeof VENUE_BASES)[number];

/**
 * The four checkboxes of B101 line 15, named rather than numbered so a form
 * revision that reorders them does not silently change stored meanings.
 */
export const COUNSELING_STATUSES = [
  'completed_with_certificate',
  'completed_certificate_pending',
  'exigent_circumstances_waiver_requested',
  'not_required',
] as const;

/** Where the debtor stands on credit counseling. See {@link COUNSELING_STATUSES}. */
export type CounselingStatus = (typeof COUNSELING_STATUSES)[number];

/** The form's three grounds. Only meaningful with status `not_required`. */
export const COUNSELING_EXEMPTIONS = ['incapacity', 'disability', 'active_duty'] as const;

/** Why credit counseling was not required. See {@link COUNSELING_EXEMPTIONS}. */
export type CounselingExemption = (typeof COUNSELING_EXEMPTIONS)[number];

/**
 * Who supplied a value.
 *
 * `imported` sits with `ai_extracted` rather than with `staff_typed`: machine-
 * supplied is machine-supplied, and the source system does not change who is
 * signing the form. Both are subject to the confirm-before-entry rule below.
 */
export const PROVENANCE_SOURCES = ['staff_typed', 'ai_extracted', 'imported'] as const;

/** Who supplied a value. See {@link PROVENANCE_SOURCES}. */
export type ProvenanceSource = (typeof PROVENANCE_SOURCES)[number];

/**
 * Where one field's value came from.
 *
 * **`confirmed_by` and `confirmed_at` are one act, not two fields** — a human
 * said "yes, that is right" at a moment — and the API enforces that:
 * a `ai_extracted` or `imported` entry with either half missing is rejected
 * with a 400, because "nothing extracted enters the case until a human
 * confirms it" is a property of the store rather than a promise about the UI.
 * A `staff_typed` entry needs neither: the person typing it *is* the
 * confirmation.
 *
 * Every member but {@link source} is omitted from the wire when absent, in
 * both directions — see {@link putDebtorRequestToJson}.
 */
export interface ProvenanceEntry {
  /** Who supplied the value. */
  readonly source: ProvenanceSource;
  /** The person who confirmed it. Required when {@link source} is machine-supplied. */
  readonly confirmed_by?: string | undefined;
  /**
   * When it was confirmed, as a UTC ISO-8601 `Z` string. Required when
   * {@link source} is machine-supplied, and the API parses it — a string of
   * the right shape that is not a real instant is refused.
   */
  readonly confirmed_at?: string | undefined;
  /** The document the value was read from. */
  readonly document_id?: string | undefined;
  /** Where in that document — an opaque map the API stores but does not interpret. */
  readonly locator?: Readonly<Record<string, unknown>> | undefined;
  /** The extraction run that produced the value. */
  readonly extraction_id?: string | undefined;
  /** The extractor's confidence, between 0 and 1 inclusive. */
  readonly confidence?: number | undefined;
}

/**
 * A record's provenance, keyed by **dotted field path** — `name.given`,
 * `credit_counseling.status`, `other_names_used[n1].surname`.
 *
 * A record type rather than a fixed-shape interface, because the key set is
 * open: it is one entry per *populated* field of one particular debtor, and
 * list elements contribute paths named after ids the client chose. Nothing
 * here can be modelled as a property list.
 *
 * Two API rules make this map load-bearing rather than metadata, and both
 * surface as a 400 rather than as a silent drop:
 *
 * 1. **Every populated field carries an entry.** A record with a value and no
 *    entry for it is rejected. {@link staffTypedProvenance} builds a conforming
 *    map so callers never hand-write these keys.
 * 2. **Machine-supplied values must be confirmed by a person.** See
 *    {@link ProvenanceEntry}.
 */
export type ProvenanceMap = Readonly<Record<string, ProvenanceEntry>>;

/** A debtor's legal name. Every part is optional — intake is progressive. */
export interface PersonName {
  readonly given?: string | undefined;
  readonly middle?: string | undefined;
  readonly surname?: string | undefined;
  /** Max 20 characters, where the other parts allow 200. */
  readonly suffix?: string | undefined;
}

/**
 * An 8-year-lookback alias.
 *
 * **{@link id} is required here even though the API will generate one**, and
 * that is not defensiveness — a generated id makes the request impossible to
 * satisfy. Provenance addresses this row as `other_names_used[<id>].surname`,
 * so a client that omits the id cannot write provenance for the fields it is
 * sending, and the API rejects any populated field with no provenance. The id
 * must therefore be the client's, chosen before the request. It has to match
 * `[A-Za-z0-9_-]+`; anything else is refused rather than replaced.
 */
export interface OtherName {
  /** The client-chosen row id. Matches `[A-Za-z0-9_-]+`. See above. */
  readonly id: string;
  readonly given?: string | undefined;
  readonly middle?: string | undefined;
  readonly surname?: string | undefined;
  readonly business_name?: string | undefined;
}

/** A postal address. Every part is optional. */
export interface Address {
  readonly line1?: string | undefined;
  readonly line2?: string | undefined;
  readonly city?: string | undefined;
  /** Max 40 characters. */
  readonly state?: string | undefined;
  /** Max 12 characters. */
  readonly postal_code?: string | undefined;
}

/** B101 line 6: why this district. */
export interface Venue {
  readonly basis?: VenueBasis | undefined;
  /** The explanation the form asks for when {@link basis} is `other`. Max 1000. */
  readonly explanation?: string | undefined;
}

/** B101 line 15. */
export interface CreditCounseling {
  readonly status?: CounselingStatus | undefined;
  /** Only meaningful when {@link status} is `not_required`. */
  readonly exemption_reason?: CounselingExemption | undefined;
}

/**
 * A debtor's case data — everything except server-stamped identity and the
 * provenance map. Shared by {@link PutDebtorRequest} and {@link Debtor}
 * because the API sends back exactly what it accepts.
 *
 * **Every field is optional, by construction rather than by omission.** Intake
 * is progressive: a half-finished questionnaire has to persist, so the API
 * validates shape and type only and accepts absent values everywhere.
 * Completeness against a given chapter's forms is a pre-filing check the forms
 * engine owns, not something this request can fail.
 */
export interface DebtorBody {
  readonly name?: PersonName | undefined;
  /** Aliases used in the last 8 years. Each row needs a client-chosen {@link OtherName.id}. */
  readonly other_names_used?: readonly OtherName[] | undefined;
  /** Employer identification numbers. Each max 20 characters. */
  readonly employer_ids?: readonly string[] | undefined;
  readonly residence_address?: Address | undefined;
  readonly mailing_address?: Address | undefined;
  /** Max 40 characters. */
  readonly phone?: string | undefined;
  /** Max 40 characters. */
  readonly mobile?: string | undefined;
  readonly email?: string | undefined;
  readonly venue?: Venue | undefined;
  readonly credit_counseling?: CreditCounseling | undefined;
  /**
   * The signature date, `YYYY-MM-DD` — a calendar fact, not an instant, so
   * no time and no zone (see docs/reference/case-data-model.md). The API
   * parses it as a real date and rejects anything else, including a
   * timestamp and the compact `20260805` form. `DateInput` in
   * `@insolvia-ai/design-system` emits exactly this.
   */
  readonly signed_at?: string | undefined;
}

/**
 * The `PUT /v1/cases/{caseId}/debtors/{filingRole}` request body: a
 * {@link DebtorBody} plus its {@link provenance}.
 *
 * **Whole, not partial.** The endpoint is a PUT and replaces the record, so
 * anything left out is gone rather than left alone. That is a consequence of
 * the provenance rule rather than a preference: "every populated field carries
 * provenance" can only be checked against a complete record, and a partial
 * write would have to merge against the stored copy and re-derive the rule
 * afterwards. The questionnaire holds the whole record client-side anyway, so
 * autosave sends it.
 *
 * **There is no `tax_id`.** The API rejects one explicitly with a 400 rather
 * than ignoring it: the SSN/ITIN has to be stored encrypted with only the last
 * four ever served, and that encryption is not built. Refusing it is the
 * honest failure — silently dropping it would leave the client believing a tax
 * id had been stored.
 */
export interface PutDebtorRequest extends DebtorBody {
  /**
   * Where each populated field came from. Omitted when empty, which is what an
   * entirely blank record sends. Build it with {@link staffTypedProvenance}
   * rather than by hand.
   */
  readonly provenance?: ProvenanceMap | undefined;
}

/**
 * A debtor as both endpoints return it: identity, {@link provenance}, and
 * whatever of {@link DebtorBody} is populated.
 *
 * **Absent members are absent, not null.** On a progressive intake most of the
 * record is empty most of the time, and the API omits empty values — and empty
 * sub-objects and empty lists — entirely rather than sending a body of nulls.
 * This model mirrors that: a missing `name` means nothing in the name has been
 * filled in yet, and the key is genuinely not there.
 */
export interface Debtor extends DebtorBody {
  /** The server-generated debtor id, stable across saves. */
  readonly id: string;
  /** The case this debtor belongs to. */
  readonly case_id: string;
  /** Which debtor of the case this is. */
  readonly filing_role: FilingRole;
  /** The server's UTC creation timestamp, kept verbatim as the wire string. */
  readonly created_at: string;
  /** The server's UTC last-update timestamp, kept verbatim as the wire string. */
  readonly updated_at: string;
  /**
   * Where each populated field came from. **Always present**, unlike every
   * other optional member here — the API emits it unconditionally, as `{}` on
   * a record with nothing in it.
   */
  readonly provenance: ProvenanceMap;
}

/**
 * The `PUT /v1/cases/{caseId}/debtors/{filingRole}` request body, with absent
 * members omitted from the JSON entirely — never sent as `null` or `{}`.
 *
 * The pruning is recursive and mirrors `_prune` in `core/debtors.py`: an
 * `undefined` member is dropped, and a sub-object or list that ends up empty
 * is dropped with it. That is not just tidiness — it makes the request body
 * and the response body the same shape, so a record sent and the record
 * returned compare equal instead of differing by a scatter of empty objects.
 *
 * An empty list and an absent list mean the same thing to this endpoint (the
 * PUT replaces the record either way), so dropping an empty one loses nothing.
 *
 * `provenance` entries are pruned by a different rule — see
 * {@link provenanceToJson}.
 */
export function putDebtorRequestToJson(request: PutDebtorRequest): Record<string, unknown> {
  return assignDefined(
    {},
    {
      name: personNameToJson(request.name),
      other_names_used: otherNamesToJson(request.other_names_used),
      employer_ids: stringListToJson(request.employer_ids),
      residence_address: addressToJson(request.residence_address),
      mailing_address: addressToJson(request.mailing_address),
      phone: request.phone,
      mobile: request.mobile,
      email: request.email,
      venue: venueToJson(request.venue),
      credit_counseling: creditCounselingToJson(request.credit_counseling),
      signed_at: request.signed_at,
      provenance: provenanceToJson(request.provenance),
    },
  );
}

/**
 * Copies the members of `members` that have a value onto `target`, and returns
 * it.
 *
 * The one place the omit-when-absent rule is applied to a debtor body, so it
 * reads as a rule the way {@link waitlistSubmissionToJson}'s explicit `if`s do
 * — a debtor has eleven body members across five nested shapes, and eleven
 * hand-written `if`s would obscure it rather than state it.
 */
function assignDefined(
  target: Record<string, unknown>,
  members: Record<string, unknown>,
): Record<string, unknown> {
  for (const [key, value] of Object.entries(members)) {
    if (value !== undefined) {
      target[key] = value;
    }
  }
  return target;
}

/**
 * {@link assignDefined} into a fresh object, or `undefined` when no member had
 * a value — which is how an all-empty sub-object comes to be omitted rather
 * than sent as `{}`.
 */
function definedMembersOrUndefined(
  members: Record<string, unknown>,
): Record<string, unknown> | undefined {
  const json = assignDefined({}, members);
  return Object.keys(json).length === 0 ? undefined : json;
}

function personNameToJson(name: PersonName | undefined): Record<string, unknown> | undefined {
  if (name === undefined) {
    return undefined;
  }
  return definedMembersOrUndefined({
    given: name.given,
    middle: name.middle,
    surname: name.surname,
    suffix: name.suffix,
  });
}

function addressToJson(address: Address | undefined): Record<string, unknown> | undefined {
  if (address === undefined) {
    return undefined;
  }
  return definedMembersOrUndefined({
    line1: address.line1,
    line2: address.line2,
    city: address.city,
    state: address.state,
    postal_code: address.postal_code,
  });
}

function venueToJson(venue: Venue | undefined): Record<string, unknown> | undefined {
  if (venue === undefined) {
    return undefined;
  }
  return definedMembersOrUndefined({ basis: venue.basis, explanation: venue.explanation });
}

function creditCounselingToJson(
  counseling: CreditCounseling | undefined,
): Record<string, unknown> | undefined {
  if (counseling === undefined) {
    return undefined;
  }
  return definedMembersOrUndefined({
    status: counseling.status,
    exemption_reason: counseling.exemption_reason,
  });
}

/**
 * Alias rows, each keeping its `id` — the row's address, which provenance
 * paths already reference and which the server will not replace.
 */
function otherNamesToJson(
  names: readonly OtherName[] | undefined,
): Record<string, unknown>[] | undefined {
  if (names === undefined || names.length === 0) {
    return undefined;
  }
  return names.map((name) =>
    assignDefined(
      { id: name.id },
      {
        given: name.given,
        middle: name.middle,
        surname: name.surname,
        business_name: name.business_name,
      },
    ),
  );
}

function stringListToJson(values: readonly string[] | undefined): string[] | undefined {
  return values === undefined || values.length === 0 ? undefined : [...values];
}

/**
 * The `provenance` map, or `undefined` when it holds nothing — the API treats
 * an absent map and an empty one identically.
 *
 * Entries are pruned of their **absent** members only, which is a deliberately
 * weaker rule than the body's: `provenance_json` server-side drops nulls and
 * nothing else, so an explicitly empty `locator: {}` survives a round trip and
 * dropping it here would make a re-sent record differ from the one received.
 */
function provenanceToJson(
  provenance: ProvenanceMap | undefined,
): Record<string, unknown> | undefined {
  if (provenance === undefined) {
    return undefined;
  }
  const json: Record<string, unknown> = {};
  for (const [path, entry] of Object.entries(provenance)) {
    json[path] = assignDefined(
      { source: entry.source },
      {
        confirmed_by: entry.confirmed_by,
        confirmed_at: entry.confirmed_at,
        document_id: entry.document_id,
        locator: entry.locator,
        extraction_id: entry.extraction_id,
        confidence: entry.confidence,
      },
    );
  }
  return Object.keys(json).length === 0 ? undefined : json;
}

/**
 * What {@link staffTypedProvenance} will walk.
 *
 * The union has three members and needs all three: an `interface` is **not**
 * assignable to `Record<string, unknown>` in TypeScript (only a type alias
 * gets an implicit index signature), so naming the record type alone would
 * reject the two shapes callers actually hold, and naming the two alone would
 * reject the loose objects a caller assembles from a form.
 */
export type DebtorBodyLike = PutDebtorRequest | Debtor | Readonly<Record<string, unknown>>;

/**
 * A `provenance` map that says "a person typed this" for every populated field
 * of `body` — the map an ordinary questionnaire save needs, without the caller
 * ever writing a path by hand.
 *
 * The API rejects any body where a populated field has no provenance entry, so
 * a client that does not build this map cannot save anything. Getting the walk
 * subtly wrong produces a 400 naming a path the caller never wrote, which is
 * close to undiagnosable from the app — which is why it lives here rather than
 * being reimplemented per client.
 *
 * ```ts
 * const body = { name: { given: 'Ada' } };
 * await client.putDebtor(caseId, 'debtor_1', { ...body, provenance: staffTypedProvenance(body) });
 * ```
 *
 * `staff_typed` needs no confirmation — the person typing it *is* the
 * confirmation. For a value that came from extraction or an import, write that
 * entry yourself: it needs a `confirmed_by` and a `confirmed_at`, and inventing
 * those would defeat the rule this helper exists to satisfy.
 *
 * Identity and the provenance map itself are skipped, exactly as
 * `debtor_body()` skips them server-side, so a {@link Debtor} fetched from the
 * API can be handed straight back in.
 *
 * **This walk is duplicated from the server on purpose.** `populated_paths` in
 * `core/provenance.py` is the authority and re-runs on every write, so the two
 * can only disagree by rejecting a request — never by storing something
 * unattributed. It is copied here so the caller does not have to guess, and
 * the two must be changed together; the tests alongside this package mirror
 * the server's own, case for case, so a divergence fails here first.
 *
 * @throws Error — before any request — when a key is not a legal field name
 * (`[a-z][a-z0-9_]*`), because no provenance path could ever describe it. The
 * API answers 400 for the same body; failing here names the key.
 */
export function staffTypedProvenance(body: DebtorBodyLike): Record<string, ProvenanceEntry> {
  const entries: Record<string, ProvenanceEntry> = {};
  for (const path of populatedPaths(caseDataOf(body))) {
    entries[path] = { source: 'staff_typed' };
  }
  return entries;
}

// Stamped by the server or already the record's address, so there is nothing
// to record the origin of. The same six keys `debtor_body()` drops.
const NOT_CASE_DATA: readonly string[] = [
  'id',
  'case_id',
  'filing_role',
  'created_at',
  'updated_at',
  'provenance',
];

function caseDataOf(body: DebtorBodyLike): unknown {
  if (!isPlainObject(body)) {
    return body;
  }
  return Object.fromEntries(Object.entries(body).filter(([key]) => !NOT_CASE_DATA.includes(key)));
}

/** A field path segment. Note the lower-case start: `SSN` and `legalName` are not names. */
const FIELD_NAME_RE = /^[a-z][a-z0-9_]*$/;

/** What a list element's `id` has to look like to be usable as an address. */
const ADDRESSABLE_ID_RE = /^[A-Za-z0-9_-]+$/;

function isPlainObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/**
 * Every field path in `record` that actually holds a value — the TypeScript
 * half of `populated_paths` in `core/provenance.py`. See
 * {@link staffTypedProvenance} for why it is duplicated.
 *
 * The definition of "populated" is the whole of it:
 *
 * - `null` and `undefined` are absent. Most of a progressive intake is empty
 *   most of the time, and requiring provenance for nothing would make an empty
 *   record unsavable.
 * - An empty string, list or object is **also** absent. A field the user
 *   cleared has no origin to record, and demanding one would mean writing
 *   provenance for the act of deleting.
 * - `false` and `0` are **present**. They are answers — "no, I do not rent my
 *   residence" is a fact someone asserted, and exactly the kind of answer an
 *   extraction can get wrong. A `if (!value)` here is the classic bug this
 *   note exists to prevent, and there is a test named after it on both sides.
 *
 * Nested objects recurse into dotted paths; lists recurse into their elements
 * addressed by the element's own `id`, so a reorder does not move provenance
 * onto a different value. That `id` is never itself emitted: it *is* the
 * address, and asking where an address came from is not a question about the
 * case.
 */
function populatedPaths(record: unknown, prefix = ''): string[] {
  const paths: string[] = [];

  if (isPlainObject(record)) {
    for (const [key, value] of Object.entries(record)) {
      // The element's own id, already spent as the address in `prefix`.
      if (key === 'id' && prefix.endsWith(']')) {
        continue;
      }
      if (!FIELD_NAME_RE.test(key)) {
        // Loud, and here rather than at the API: a key like `legalName` or
        // `1099_income` would otherwise mint a REQUIRED path that the server's
        // provenance parser then refuses as a key, and no payload could
        // satisfy both.
        throw new Error(
          `"${prefix === '' ? key : `${prefix}.${key}`}" is not a field name, so nothing can ` +
            'record where it came from',
        );
      }
      paths.push(...populatedPaths(value, prefix === '' ? key : `${prefix}.${key}`));
    }
    return paths;
  }

  if (typeof record === 'string') {
    return record === '' || prefix === '' ? [] : [prefix];
  }

  if (Array.isArray(record)) {
    // Decided BEFORE walking. Returning the moment an element without a usable
    // id turned up would silently discard the paths already collected from the
    // elements before it, so a list of one good element and one bad one would
    // behave differently depending on the ORDER of the two.
    const addressed: { readonly element: unknown; readonly id: string }[] = [];
    for (const element of record) {
      const id: unknown = isPlainObject(element) ? element.id : undefined;
      if (typeof id !== 'string' || !ADDRESSABLE_ID_RE.test(id)) {
        // No stable address for the elements, so the list is attributed whole
        // rather than indexed by position — a positional path would reattach
        // provenance to a different value on the next reorder.
        return prefix === '' ? [] : [prefix];
      }
      addressed.push({ element, id });
    }
    for (const { element, id } of addressed) {
      paths.push(...populatedPaths(element, `${prefix}[${id}]`));
    }
    return paths;
  }

  if (record === null || record === undefined) {
    return [];
  }
  // Numbers and booleans land here — `false` and `0` included, deliberately.
  return prefix === '' ? [] : [prefix];
}
