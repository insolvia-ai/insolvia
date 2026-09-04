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
  /**
   * The caller's firm and standing in it, or **absent** when nobody has added
   * them to one. See {@link FirmMembership} — `undefined` here is what a
   * client should render as "ask your firm's administrator to add you", and it
   * is the only endpoint that reports that state rather than refusing.
   */
  readonly firm?: FirmMembership;
}

/**
 * A feature the firm grants access to, per user. The list is Insolvia's, not
 * a general RBAC vocabulary — adding one is a server change.
 */
export type FirmFeature =
  'cases' | 'intake' | 'documents' | 'extraction_review' | 'firm_administration';

/**
 * How much of a feature a firm user may reach. Ordered weakest to strongest:
 * `add_edit` satisfies anything `view_only` does.
 */
export type PermissionLevel = 'hidden' | 'view_only' | 'add_edit';

/** A firm user's job title. Drives server-side DEFAULTS and decides nothing. */
export type FirmRole = 'attorney' | 'paralegal' | 'staff';

/** Whether a firm user's account is usable. */
export type FirmUserStatus = 'active' | 'disabled';

/**
 * The caller's own firm and standing in it — the `firm` block of `GET /v1/me`.
 *
 * **Absent, not null, when the caller is in no firm.** A person can sign in
 * before anyone adds them to a firm; every other endpoint answers 403 for
 * them, and this is the one place that says so as an answer rather than an
 * error. `undefined` here is the state a client should render as "ask your
 * firm's administrator to add you", never as a failure.
 */
export interface FirmMembership {
  /** The firm's id. */
  readonly id: string;
  /** The firm's name, for the header. */
  readonly name: string;
  /** This user's job title. */
  readonly role: FirmRole;
  /**
   * The two halves of this user's name, as their firm recorded them.
   *
   * **Either may be `''`, and that means "never recorded"** — not "blank".
   * Rows written before the name was two fields carry one display string, and
   * the server derives what it can from it; a name it cannot split yields an
   * empty surname. A client that requires a real name is expected to ask,
   * which is the state these two exist to make visible.
   */
  readonly firstName: string;
  readonly lastName: string;
  /**
   * The composed name, for rendering. **Server-derived — there is nothing to
   * write here.** Send {@link UpdateMeRequest} with the halves instead.
   *
   * It stays on the wire because most callers only ever show a name: a screen
   * rendering "opened by" reads this one field and never needs the halves.
   */
  readonly displayName: string;
  /** Full access to every feature, every case, and the firm's user list. */
  readonly isAdmin: boolean;
  /**
   * Every case in the firm, without being linked to them one by one.
   * Independent of {@link isAdmin} on purpose — a supervising attorney can
   * have this without also being able to manage users.
   */
  readonly accessAllCases: boolean;
  /**
   * The EFFECTIVE level per feature, not the stored map: an administrator's
   * record says `firm_administration: 'hidden'` and they can nonetheless
   * manage users, so this is what the server will actually allow. Use
   * {@link permits} rather than comparing strings.
   */
  readonly permissions: Readonly<Record<FirmFeature, PermissionLevel>>;
}

/** Weakest to strongest. `permits` compares by position. */
const PERMISSION_ORDER: readonly PermissionLevel[] = ['hidden', 'view_only', 'add_edit'];

/**
 * Whether `held` is enough for `required`.
 *
 * Exists so no caller writes `permissions.documents === 'add_edit'` and
 * thereby treats an `add_edit` holder as unable to view. It is a client-side
 * CONVENIENCE and never a control: the server decides, and a client that
 * showed a button it should not have gets a 403 rather than access.
 */
export function permits(held: PermissionLevel, required: PermissionLevel): boolean {
  return PERMISSION_ORDER.indexOf(held) >= PERMISSION_ORDER.indexOf(required);
}

/**
 * The `PATCH /v1/me` request body — your own name, and nothing else.
 *
 * Two optional fields where {@link UpdateFirmUserRequest} has six, and the
 * narrowness is the contract: everything else on the record is an
 * administrator's statement about you (role, permissions, status — sent
 * through {@link InsolviaApiClient.updateFirmUser}), and email is a pool fact
 * neither endpoint accepts.
 *
 * **Both halves are optional, but the server refuses a body with neither.**
 * Either alone is a legitimate edit: a row whose halves were derived from a
 * pre-split display name often has a correct first name and an empty surname,
 * and making that person retype both would be rude.
 *
 * `displayName` is deliberately absent — it is derived on the way out, so
 * there is nothing here to write it with.
 */
export interface UpdateMeRequest {
  readonly firstName?: string;
  readonly lastName?: string;
}

/**
 * The `PATCH /v1/me` body.
 *
 * Omit-when-absent, this package's standing rule: an unsent half means "leave
 * it alone", and sending `''` would mean "erase it" — a difference the server
 * acts on, so it must not be blurred here.
 */
export function updateMeRequestToJson(request: UpdateMeRequest): Record<string, unknown> {
  const body: Record<string, unknown> = {};
  if (request.firstName !== undefined) body.firstName = request.firstName;
  if (request.lastName !== undefined) body.lastName = request.lastName;
  return body;
}

/**
 * Whether the firm may be used at all. Reading `suspended` through this
 * client is nearly hypothetical: accessor resolution refuses every member of
 * a suspended firm, so the routes that return a {@link Firm} answer 403
 * before they could say it.
 */
export type FirmStatus = 'active' | 'suspended';

/**
 * The firm's own record — `GET /v1/firm` and `PATCH /v1/firm`, for the firm's
 * administrators.
 *
 * Thinner than what the ADMIN portal sees, deliberately: the provenance
 * fields (`createdBy`, `createdByEmail`) name the Insolvia staff member who
 * provisioned the firm, and a staff identity is not something a tenant
 * response carries.
 */
export interface Firm {
  readonly id: string;
  readonly name: string;
  readonly status: FirmStatus;
  /** When the firm was provisioned, verbatim. */
  readonly createdAt: string;
  /** Last write to the record, verbatim. */
  readonly updatedAt: string;
}

/**
 * The `PATCH /v1/firm` request body — the name, and nothing else.
 *
 * **`status` is absent on purpose and never joins.** Suspend/reactivate is
 * Insolvia's own operation, on the admin portal: a firm suspending itself
 * would be a lockout with no self-service recovery, because self-signup is
 * off and a suspended firm's members — the caller included — are refused
 * everywhere.
 */
export interface UpdateFirmRequest {
  readonly name: string;
}

/** The `PATCH /v1/firm` body. */
export function updateFirmRequestToJson(request: UpdateFirmRequest): Record<string, unknown> {
  return { name: request.name };
}

/**
 * A colleague as `GET /v1/firm/directory` returns them — three fields, for
 * every member of the firm.
 *
 * Deliberately thinner than {@link FirmUser}. It exists so a subject
 * ({@link Case.createdBy}, an assignee) can be rendered as a name, and that
 * need does not extend to a colleague's email address or permission map.
 */
export interface FirmColleague {
  /** The Cognito subject other endpoints address them by. */
  readonly subject: string;
  /** The two halves of their name. Either may be `''` — see {@link FirmMembership}. */
  readonly firstName: string;
  readonly lastName: string;
  /** Their composed name, for display. Server-derived. */
  readonly displayName: string;
  /** Their job title. */
  readonly role: FirmRole;
}

/**
 * A firm user as `GET /v1/firm/users` returns them — the whole record, for
 * administrators only.
 *
 * `permissions` here is the STORED map, unlike {@link FirmMembership}'s: an
 * administrator editing somebody's record needs to see the stored value and
 * the admin override as two separate facts, or turning off `isAdmin` looks
 * like it grants nothing back.
 */
export interface FirmUser {
  /** The Cognito subject — the id every other endpoint addresses them by. */
  readonly subject: string;
  /** Their email address, which is also their sign-in username. */
  readonly email: string;
  /** The two halves of their name. Either may be `''` — see {@link FirmMembership}. */
  readonly firstName: string;
  readonly lastName: string;
  /** Their composed name, for display. Server-derived. */
  readonly displayName: string;
  /** Their job title. */
  readonly role: FirmRole;
  /** Full access to every feature, every case, and the firm's user list. */
  readonly isAdmin: boolean;
  /** Every case in the firm, without per-case linking. */
  readonly accessAllCases: boolean;
  /** The stored per-feature map — see the note above. */
  readonly permissions: Readonly<Record<FirmFeature, PermissionLevel>>;
  /** Whether the account is usable. */
  readonly status: FirmUserStatus;
  /** The server's UTC creation timestamp, verbatim. */
  readonly createdAt: string;
  /** The server's UTC last-update timestamp, verbatim. */
  readonly updatedAt: string;
}

/**
 * The `POST /v1/firm/users` request body.
 *
 * `permissions` is MERGED over the role's defaults rather than replacing them,
 * so sending one feature does not silently revoke the rest. That is the
 * opposite of {@link UpdateFirmUserRequest}, and both are the server's
 * behaviour rather than this client's.
 */
export interface AddFirmUserRequest {
  /** The colleague's email address. Lower-cased by the server. */
  readonly email: string;
  /** Both halves are required to ADD somebody — the server refuses a partial name here. */
  readonly firstName: string;
  readonly lastName: string;
  /** Their job title, which chooses the default permission map. */
  readonly role: FirmRole;
  /** Full access, including the firm's user list. Defaults to `false`. */
  readonly isAdmin?: boolean;
  /** Every case in the firm. Defaults to `false`. */
  readonly accessAllCases?: boolean;
  /** Overrides merged over the role defaults. */
  readonly permissions?: Partial<Record<FirmFeature, PermissionLevel>>;
}

/** The `POST /v1/firm/users` body, with absent optionals omitted entirely. */
export function addFirmUserRequestToJson(request: AddFirmUserRequest): Record<string, unknown> {
  const body: Record<string, unknown> = {
    email: request.email,
    firstName: request.firstName,
    lastName: request.lastName,
    role: request.role,
  };
  // Omitted rather than sent as `undefined`: the server treats an absent key
  // as "use the default" and a present one as an instruction, and
  // JSON.stringify would drop `undefined` anyway — being explicit here means
  // the request test can assert the exact body.
  if (request.isAdmin !== undefined) body.isAdmin = request.isAdmin;
  if (request.accessAllCases !== undefined) body.accessAllCases = request.accessAllCases;
  if (request.permissions !== undefined) body.permissions = request.permissions;
  return body;
}

/**
 * The `PATCH /v1/firm/users/{subject}` request body. Every field optional;
 * an empty body is a 400 rather than a no-op.
 *
 * **`permissions` REPLACES the stored map**, unlike
 * {@link AddFirmUserRequest}'s. A merging PATCH could only ever grant — there
 * would be no way to express "take documents away" — so send the map you want.
 *
 * **`email` is absent on purpose.** The address is the one Cognito
 * authenticates and sends to; changing it in our store alone would leave two
 * systems disagreeing about who somebody is.
 */
export interface UpdateFirmUserRequest {
  readonly firstName?: string;
  readonly lastName?: string;
  readonly role?: FirmRole;
  readonly isAdmin?: boolean;
  readonly accessAllCases?: boolean;
  readonly permissions?: Readonly<Record<FirmFeature, PermissionLevel>>;
  readonly status?: FirmUserStatus;
}

/** The `PATCH /v1/firm/users/{subject}` body, absent fields omitted. */
export function updateFirmUserRequestToJson(
  request: UpdateFirmUserRequest,
): Record<string, unknown> {
  const body: Record<string, unknown> = {};
  if (request.firstName !== undefined) body.firstName = request.firstName;
  if (request.lastName !== undefined) body.lastName = request.lastName;
  if (request.role !== undefined) body.role = request.role;
  if (request.isAdmin !== undefined) body.isAdmin = request.isAdmin;
  if (request.accessAllCases !== undefined) body.accessAllCases = request.accessAllCases;
  if (request.permissions !== undefined) body.permissions = request.permissions;
  if (request.status !== undefined) body.status = request.status;
  return body;
}

/** One person linked to a case, as `GET /v1/cases/{id}/assignees` returns them. */
export interface CaseAssignee {
  /** The colleague's Cognito subject — resolve it through the directory. */
  readonly subject: string;
  /** When the link was made, verbatim. */
  readonly assignedAt: string;
  /** The subject of whoever made it. */
  readonly assignedBy: string;
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
 * `{"id", "createdBy", "chapter", "district", "status", "createdAt",
 * "updatedAt"}`.
 *
 * **There is no `firmId`.** Every caller who can see a case is in its firm by
 * construction, so the id would echo the reader's own tenant back at them.
 */
export interface Case {
  /** The server-generated case id. */
  readonly id: string;
  /**
   * The subject of the firm user who OPENED the matter — an audit fact, not a
   * permission. It grants nothing: reaching a case means being in its firm and
   * either an admin, or carrying {@link FirmMembership.accessAllCases}, or
   * being linked to it.
   *
   * Keyed the same way {@link FirmColleague.subject} is, so
   * `listFirmDirectory` turns it into a name. Rendering it raw is a bug — it
   * is a UUID.
   */
  readonly createdBy: string;
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
  /**
   * The effective-dating pins packet assembly wrote (issue #96): form series
   * id -> the pinned revision (`effective_date[+sequence]`). **Absent until
   * the first packet assembles** — a floating case records nothing — and
   * replaced whole on re-assembly. Read-only: no request ever sends it.
   */
  readonly formRevisions?: Readonly<Record<string, string>>;
  /**
   * The pinned `code/dollar-amounts` release id. Reserved — the series has no
   * registry yet (it lands with the means-test milestone), so today this is
   * always absent.
   */
  readonly constantsSetId?: string;
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
// Pipeline jobs — mirrors services/api/src/insolvia_api/core/jobs.py
// (`job_json`, `KINDS`, `STATUSES`) and api/routes/jobs.py (ADR 0018).
// ---------------------------------------------------------------------------

/**
 * The job kinds the API accepts today. The exact `KINDS` tuple from
 * `core/jobs.py`: `echo` (the walking skeleton) and `packet_assembly` (the
 * Chapter 7 packet, issue #96). 9.7 adds the AI review.
 */
export type JobKind = 'echo' | 'packet_assembly';

/**
 * Where a job sits. `queued` and `running` mean poll again; `succeeded` and
 * `failed` are settled — except that a `failed` job whose failure was
 * infrastructure (category `internal`) is still being retried by the
 * pipeline and may yet flip to `succeeded`. Only `succeeded` never changes.
 */
export type JobStatus = 'queued' | 'running' | 'succeeded' | 'failed';

/**
 * Why a job failed, in words safe to show the preparer. `category` is a
 * machine key (`case_incomplete`, `internal`, …); `message` is the sentence
 * to render.
 */
export interface JobFailure {
  readonly category: string;
  readonly message: string;
}

// ---------------------------------------------------------------------------
// Assembled packets — mirrors services/api/src/insolvia_api/core/packets.py
// (`packet_json`) and api/routes/packets.py (issue #96).
// ---------------------------------------------------------------------------

/**
 * One assembled Chapter 7 packet, as returned by `GET
 * /v1/cases/{caseId}/packets`. Produced by the `packet_assembly` pipeline job
 * — trigger with {@link InsolviaApiClient.acceptCaseJob}, poll with
 * {@link InsolviaApiClient.getCaseJob}, then download through
 * {@link InsolviaApiClient.getPacketUrl}.
 *
 * A packet is immutable: re-assembly creates a NEW record rather than
 * replacing this one, so the packet an attorney reviewed stays the packet
 * they reviewed.
 */
export interface Packet {
  /** The server-generated packet id. */
  readonly id: string;
  /** The case it belongs to (also named in the URL; echoed for convenience). */
  readonly caseId: string;
  /** The pipeline job whose run produced it. */
  readonly jobId: string;
  /** The download name — always `chapter7-packet.zip`. */
  readonly fileName: string;
  /** Always `application/zip`. */
  readonly contentType: string;
  /** The stored zip's exact size in bytes. */
  readonly byteSize: number;
  /**
   * The sha256 of the stored zip. Assembly is deterministic to the byte, so
   * this digest is how a reviewer proves a downloaded file is THIS packet.
   */
  readonly sha256: string;
  /**
   * The effective-dating pins this packet was rendered under — the same map
   * written onto the case, kept here because the case's copy moves on
   * re-assembly while this record describes this packet forever.
   */
  readonly formRevisions: Readonly<Record<string, string>>;
  /** How many creditors the enclosed matrix lists (after deduplication). */
  readonly creditorCount: number;
  /**
   * The subject of the firm user whose job accept produced it — resolve
   * through `listFirmDirectory`, never render raw.
   */
  readonly createdBy: string;
  readonly createdAt: string;
}

/**
 * The `GET /v1/cases/{caseId}/packets/{packetId}/url` 200 response — the same
 * `{"url", "method", "expiresAt"}` short-lived capability shape the document
 * download uses ({@link DocumentDownload}), minted by the same route pattern.
 */
export type PacketDownload = DocumentDownload;

/**
 * A pipeline job, as returned by both `/v1/cases/{caseId}/jobs` endpoints:
 * `{"id", "kind", "status", "createdBy", "attempts", "createdAt",
 * "updatedAt"}` plus `failure` only when failed and `result` only when
 * succeeded — both **absent** otherwise, never `null`.
 *
 * There is no `caseId`: the caller named the case in the URL.
 */
export interface Job {
  /** The server-generated job id — the handle {@link InsolviaApiClient.getCaseJob} polls. */
  readonly id: string;
  readonly kind: JobKind;
  readonly status: JobStatus;
  /**
   * The subject of the firm user who accepted the job. Keyed the same way
   * {@link FirmColleague.subject} is — resolve it through
   * `listFirmDirectory`, never render it raw.
   */
  readonly createdBy: string;
  /** How many times a worker has started this job. 0 until the first run. */
  readonly attempts: number;
  readonly createdAt: string;
  readonly updatedAt: string;
  /** Present only when `status` is `'failed'`. */
  readonly failure?: JobFailure;
  /** Present only when `status` is `'succeeded'`. Shape is per-kind. */
  readonly result?: Readonly<Record<string, unknown>>;
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
  /**
   * B101 line 5's County box (venue turns on it). Only the debtor's
   * residence address ever needs it; max 64 characters.
   */
  readonly county?: string | undefined;
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

// ---------------------------------------------------------------------------
// The generic case collections (issue #249): creditors, claims, assets,
// employments, income summaries, households, expenses, dependents, codebtors
// and SOFA entries. Mirrors services/api/src/insolvia_api/core/
// {case_entities,case_collections,creditors,claims,assets,income,expenses,
// codebtors,sofa}.py.
//
// The bodies are typed as interfaces (compile-time) while the enums below are
// exported as VALUES — each one is a picker in the questionnaire, the same
// argument the debtor enums make. The provenance rules are identical to the
// debtor's: every populated field carries an entry, machine-supplied values
// must be confirmed, and {@link staffTypedProvenance} builds the ordinary map.
// ---------------------------------------------------------------------------

/** 106D/E/F's claim classes — which schedule a claim prints on. */
export const CLAIM_CLASSES = ['secured', 'priority_unsecured', 'nonpriority_unsecured'] as const;
export type ClaimClass = (typeof CLAIM_CLASSES)[number];

/** 106D line 2's "check all that apply". `other` carries `lien_nature_other`. */
export const LIEN_NATURES = ['agreement', 'statutory', 'judgment', 'other'] as const;
export type LienNature = (typeof LIEN_NATURES)[number];

/** The three priority categories printed on 106E/F, plus `other`. */
export const PRIORITY_TYPES = [
  'domestic_support',
  'tax_and_government',
  'death_or_injury_while_intoxicated',
  'other',
] as const;
export type PriorityType = (typeof PRIORITY_TYPES)[number];

/** 106E/F Part 2's type line. `other` carries `nonpriority_type_other`. */
export const NONPRIORITY_TYPES = [
  'student_loan',
  'separation_or_divorce',
  'pension_or_profit_sharing',
  'other',
] as const;
export type NonpriorityType = (typeof NONPRIORITY_TYPES)[number];

/**
 * The "which debtor" column that recurs across the schedules — who incurred a
 * claim, who owns an asset. One vocabulary, verbatim from the forms.
 */
export const DEBTOR_ATTRIBUTION = [
  'debtor_1',
  'debtor_2',
  'both',
  'at_least_one_plus_another',
] as const;
export type DebtorAttribution = (typeof DEBTOR_ATTRIBUTION)[number];

/** The 106A/B line set, named for what the line asks about, never numbered. */
export const ASSET_CATEGORIES = [
  'real_property',
  'vehicle',
  'watercraft_aircraft_or_recreational_vehicle',
  'household_goods',
  'electronics',
  'collectibles',
  'sports_and_hobby_equipment',
  'firearms',
  'clothes',
  'jewelry',
  'non_farm_animals',
  'other_personal_or_household',
  'cash',
  'deposits_of_money',
  'bonds_and_mutual_funds',
  'non_publicly_traded_stock_and_business_interests',
  'government_and_corporate_bonds',
  'retirement_accounts',
  'security_deposits_and_prepayments',
  'annuities',
  'education_accounts',
  'trusts_and_future_interests',
  'intellectual_property',
  'licenses_and_franchises',
  'money_owed_to_you',
  'family_support_owed',
  'other_amounts_owed',
  'insurance_policy_interests',
  'property_due_from_a_death',
  'claims_against_third_parties',
  'other_contingent_and_unliquidated_claims',
  'other_financial_assets',
  'accounts_receivable',
  'office_equipment',
  'machinery_and_tools_of_trade',
  'inventory',
  'partnership_and_joint_venture_interests',
  'customer_lists_and_intangibles',
  'other_business_property',
  'farm_animals',
  'crops',
  'farm_and_fishing_equipment',
  'farm_and_fishing_supplies',
  'other_farm_property',
  'other_property_not_listed',
] as const;
export type AssetCategory = (typeof ASSET_CATEGORIES)[number];

/** 106A/B Part 1's real-property "check all that apply". */
export const PROPERTY_TYPES = [
  'single_family_home',
  'duplex_or_multi_unit',
  'condominium_or_cooperative',
  'manufactured_or_mobile_home',
  'land',
  'investment_property',
  'timeshare',
  'other',
] as const;
export type PropertyType = (typeof PROPERTY_TYPES)[number];

/** 106I Part 1's employment box. */
export const EMPLOYMENT_STATUSES = ['employed', 'not_employed'] as const;
export type EmploymentStatus = (typeof EMPLOYMENT_STATUSES)[number];

/** Which of 106J's two schedules a household row is: 106J or 106J-2. */
export const WHICH_HOUSEHOLDS = ['main', 'debtor_2_separate'] as const;
export type WhichHousehold = (typeof WHICH_HOUSEHOLDS)[number];

/** The 106J line set, named for what the line asks about, never numbered. */
export const EXPENSE_CATEGORIES = [
  'rent_or_home_ownership',
  'real_estate_taxes',
  'property_insurance',
  'home_maintenance',
  'homeowners_association_dues',
  'additional_mortgage_payments',
  'electricity_heat_gas',
  'water_sewer_garbage',
  'telephone_and_internet',
  'other_utilities',
  'food_and_housekeeping',
  'childcare_and_education',
  'clothing_and_laundry',
  'personal_care',
  'medical_and_dental',
  'transportation',
  'entertainment_and_recreation',
  'charitable_contributions',
  'life_insurance',
  'health_insurance',
  'vehicle_insurance',
  'other_insurance',
  'taxes',
  'vehicle_installment_payments',
  'other_installment_payments',
  'alimony_and_support_payments',
  'support_of_others',
  'other_property_mortgages',
  'other_property_taxes',
  'other_property_insurance',
  'other_property_maintenance',
  'other_property_association_dues',
  'other',
] as const;
export type ExpenseCategory = (typeof EXPENSE_CATEGORIES)[number];

/**
 * B107's entry types — the closed enum behind `sofa_entries`. Named for what
 * the question asks about, never numbered: the annual form cycle renumbers.
 * The payload SHAPES are owned by `core/sofa.py` server-side; the client
 * carries them as {@link SofaPayload} and the API validates every field.
 */
export const SOFA_ENTRY_TYPES = [
  'marital_status',
  'prior_address',
  'community_property_residence',
  'income_by_period',
  'consumer_debt_declaration',
  'creditor_payment',
  'insider_payment',
  'insider_benefit_payment',
  'lawsuit',
  'repossession',
  'setoff',
  'receivership',
  'gift',
  'charitable_contribution',
  'loss',
  'consultant_payment',
  'creditor_assistance_payment',
  'property_transfer',
  'self_settled_trust',
  'closed_account',
  'safe_deposit_box',
  'storage_unit',
  'held_for_another',
  'environmental_notice',
  'environmental_proceeding',
  'business_connection',
  'financial_statement_issued',
] as const;
export type SofaEntryType = (typeof SOFA_ENTRY_TYPES)[number];

/**
 * A dollar amount on the wire: a fixed-scale decimal CARRIED AS A STRING,
 * `"1200.00"`. Never a number — a JSON number has been through the sender's
 * binary floating point, which is the corruption the string exists to
 * prevent. The API canonicalises to two decimal places and rejects negatives.
 */
export type Money = string;

/** A calendar date, `YYYY-MM-DD` — no time, no zone. The API parses it. */
export type FormDate = string;

/** One deduplicated name-and-address for the creditor matrix. */
export interface CreditorBody {
  /** One free-text line — creditors are predominantly entities. */
  readonly name?: string | undefined;
  readonly address?: Address | undefined;
}

/**
 * 106D Part 2 / 106E/F Part 3: someone else to be notified about a debt.
 * {@link id} is the client-chosen row id, required for the same reason
 * {@link OtherName.id} is: provenance addresses the row by it.
 */
export interface NoticeParty {
  readonly id: string;
  readonly name?: string | undefined;
  readonly address?: Address | undefined;
  readonly account_last4?: string | undefined;
}

/**
 * One claim spanning 106D and both parts of 106E/F, discriminated by
 * {@link claim_class}. The class-specific members are accepted regardless of
 * the class (intake is progressive; the class may be decided last). The
 * unsecured portion of a secured claim and a priority claim's total are
 * arithmetic and never stored or sent.
 */
export interface ClaimBody {
  /**
   * The creditor record this claim names. Not checked server-side — a claim
   * typed before its creditor is saved must persist.
   */
  readonly creditor_id?: string | undefined;
  readonly claim_class?: ClaimClass | undefined;
  /** Up to four digits. Never a full account number. */
  readonly account_last4?: string | undefined;
  readonly date_incurred?: FormDate | undefined;
  readonly amount?: Money | undefined;
  readonly contingent?: boolean | undefined;
  readonly unliquidated?: boolean | undefined;
  readonly disputed?: boolean | undefined;
  readonly subject_to_offset?: boolean | undefined;
  readonly who_incurred?: DebtorAttribution | undefined;
  readonly community_debt?: boolean | undefined;
  readonly notice_parties?: readonly NoticeParty[] | undefined;
  readonly collateral_description?: string | undefined;
  readonly collateral_value?: Money | undefined;
  readonly lien_nature?: readonly LienNature[] | undefined;
  readonly lien_nature_other?: string | undefined;
  readonly priority_amount?: Money | undefined;
  readonly nonpriority_amount?: Money | undefined;
  readonly priority_type?: PriorityType | undefined;
  readonly priority_type_other?: string | undefined;
  readonly nonpriority_type?: NonpriorityType | undefined;
  readonly nonpriority_type_other?: string | undefined;
}

/** One row of 106A/B. Both value boxes are stored; subtotals never are. */
export interface AssetBody {
  readonly category?: AssetCategory | undefined;
  readonly property_types?: readonly PropertyType[] | undefined;
  readonly description?: string | undefined;
  readonly county?: string | undefined;
  readonly value_entire?: Money | undefined;
  readonly value_portion_owned?: Money | undefined;
  readonly ownership_interest?: DebtorAttribution | undefined;
  readonly ownership_interest_description?: string | undefined;
  readonly community_property?: boolean | undefined;
  /** Category-specific free text: make/model/year, institution, percentage. */
  readonly detail?: string | undefined;
}

/** 106I Part 1: where a debtor works. */
export interface EmploymentBody {
  readonly debtor_id?: string | undefined;
  readonly status?: EmploymentStatus | undefined;
  readonly occupation?: string | undefined;
  readonly employer_name?: string | undefined;
  readonly employer_address?: Address | undefined;
  readonly employed_since?: FormDate | undefined;
}

/**
 * 106I Part 2, one column per debtor. ENTERED AND CONFIRMED, NOT COMPUTED —
 * the form asks for an estimate of what income will be, and the derived lines
 * (gross, totals, take-home, combined) are never stored or sent.
 */
export interface IncomeSummaryBody {
  readonly debtor_id?: string | undefined;
  readonly wages?: Money | undefined;
  readonly overtime?: Money | undefined;
  readonly deduction_tax?: Money | undefined;
  readonly deduction_mandatory_retirement?: Money | undefined;
  readonly deduction_voluntary_retirement?: Money | undefined;
  readonly deduction_retirement_loan_repayment?: Money | undefined;
  readonly deduction_insurance?: Money | undefined;
  readonly deduction_domestic_support?: Money | undefined;
  readonly deduction_union_dues?: Money | undefined;
  readonly deduction_other?: Money | undefined;
  readonly deduction_other_specify?: string | undefined;
  readonly business_net_income?: Money | undefined;
  readonly interest_and_dividends?: Money | undefined;
  readonly family_support?: Money | undefined;
  readonly unemployment?: Money | undefined;
  readonly social_security?: Money | undefined;
  readonly other_government_assistance?: Money | undefined;
  readonly other_government_assistance_specify?: string | undefined;
  readonly pension_or_retirement?: Money | undefined;
  readonly other_monthly_income?: Money | undefined;
  readonly other_monthly_income_specify?: string | undefined;
  /** Line 11 — case-level on the form; carried on the debtor-1 summary. */
  readonly household_contributions?: Money | undefined;
  readonly household_contributions_specify?: string | undefined;
  readonly change_expected?: boolean | undefined;
  readonly change_explanation?: string | undefined;
}

/** 106J Part 1's frame: which schedule, and the change narrative. */
export interface HouseholdBody {
  readonly which_household?: WhichHousehold | undefined;
  readonly separate_household?: boolean | undefined;
  /** Line 3 — expenses include people other than the debtors + dependents. */
  readonly expenses_include_others?: boolean | undefined;
  readonly change_expected?: boolean | undefined;
  readonly change_explanation?: string | undefined;
}

/** One 106J expense line: a row, not a column. */
export interface ExpenseBody {
  readonly household_id?: string | undefined;
  readonly category?: ExpenseCategory | undefined;
  readonly specify_text?: string | undefined;
  readonly amount?: Money | undefined;
}

/**
 * A 106J dependent. There is deliberately NO name member: the form does not
 * ask for dependents' names, and the API refuses one with a 400 rather than
 * dropping it.
 */
export interface DependentBody {
  readonly household_id?: string | undefined;
  readonly relationship?: string | undefined;
  readonly age?: number | undefined;
  readonly lives_with_debtor?: boolean | undefined;
}

/** 106H Part 2: who else is liable, and on which claims. */
export interface CodebtorBody {
  readonly name?: string | undefined;
  readonly address?: Address | undefined;
  /** Claim record ids — the fact behind the form's "Schedule D, line __". */
  readonly claim_ids?: readonly string[] | undefined;
  /** Contract/lease record ids — the "Schedule G, line __" column. */
  readonly contract_lease_ids?: readonly string[] | undefined;
}

/**
 * 106C, one row: an exemption claimed on an asset. The amount is EITHER a
 * dollar figure OR the 100%-of-fair-market-value election — the API accepts
 * both while intake is in progress; the completeness gate flags a conflict.
 * The printed description and value are the referenced ASSET's, copied at
 * projection time and never stored here.
 */
export interface ExemptionBody {
  /** The asset record this exemption protects. Not checked server-side. */
  readonly asset_id?: string | undefined;
  readonly statute_citation?: string | undefined;
  readonly amount?: Money | undefined;
  readonly claims_full_fmv?: boolean | undefined;
  /** 106C line 3's follow-up — a fact the debtor supplies. */
  readonly acquired_within_1215_days?: boolean | undefined;
}

/** 106G, one row: an executory contract or unexpired lease. */
export interface ContractLeaseBody {
  /** One free-text line — counterparties are predominantly entities. */
  readonly counterparty_name?: string | undefined;
  readonly counterparty_address?: Address | undefined;
  /** What the contract or lease is for, term remaining, contract number. */
  readonly description?: string | undefined;
}

/** 106H line 2 / B107 Q3: the community-property spouse or former spouse. */
export interface CommunityHouseholdMemberBody {
  readonly name?: string | undefined;
  readonly address?: Address | undefined;
  /** The community property state lived in — a two-letter code. */
  readonly community_state?: string | undefined;
  readonly lived_with_debtor?: boolean | undefined;
}

/**
 * A SOFA payload's members, typed loosely on the wire. The per-type shapes
 * are owned and validated by `core/sofa.py` server-side — the one dispatch
 * table — and re-declaring all twenty-seven here would be a second copy of
 * that contract with no second reader yet. The intake UI narrows what it
 * renders per {@link SofaEntryType}.
 */
export type SofaPayload = Readonly<Record<string, unknown>>;

/** One B107 answer: a typed row in the single SOFA table. */
export interface SofaEntryBody {
  readonly entry_type?: SofaEntryType | undefined;
  /** Refused with a 400 when sent without {@link entry_type}. */
  readonly payload?: SofaPayload | undefined;
}

// ── B101's entities (issue #93) — mirrored from core/petitions.py ──

/** B101 line 8 — how the filing fee will be handled (→ Forms 103A/103B). */
export const FEE_HANDLING = ['full', 'installments', 'waiver'] as const;
export type FeeHandling = (typeof FEE_HANDLING)[number];

/** B101 line 12's business-type choice. */
export const BUSINESS_TYPES = [
  'health_care_business',
  'single_asset_real_estate',
  'stockbroker',
  'commodity_broker',
  'none_of_the_above',
] as const;
export type BusinessType = (typeof BUSINESS_TYPES)[number];

/** B101 line 13, including the Subchapter V election. */
export const SMALL_BUSINESS_STATUSES = [
  'not_filing_under_chapter_11',
  'chapter_11_not_small_business',
  'chapter_11_small_business',
  'chapter_11_subchapter_v',
] as const;
export type SmallBusinessStatus = (typeof SMALL_BUSINESS_STATUSES)[number];

/** B101 line 16. `other` carries the explanation in `debt_character_other`. */
export const DEBT_CHARACTERS = ['consumer', 'business', 'other'] as const;
export type DebtCharacter = (typeof DEBT_CHARACTERS)[number];

/** B101 line 18's printed brackets, self-selected by the debtor. */
export const ESTIMATED_CREDITORS_BANDS = [
  '1_49',
  '50_99',
  '100_199',
  '200_999',
  '1000_5000',
  '5001_10000',
  '10001_25000',
  '25001_50000',
  '50001_100000',
  'more_than_100000',
] as const;
export type EstimatedCreditorsBand = (typeof ESTIMATED_CREDITORS_BANDS)[number];

/** B101 lines 19 and 20 share one dollar-bracket scale. */
export const ESTIMATED_DOLLAR_BANDS = [
  '0_50000',
  '50001_100000',
  '100001_500000',
  '500001_1000000',
  '1000001_10000000',
  '10000001_50000000',
  '50000001_100000000',
  '100000001_500000000',
  '500000001_1000000000',
  '1000000001_10000000000',
  '10000000001_50000000000',
  'more_than_50000000000',
] as const;
export type EstimatedDollarBand = (typeof ESTIMATED_DOLLAR_BANDS)[number];

/** Part 7's two signer kinds; a preparer triggers Form 119. */
export const FILING_PROFESSIONAL_ROLES = ['attorney', 'bankruptcy_petition_preparer'] as const;
export type FilingProfessionalRole = (typeof FILING_PROFESSIONAL_ROLES)[number];

/** B101 line 14: property needing immediate attention. */
export interface HazardousProperty {
  readonly description?: string | undefined;
  readonly why_immediate?: string | undefined;
  readonly address?: Address | undefined;
}

/**
 * B101's Part 2–6 case-level answers. ONE per case by meaning — churned
 * during intake, untouched afterwards — though stored as a generic
 * collection; the pre-filing completeness gate flags a duplicate.
 */
export interface PetitionBody {
  readonly fee_handling?: FeeHandling | undefined;
  readonly rents_residence?: boolean | undefined;
  readonly eviction_judgment_against_you?: boolean | undefined;
  readonly small_business_status?: SmallBusinessStatus | undefined;
  readonly hazardous_property?: HazardousProperty | undefined;
  readonly debt_character?: DebtCharacter | undefined;
  readonly debt_character_other?: string | undefined;
  readonly ch7_funds_available_for_creditors?: boolean | undefined;
  readonly estimated_creditors?: EstimatedCreditorsBand | undefined;
  readonly estimated_assets?: EstimatedDollarBand | undefined;
  readonly estimated_liabilities?: EstimatedDollarBand | undefined;
}

/** B101 line 9: a bankruptcy filed within the last 8 years. */
export interface PriorCaseBody {
  readonly district?: string | undefined;
  readonly filed_on?: FormDate | undefined;
  readonly case_number?: string | undefined;
}

/** B101 line 10: a pending case by a spouse, partner, or affiliate. */
export interface RelatedCaseBody {
  readonly debtor_name?: string | undefined;
  readonly relationship?: string | undefined;
  readonly district?: string | undefined;
  readonly filed_on?: FormDate | undefined;
  readonly case_number?: string | undefined;
}

/** B101 line 12: a business the debtor runs as a sole proprietor. */
export interface SoleProprietorshipBody {
  readonly name?: string | undefined;
  readonly address?: Address | undefined;
  readonly business_type?: BusinessType | undefined;
}

/**
 * B101 Part 7: the attorney block, or a bankruptcy petition preparer.
 * The name is four discrete parts like every person name — the form prints
 * one line, and composing it is the forms engine's job.
 */
export interface FilingProfessionalBody {
  readonly role?: FilingProfessionalRole | undefined;
  readonly name?: PersonName | undefined;
  readonly firm_name?: string | undefined;
  readonly address?: Address | undefined;
  readonly phone?: string | undefined;
  readonly email?: string | undefined;
  readonly bar_number?: string | undefined;
  readonly bar_state?: string | undefined;
  readonly signature_date?: FormDate | undefined;
}

/**
 * The URL segment and listing key of each generic collection, and the body
 * each one carries. `debtors` and `documents` are deliberately not here —
 * they have their own endpoints and their own shapes.
 */
export interface CaseCollections {
  readonly creditors: CreditorBody;
  readonly claims: ClaimBody;
  readonly assets: AssetBody;
  readonly employments: EmploymentBody;
  readonly income_summaries: IncomeSummaryBody;
  readonly households: HouseholdBody;
  readonly expenses: ExpenseBody;
  readonly dependents: DependentBody;
  readonly codebtors: CodebtorBody;
  readonly sofa_entries: SofaEntryBody;
  readonly petitions: PetitionBody;
  readonly prior_cases: PriorCaseBody;
  readonly related_cases: RelatedCaseBody;
  readonly sole_proprietorships: SoleProprietorshipBody;
  readonly filing_professionals: FilingProfessionalBody;
  readonly exemptions: ExemptionBody;
  readonly contract_leases: ContractLeaseBody;
  readonly community_household_members: CommunityHouseholdMemberBody;
}

/** A collection's URL segment: `creditors`, `claims`, … */
export type CaseCollection = keyof CaseCollections;

/** Every collection, as a runtime list — mirrors `core/case_collections.py`. */
export const CASE_COLLECTIONS = [
  'creditors',
  'claims',
  'assets',
  'employments',
  'income_summaries',
  'households',
  'expenses',
  'dependents',
  'codebtors',
  'sofa_entries',
  'petitions',
  'prior_cases',
  'related_cases',
  'sole_proprietorships',
  'filing_professionals',
  'exemptions',
  'contract_leases',
  'community_household_members',
] as const satisfies readonly CaseCollection[];

/**
 * The body of a POST or PUT to a generic collection: the collection's body
 * plus its {@link ProvenanceMap}. Whole, not partial — the endpoints replace
 * the record, for the same invariant-1 reason {@link PutDebtorRequest}
 * states. Build the ordinary provenance with {@link staffTypedProvenance}.
 */
export type CaseEntityRequest<C extends CaseCollection> = CaseCollections[C] & {
  readonly provenance?: ProvenanceMap | undefined;
};

/**
 * A stored record as every entity endpoint returns it: server-stamped
 * identity, an always-present `provenance` map, and whatever of the body is
 * populated — absent members are absent, never null, exactly as on
 * {@link Debtor}.
 */
export type CaseEntity<C extends CaseCollection> = CaseCollections[C] & {
  /** The server-generated id, stable across saves. */
  readonly id: string;
  readonly case_id: string;
  readonly created_at: string;
  readonly updated_at: string;
  /** Always present — `{}` on a record with nothing in it. */
  readonly provenance: ProvenanceMap;
};

/**
 * A generic collection request body as JSON, with absent members omitted
 * entirely — the entity counterpart of {@link putDebtorRequestToJson}, and
 * the same recursive rule as the server's `prune`: `undefined` members are
 * dropped, and a sub-object or list that ends up empty is dropped with it, so
 * a record sent and the record returned compare equal. `provenance` entries
 * keep the weaker per-entry rule — see {@link putDebtorRequestToJson}.
 */
export function caseEntityRequestToJson(
  request: CaseEntityRequest<CaseCollection>,
): Record<string, unknown> {
  const json: Record<string, unknown> = {};
  for (const [key, value] of Object.entries(request)) {
    if (key === 'provenance') {
      continue;
    }
    const pruned = pruneJsonValue(value);
    if (pruned !== undefined) {
      json[key] = pruned;
    }
  }
  const provenance = provenanceToJson(request.provenance);
  if (provenance !== undefined) {
    json.provenance = provenance;
  }
  return json;
}

/**
 * The recursive half of {@link caseEntityRequestToJson}. `null` is treated as
 * absent alongside `undefined` — the server stores neither — and `false` and
 * `0` survive, because they are answers.
 */
function pruneJsonValue(value: unknown): unknown {
  if (value === undefined || value === null) {
    return undefined;
  }
  if (Array.isArray(value)) {
    const members = value.map(pruneJsonValue);
    return members.length === 0 ? undefined : members;
  }
  if (isPlainObject(value)) {
    const built: Record<string, unknown> = {};
    for (const [key, member] of Object.entries(value)) {
      const pruned = pruneJsonValue(member);
      if (pruned !== undefined) {
        built[key] = pruned;
      }
    }
    return Object.keys(built).length === 0 ? undefined : built;
  }
  return value;
}

/**
 * One reason one creditor cannot go on the matrix as recorded — issue #94.
 *
 * `creditorId` is absent on the single case-level problem (a case with no
 * creditors at all); `field` is the creditor body path the fix belongs to
 * (`name`, `address.state`, ...), so a screen can put the message next to the
 * input that needs the edit.
 */
export interface CreditorMatrixProblem {
  readonly creditorId?: string;
  readonly field: string;
  readonly message: string;
}

/**
 * `GET /v1/cases/{caseId}/creditor-matrix` — the court's mailing list, or
 * every reason there isn't one yet. `content` is the exact text of the .txt
 * file (CRLF line endings, plain ASCII) and is present only when `problems`
 * is empty: the server refuses to produce a partial matrix, because a missing
 * entry is a bankruptcy notice that never arrives. The court-format rules the
 * server enforces are cited in
 * `services/api/src/insolvia_api/core/creditor_matrix.py`.
 */
export interface CreditorMatrix {
  /** The suggested file name for the download, `creditor-matrix.txt`. */
  readonly fileName: string;
  /** Entries printed — after identical blocks are deduplicated. */
  readonly creditorCount: number;
  /** Blocks dropped because they would print identically to another. */
  readonly duplicatesOmitted: number;
  readonly problems: readonly CreditorMatrixProblem[];
  /** The file text. Absent, never null, while `problems` is non-empty. */
  readonly content?: string;
}
