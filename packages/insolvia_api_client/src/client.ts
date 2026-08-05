import { ApiException, ApiUnauthorizedException, ApiValidationException } from './exceptions.ts';
import {
  DOCUMENT_STATUSES,
  createCaseRequestToJson,
  createDocumentRequestToJson,
  listCasesQuery,
  updateCaseChangesToJson,
  waitlistSubmissionToJson,
} from './models.ts';
import type {
  Case,
  CaseChapter,
  CaseStatus,
  CreateCaseRequest,
  CreateDocumentRequest,
  CreateDocumentResult,
  Document,
  DocumentDownload,
  DocumentStatus,
  DocumentUpload,
  HealthStatus,
  ListCasesOptions,
  ListCasesResult,
  Principal,
  UpdateCaseChanges,
  UploadDocumentOptions,
  WaitlistConfirmation,
  WaitlistSubmission,
} from './models.ts';

/**
 * The subset of the platform `fetch` this client uses. Injectable so tests
 * can stub the transport without a server, and so a host that polyfills
 * `fetch` can hand its own in.
 */
export type FetchLike = (input: string, init?: RequestInit) => Promise<Response>;

/**
 * Supplies the Cognito **access token** for protected calls, or `undefined`
 * when there is no signed-in user.
 *
 * This client deliberately does not own token storage, refresh, or expiry:
 * that is the host app's job (secure storage on native, the auth session on
 * web), and baking it in here would make this package depend on one of them.
 * The seam is a single callback, consulted once per protected request so a
 * token refreshed between calls is picked up without rebuilding the client.
 *
 * Both shapes work — a synchronous read from an in-memory store, or an async
 * read from secure storage — because the client `await`s the result either
 * way.
 */
export type AccessTokenProvider = () => string | undefined | Promise<string | undefined>;

/** Options accepted by the {@link InsolviaApiClient} constructor. */
export interface InsolviaApiClientOptions {
  /**
   * Transport override. Defaults to the platform `fetch`, looked up at call
   * time so a client constructed before a polyfill lands still works.
   */
  readonly fetch?: FetchLike | undefined;
  /**
   * Supplies the bearer token for protected calls. Omit it for a client that
   * only makes public calls (`health`, `joinWaitlist`) — those never send an
   * `Authorization` header, with or without a provider configured.
   */
  readonly accessToken?: AccessTokenProvider | undefined;
}

const JSON_HEADERS = {
  Accept: 'application/json',
  'Content-Type': 'application/json',
} as const;

const ACCEPT_JSON_HEADERS = { Accept: 'application/json' } as const;

/**
 * A typed client for the Insolvia API.
 *
 * ```ts
 * const client = new InsolviaApiClient('https://staging-api.insolvia.ai');
 * const status = await client.health();
 * ```
 *
 * Error model:
 * - 401, or a protected call with no token to send →
 *   {@link ApiUnauthorizedException};
 * - 400 with per-field messages → {@link ApiValidationException};
 * - any other unexpected status, or an undecodable success body →
 *   {@link ApiException};
 * - transport failures (DNS, refused connection, …) propagate untouched as
 *   whatever `fetch` rejects with.
 *
 * Public vs protected: `health` and `joinWaitlist` are public and never send
 * an `Authorization` header, even when {@link InsolviaApiClientOptions.accessToken}
 * is configured. `me` is protected and always sends one.
 *
 * There is no `close()`: `fetch` owns no client object the caller has to
 * release.
 */
export class InsolviaApiClient {
  readonly #baseUrl: string;
  readonly #fetch: FetchLike;
  readonly #accessToken: AccessTokenProvider | undefined;

  /**
   * `baseUrl` is the API origin, with or without a trailing slash — e.g.
   * `http://localhost:8080` or `https://api.insolvia.ai`.
   */
  constructor(baseUrl: string, options: InsolviaApiClientOptions = {}) {
    this.#baseUrl = baseUrl.endsWith('/') ? baseUrl.slice(0, -1) : baseUrl;
    // Wrapped rather than `globalThis.fetch.bind(...)`: the lookup stays
    // late, and the call keeps `globalThis` as its receiver (browsers throw
    // "Illegal invocation" on a detached `fetch`).
    this.#fetch = options.fetch ?? ((input, init) => globalThis.fetch(input, init));
    this.#accessToken = options.accessToken;
  }

  /**
   * Builds the headers for a protected call: `Accept: application/json` plus
   * `Authorization: Bearer <token>`.
   *
   * Throws {@link ApiUnauthorizedException} — **before touching the network**
   * — when no provider is configured or it yields nothing. A request that is
   * certain to be rejected is not worth a round trip, and the app gets the
   * same exception type it would get from a server 401, so one `catch` covers
   * both.
   *
   * `await` covers a sync and an async provider identically.
   */
  async #protectedHeaders(): Promise<Record<string, string>> {
    const token = await this.#accessToken?.();
    if (token === undefined || token.trim() === '') {
      // The token is absent here by definition, so there is nothing to leak;
      // note that no branch of this class ever puts a token in a message.
      throw new ApiUnauthorizedException({ statusCode: 401, body: '', source: 'client' });
    }
    return { ...ACCEPT_JSON_HEADERS, Authorization: `Bearer ${token}` };
  }

  /** `GET /health` — the API's liveness/identity endpoint. */
  async health(): Promise<HealthStatus> {
    const response = await this.#fetch(`${this.#baseUrl}/health`, {
      method: 'GET',
      headers: ACCEPT_JSON_HEADERS,
    });
    const decoded = await decodeExpected(response, 200);
    return {
      status: requireString(decoded, 'status'),
      service: requireString(decoded, 'service'),
      version: requireString(decoded, 'version'),
      environment: requireString(decoded, 'environment'),
    };
  }

  /**
   * `POST /v1/waitlist` — submit a waitlist entry.
   *
   * Returns the server's confirmation on 201. Throws
   * {@link ApiValidationException} on a 400 with per-field messages.
   */
  async joinWaitlist(submission: WaitlistSubmission): Promise<WaitlistConfirmation> {
    const response = await this.#fetch(`${this.#baseUrl}/v1/waitlist`, {
      method: 'POST',
      headers: JSON_HEADERS,
      body: JSON.stringify(waitlistSubmissionToJson(submission)),
    });
    const decoded = await decodeExpected(response, 201);
    return {
      id: requireString(decoded, 'id'),
      submittedAt: requireString(decoded, 'submittedAt'),
    };
  }

  /**
   * `GET /v1/me` — the signed-in caller's identity, and the app's "is my
   * access token still good?" probe.
   *
   * Protected: sends `Authorization: Bearer <token>` from the configured
   * {@link InsolviaApiClientOptions.accessToken} provider. Throws
   * {@link ApiUnauthorizedException} when the API answers 401, and — without
   * making a request at all — when there is no token to send.
   *
   * Note the path is `/v1/me`, not `/me`: it is versioned like
   * `/v1/waitlist`, and unlike the unversioned `/health`.
   */
  async me(): Promise<Principal> {
    const headers = await this.#protectedHeaders();
    const response = await this.#fetch(`${this.#baseUrl}/v1/me`, {
      method: 'GET',
      headers,
    });
    const decoded = await decodeExpected(response, 200);
    return {
      subject: requireString(decoded, 'subject'),
      username: requireNullableString(decoded, 'username'),
      clientId: requireString(decoded, 'clientId'),
      scopes: requireStringArray(decoded, 'scopes'),
      expiresAt: requireNullableNumber(decoded, 'expiresAt'),
    };
  }

  /**
   * `POST /v1/cases` — start a new case.
   *
   * Protected: same bearer-token mechanism as {@link me}. Returns the created
   * {@link Case} on 201. Throws {@link ApiValidationException} on a 400 with
   * per-field messages (e.g. an out-of-range chapter).
   */
  async createCase(request: CreateCaseRequest): Promise<Case> {
    const headers = await this.#protectedHeaders();
    const response = await this.#fetch(`${this.#baseUrl}/v1/cases`, {
      method: 'POST',
      headers: { ...headers, 'Content-Type': 'application/json' },
      body: JSON.stringify(createCaseRequestToJson(request)),
    });
    const decoded = await decodeExpected(response, 201);
    return caseFromJson(decoded);
  }

  /**
   * `GET /v1/cases` — the signed-in caller's cases, paginated.
   *
   * `limit` and `cursor` are both optional and, per this package's rule,
   * omitted from the query string entirely when absent — see
   * {@link listCasesQuery}. `nextCursor` on the result is absent, not `null`,
   * on the last page.
   */
  async listCases(options: ListCasesOptions = {}): Promise<ListCasesResult> {
    const headers = await this.#protectedHeaders();
    const query = listCasesQuery(options).toString();
    const url = `${this.#baseUrl}/v1/cases${query === '' ? '' : `?${query}`}`;
    const response = await this.#fetch(url, {
      method: 'GET',
      headers,
    });
    const decoded = await decodeExpected(response, 200);
    const cases = requireCaseArray(decoded, 'cases');
    const nextCursor = optionalString(decoded, 'nextCursor');
    // Built conditionally, not `{ cases, nextCursor }`: a present key with
    // value `undefined` still shows up in `'nextCursor' in result`, and the
    // contract requires the key to be genuinely absent on the last page.
    return nextCursor === undefined ? { cases } : { cases, nextCursor };
  }

  /**
   * `GET /v1/cases/{caseId}` — a single case by id.
   *
   * Throws a plain {@link ApiException} (statusCode `404`) when `caseId` is
   * unknown **or** belongs to a different caller — the API deliberately
   * answers both the same way, so a 404 here must never be rendered as "this
   * case does not exist" in the UI.
   */
  async getCase(caseId: string): Promise<Case> {
    const headers = await this.#protectedHeaders();
    const response = await this.#fetch(`${this.#baseUrl}/v1/cases/${encodeURIComponent(caseId)}`, {
      method: 'GET',
      headers,
    });
    const decoded = await decodeExpected(response, 200);
    return caseFromJson(decoded);
  }

  /**
   * `PATCH /v1/cases/{caseId}` — change a subset of a case's fields.
   *
   * `changes` may hold any subset of `{chapter, district, status}`; omitted
   * keys mean "leave unchanged" and are never sent — see
   * {@link updateCaseChangesToJson}. Returns the updated {@link Case} on 200.
   *
   * Like {@link getCase}, a 404 means unknown *or* not-owned; see that method's
   * note.
   */
  async updateCase(caseId: string, changes: UpdateCaseChanges): Promise<Case> {
    const headers = await this.#protectedHeaders();
    const response = await this.#fetch(`${this.#baseUrl}/v1/cases/${encodeURIComponent(caseId)}`, {
      method: 'PATCH',
      headers: { ...headers, 'Content-Type': 'application/json' },
      body: JSON.stringify(updateCaseChangesToJson(changes)),
    });
    const decoded = await decodeExpected(response, 200);
    return caseFromJson(decoded);
  }

  /** `/v1/cases/{caseId}/documents`, with the id encoded exactly once. */
  #documentsUrl(caseId: string): string {
    return `${this.#baseUrl}/v1/cases/${encodeURIComponent(caseId)}/documents`;
  }

  /** `/v1/cases/{caseId}/documents/{documentId}`. */
  #documentUrl(caseId: string, documentId: string): string {
    return `${this.#documentsUrl(caseId)}/${encodeURIComponent(documentId)}`;
  }

  /**
   * `POST /v1/cases/{caseId}/documents` — record a document and mint a
   * capability to upload its bytes.
   *
   * **This is step one of three, and the record it creates is not a document
   * yet.** It comes back `status: 'pending'`, and a pending record whose
   * {@link completeDocument} never runs is deleted by the bucket's lifecycle
   * rule 24 hours later. Prefer {@link uploadDocument}, which runs all three
   * steps; reach for this only when the PUT has to happen somewhere this
   * client cannot see (a background transfer, a native uploader).
   *
   * Throws {@link ApiValidationException} on a 400 — per-field messages keyed
   * `kind`, `fileName`, `contentType`, `byteSize`. Throws a plain
   * {@link ApiException} (404) when the case is unknown *or* not the caller's;
   * see {@link getCase}.
   */
  async createDocument(
    caseId: string,
    request: CreateDocumentRequest,
  ): Promise<CreateDocumentResult> {
    const headers = await this.#protectedHeaders();
    const response = await this.#fetch(this.#documentsUrl(caseId), {
      method: 'POST',
      headers: { ...headers, 'Content-Type': 'application/json' },
      body: JSON.stringify(createDocumentRequestToJson(request)),
    });
    const decoded = await decodeExpected(response, 201);
    return {
      document: documentFromJson(childObject(decoded, 'document')),
      upload: uploadFromJson(childObject(decoded, 'upload')),
    };
  }

  /**
   * `GET /v1/cases/{caseId}/documents` — every document of one case, newest
   * first.
   *
   * Not paginated (the answer is bounded by one case's paperwork), so this
   * returns the array itself rather than a result object with a cursor.
   *
   * **Pending documents are included**, and filtering them out here would be a
   * mistake: a row whose upload never finished is the case's own record of a
   * file the user tried to add, and {@link Document.status} is what lets the UI
   * offer a retry instead of losing it silently.
   */
  async listDocuments(caseId: string): Promise<readonly Document[]> {
    const headers = await this.#protectedHeaders();
    const response = await this.#fetch(this.#documentsUrl(caseId), {
      method: 'GET',
      headers,
    });
    const decoded = await decodeExpected(response, 200);
    return requireDocumentArray(decoded, 'documents');
  }

  /**
   * `GET /v1/cases/{caseId}/documents/{documentId}/url` — a short-lived URL
   * that serves one document's bytes.
   *
   * Ask at the moment of use and do not cache: the URL expires in minutes, and
   * it is a bearer capability — anything holding it can read the document, so
   * it does not belong in a log, a query string you control, or persisted
   * state. A fresh one is a single round trip.
   *
   * A `'pending'` document has a record but no object: the URL mints fine and
   * answers 404 when followed. Check {@link Document.status} first.
   */
  async getDocumentUrl(caseId: string, documentId: string): Promise<DocumentDownload> {
    const headers = await this.#protectedHeaders();
    const response = await this.#fetch(`${this.#documentUrl(caseId, documentId)}/url`, {
      method: 'GET',
      headers,
    });
    const decoded = await decodeExpected(response, 200);
    return {
      url: requireString(decoded, 'url'),
      method: requireString(decoded, 'method'),
      expiresAt: requireString(decoded, 'expiresAt'),
    };
  }

  /**
   * `POST /v1/cases/{caseId}/documents/{documentId}/complete` — tell the API
   * the PUT finished, and get the confirmed record back.
   *
   * **This is the step that keeps the document.** The server checks the object
   * really is in the bucket, clears the tag the bucket's reaper filters on,
   * and writes the record back as `'stored'` with the size S3 actually
   * counted. Until it runs, the object still carries `upload=unconfirmed` and
   * is deleted 24 hours after it was written.
   *
   * Idempotent — a client that retries because it lost the response gets the
   * same record.
   *
   * Throws a plain {@link ApiException} with `statusCode` **409** when the
   * object is not in the bucket, which means the bytes never arrived: the
   * record stays `'pending'` and the honest next move is to upload again, not
   * to retry this call. {@link isUploadIncomplete} is that check.
   */
  async completeDocument(caseId: string, documentId: string): Promise<Document> {
    const headers = await this.#protectedHeaders();
    const response = await this.#fetch(`${this.#documentUrl(caseId, documentId)}/complete`, {
      method: 'POST',
      headers,
    });
    const decoded = await decodeExpected(response, 200);
    return documentFromJson(childObject(decoded, 'document'));
  }

  /**
   * `DELETE /v1/cases/{caseId}/documents/{documentId}` — remove a document
   * from its case and make its bytes unreachable.
   *
   * Answers **204 with no body**, so this resolves to nothing: there is no
   * record to hand back, and echoing one would invite treating the response as
   * the document.
   *
   * "Unreachable", not "destroyed": the bucket is versioned, so the object
   * stops resolving immediately while the bytes remain as a noncurrent version
   * for 30 days. That is the answer a caller asking under a retention or
   * erasure obligation needs.
   *
   * Throws a plain {@link ApiException} (404) for an unknown document, an
   * unknown case, or a case that is not the caller's — all four the same way.
   */
  async deleteDocument(caseId: string, documentId: string): Promise<void> {
    const headers = await this.#protectedHeaders();
    const response = await this.#fetch(this.#documentUrl(caseId, documentId), {
      method: 'DELETE',
      headers,
    });
    await expectNoContent(response, 204);
  }

  /**
   * Upload a document end to end: create the record, PUT the bytes to the
   * presigned URL, and confirm the upload. Resolves to the **confirmed**
   * record — `status: 'stored'`, with the byte count S3 reported rather than
   * the one that was declared.
   *
   * ```ts
   * const document = await client.uploadDocument(caseId, {
   *   file,
   *   fileName: 'bank-statement-june.pdf',
   *   kind: 'bank_statement',
   *   contentType: 'application/pdf',
   * });
   * ```
   *
   * **Why this method exists rather than three calls at the call site.** The
   * upload is a two-step transaction and the second step is not optional. Every
   * presigned PUT is signed with an `upload=unconfirmed` tag, and the bucket
   * deletes objects still carrying that tag after 24 hours. Confirming is the
   * only thing that clears it. So a caller who creates a document, uploads the
   * bytes, and skips {@link completeDocument} gets a case that lists a
   * `'pending'` document today and, tomorrow, the same record with its bytes
   * gone for good — no error at any point, and nothing to recover. Running the
   * three steps here is what makes that unreachable by accident.
   *
   * **On a partial failure this deliberately leaves the pending record in
   * place.** If the PUT or the confirm throws, the document is still listed as
   * `'pending'`, which is the truth: the user tried to add a file and it did
   * not finish. They can see it and retry it, and if they do neither the
   * lifecycle rule cleans it up within a day. Deleting the record on the way
   * out of a failure would hide a retryable state and lose the file name the
   * user chose. Callers that want it gone can call {@link deleteDocument}.
   *
   * The PUT goes out with **exactly** the headers the API returned and nothing
   * else — no `Authorization`, no `Accept`. Those headers are signed, the URL
   * is itself the credential, and any deviation is a 403 from S3. A failed PUT
   * throws an {@link ApiException} carrying S3's status and body (an XML error
   * document, not the API's JSON envelope).
   *
   * A 409 from the confirm step means the bytes never arrived — see
   * {@link completeDocument} and {@link isUploadIncomplete}.
   */
  async uploadDocument(caseId: string, options: UploadDocumentOptions): Promise<Document> {
    const created = await this.createDocument(caseId, {
      kind: options.kind,
      fileName: options.fileName,
      contentType: options.contentType,
      // From the bytes, never from the caller: this number is bound into the
      // signature, so a declared size that disagrees with the body is a 403
      // with nothing in it to explain why.
      byteSize: options.file.size,
    });

    // The raw transport, NOT #protectedHeaders. A presigned URL carries its own
    // authority in the query string; an Authorization header alongside it makes
    // S3 authenticate the request that way instead and the signature check
    // fails. The injected FetchLike keeps this stubbable in tests all the same.
    const uploaded = await this.#fetch(created.upload.url, {
      method: created.upload.method,
      // Spread verbatim. Nothing is added, nothing is renamed, and the map is
      // not enumerated by key anywhere in this package — a header the server
      // starts signing tomorrow rides along unchanged.
      headers: { ...created.upload.headers },
      body: options.file,
    });
    if (!uploaded.ok) {
      throw new ApiException({
        statusCode: uploaded.status,
        body: await uploaded.text(),
        message: `the presigned upload failed with status ${uploaded.status}`,
      });
    }

    return this.completeDocument(caseId, created.document.id);
  }
}

/**
 * True when `error` is the 409 {@link InsolviaApiClient.completeDocument}
 * answers with because the object is not in the bucket — the bytes never
 * arrived.
 *
 * The one failure of that call a caller can act on, and the action is specific:
 * upload again. Retrying the confirm alone will fail identically forever, and
 * the record is still there and still `'pending'` in the meantime.
 *
 * A predicate rather than an exception subclass because 409 means this only on
 * that one endpoint — a `statusCode === 409` on some future route would be a
 * different thing entirely, and a class named for this one would quietly claim
 * it.
 */
export function isUploadIncomplete(error: unknown): boolean {
  return error instanceof ApiException && error.statusCode === 409;
}

// ---------------------------------------------------------------------------
// Decoding internals — module functions rather than methods. Not exported:
// nothing outside this file may depend on them.
// ---------------------------------------------------------------------------

type JsonObject = Record<string, unknown>;

/**
 * The three outcomes of reading a body as a JSON object, kept as a
 * discriminated union so callers have to answer for each — "not JSON at all"
 * and "JSON, but not an object" carry different messages.
 */
type JsonBody =
  | { readonly kind: 'object'; readonly value: JsonObject }
  | { readonly kind: 'not-json' }
  | { readonly kind: 'not-object' };

/** A response whose body was read once, kept raw alongside its parse. */
interface DecodedResponse {
  readonly statusCode: number;
  readonly body: string;
  readonly json: JsonObject;
}

function parseJsonBody(body: string): JsonBody {
  let decoded: unknown;
  try {
    decoded = JSON.parse(body);
  } catch {
    return { kind: 'not-json' };
  }
  if (typeof decoded !== 'object' || decoded === null || Array.isArray(decoded)) {
    return { kind: 'not-object' };
  }
  return { kind: 'object', value: decoded as JsonObject };
}

/**
 * Decodes `response` as a JSON object when its status is `expectedStatus`;
 * otherwise maps the failure to a typed exception.
 */
async function decodeExpected(
  response: Response,
  expectedStatus: number,
): Promise<DecodedResponse> {
  // Read once, as text: the raw body has to survive onto the exception, and
  // a response body can only be consumed a single time.
  const body = await response.text();
  const statusCode = response.status;
  if (statusCode !== expectedStatus) {
    throw errorFor(statusCode, body);
  }
  const parsed = parseJsonBody(body);
  switch (parsed.kind) {
    case 'object':
      return { statusCode, body, json: parsed.value };
    case 'not-json':
      throw new ApiException({
        statusCode,
        body,
        message: 'response body was not valid JSON',
      });
    case 'not-object':
      throw new ApiException({
        statusCode,
        body,
        message: 'response body was not a JSON object',
      });
  }
}

/**
 * The 204 counterpart of {@link decodeExpected}: checks the status and reads
 * the body, but never parses it.
 *
 * A separate function rather than a flag on `decodeExpected`, because that one
 * assumes a JSON object and would reject a 204's empty body as "not valid
 * JSON" — turning a successful delete into an exception. The body is still
 * read: it is what a failure's exception has to carry, and leaving a response
 * stream unconsumed is a leak on some runtimes. On success it is discarded
 * without a glance, so a proxy that puts something in a 204 cannot fail the
 * call.
 */
async function expectNoContent(response: Response, expectedStatus: number): Promise<void> {
  const body = await response.text();
  if (response.status !== expectedStatus) {
    throw errorFor(response.status, body);
  }
}

/**
 * Maps a non-success response to the most specific exception available:
 * 401 → {@link ApiUnauthorizedException}, `{"error": ..., "fields": {...}}` →
 * {@link ApiValidationException}, anything else (including unparseable
 * bodies) → {@link ApiException}.
 *
 * **Status beats body shape.** The 401 check comes first deliberately: if a
 * 401 body ever grew a `fields` object, matching on the body would hand the
 * app an `ApiValidationException` and it would render field errors under a
 * form instead of refreshing the session or sending the user to sign-in. The
 * status code is the API's statement about *why* the call failed, and it is
 * the one an unauthenticated caller must act on. The 400 path is unaffected:
 * {@link ApiValidationException} still wins there, as its tests pin.
 */
function errorFor(statusCode: number, body: string): ApiException {
  const parsed = parseJsonBody(body);
  if (statusCode === 401) {
    return new ApiUnauthorizedException({
      statusCode,
      body,
      source: 'server',
      message: parsed.kind === 'object' ? envelopeMessage(parsed.value) : undefined,
    });
  }
  if (parsed.kind === 'object') {
    const fields = parsed.value.fields;
    if (typeof fields === 'object' && fields !== null && !Array.isArray(fields)) {
      return new ApiValidationException({
        statusCode,
        body,
        fields: toStringMap(fields as JsonObject),
      });
    }
    const message = envelopeMessage(parsed.value);
    if (message !== undefined) {
      return new ApiException({ statusCode, body, message });
    }
  }
  return new ApiException({ statusCode, body });
}

/**
 * Renders the API's `{"error", "message"}` envelope (app_factory's error
 * handlers) as a summary line, or `undefined` when the body is not one —
 * in which case the exception keeps its own default message.
 */
function envelopeMessage(json: JsonObject): string | undefined {
  const error = json.error;
  if (typeof error !== 'string') {
    return undefined;
  }
  const message = json.message;
  return typeof message === 'string' ? `${error}: ${message}` : error;
}

/** Per-field messages verbatim; a non-string value is stringified, not dropped. */
function toStringMap(source: JsonObject): Record<string, string> {
  const result: Record<string, string> = {};
  for (const [key, value] of Object.entries(source)) {
    result[key] = typeof value === 'string' ? value : String(value);
  }
  return result;
}

/**
 * Reads a required string field, failing loudly rather than handing back an
 * `undefined` typed as `string` — the trap a bare cast would set here, since
 * a TypeScript cast is erased and checks nothing at runtime.
 */
function requireString(response: DecodedResponse, key: string): string {
  const value = response.json[key];
  if (typeof value !== 'string') {
    throw malformedField(response, key, 'string');
  }
  return value;
}

/**
 * Like {@link requireString}, but the API is allowed to send JSON `null` —
 * which stays `null` rather than becoming `undefined`, so the model keeps
 * mirroring the wire. A *missing* key is still a contract violation.
 */
function requireNullableString(response: DecodedResponse, key: string): string | null {
  const value = response.json[key];
  if (value === null) {
    return null;
  }
  if (typeof value !== 'string') {
    throw malformedField(response, key, 'string-or-null');
  }
  return value;
}

/** A nullable JSON number. `NaN`/`Infinity` cannot appear in JSON. */
function requireNullableNumber(response: DecodedResponse, key: string): number | null {
  const value = response.json[key];
  if (value === null) {
    return null;
  }
  if (typeof value !== 'number') {
    throw malformedField(response, key, 'number-or-null');
  }
  return value;
}

/** An array whose every element must be a string — checked, not cast. */
function requireStringArray(response: DecodedResponse, key: string): readonly string[] {
  const value = response.json[key];
  if (!Array.isArray(value) || !value.every((item) => typeof item === 'string')) {
    throw malformedField(response, key, 'string[]');
  }
  return value as readonly string[];
}

/**
 * The one place a "the API sent something else" failure is built, so every
 * reader reports it the same way. The field *name* appears in the message;
 * the field value never does — one of these readers will one day sit over a
 * token or a case field, and a message is a thing that gets logged.
 */
function malformedField(response: DecodedResponse, key: string, expected: string): ApiException {
  return new ApiException({
    statusCode: response.statusCode,
    body: response.body,
    message: `response body was missing the ${expected} field "${key}"`,
  });
}

/**
 * Like {@link requireString}, but the field is allowed to be **absent**
 * entirely, in which case the result is `undefined` — never `null`. Used for
 * {@link ListCasesResult.nextCursor}, which the API omits rather than nulling
 * on the last page.
 */
function optionalString(response: DecodedResponse, key: string): string | undefined {
  const value = response.json[key];
  if (value === undefined) {
    return undefined;
  }
  if (typeof value !== 'string') {
    throw malformedField(response, key, 'string');
  }
  return value;
}

/** A required field that must be one of the four valid case chapters. */
function requireCaseChapter(response: DecodedResponse, key: string): CaseChapter {
  const value = response.json[key];
  if (value === 7 || value === 11 || value === 12 || value === 13) {
    return value;
  }
  throw malformedField(response, key, 'one of 7 | 11 | 12 | 13');
}

/** A required field that must be one of the three valid case statuses. */
function requireCaseStatus(response: DecodedResponse, key: string): CaseStatus {
  const value = response.json[key];
  if (value === 'intake' || value === 'ready_to_file' || value === 'filed') {
    return value;
  }
  throw malformedField(response, key, 'one of "intake" | "ready_to_file" | "filed"');
}

/**
 * Decodes a {@link Case} from a response body: `{"id", "chapter", "district",
 * "status", "createdAt", "updatedAt"}`. Shared by every `/v1/cases` endpoint
 * that returns a single case, and by {@link requireCaseArray} for the list
 * endpoint's page of cases.
 */
function caseFromJson(response: DecodedResponse): Case {
  return {
    id: requireString(response, 'id'),
    chapter: requireCaseChapter(response, 'chapter'),
    district: requireString(response, 'district'),
    status: requireCaseStatus(response, 'status'),
    createdAt: requireString(response, 'createdAt'),
    updatedAt: requireString(response, 'updatedAt'),
  };
}

/**
 * An array field whose every element must decode as a {@link Case} — checked
 * per-element, not cast. Each element borrows the parent response's
 * `statusCode`/`body` so a malformed-element exception still carries the raw
 * page body for diagnostics.
 */
function requireCaseArray(response: DecodedResponse, key: string): readonly Case[] {
  const value = response.json[key];
  if (!Array.isArray(value)) {
    throw malformedField(response, key, 'Case[]');
  }
  return value.map((item, index) => {
    if (typeof item !== 'object' || item === null || Array.isArray(item)) {
      throw malformedField(response, `${key}[${index}]`, 'object');
    }
    return caseFromJson({
      statusCode: response.statusCode,
      body: response.body,
      json: item as JsonObject,
    });
  });
}

/** A required whole-number field. `NaN`/`Infinity` cannot appear in JSON. */
function requireNumber(response: DecodedResponse, key: string): number {
  const value = response.json[key];
  if (typeof value !== 'number') {
    throw malformedField(response, key, 'number');
  }
  return value;
}

/**
 * A nested JSON object, returned as a {@link DecodedResponse} of its own so
 * every reader above works against it unchanged. It borrows the parent's
 * `statusCode`/`body`, so a failure inside still reports the whole response —
 * the same trick {@link requireCaseArray} uses per element.
 */
function childObject(response: DecodedResponse, key: string): DecodedResponse {
  const value = response.json[key];
  if (typeof value !== 'object' || value === null || Array.isArray(value)) {
    throw malformedField(response, key, 'object');
  }
  return {
    statusCode: response.statusCode,
    body: response.body,
    json: value as JsonObject,
  };
}

/**
 * A field holding an object of string-to-string, with **no assumption about
 * which keys are in it** — the shape {@link DocumentUpload.headers} needs.
 *
 * Every value is checked rather than cast: an unchecked map would let a
 * non-string through as a header value, and `String(undefined)` in an HTTP
 * header is the kind of failure that surfaces as an unexplained 403 from S3
 * rather than as a decode error here.
 */
function requireStringRecord(
  response: DecodedResponse,
  key: string,
): Readonly<Record<string, string>> {
  const value = response.json[key];
  if (typeof value !== 'object' || value === null || Array.isArray(value)) {
    throw malformedField(response, key, 'object');
  }
  const result: Record<string, string> = {};
  for (const [name, item] of Object.entries(value as JsonObject)) {
    if (typeof item !== 'string') {
      throw malformedField(response, `${key}.${name}`, 'string');
    }
    result[name] = item;
  }
  return result;
}

/**
 * A required field that must be one of the two valid document statuses.
 *
 * Checked against the runtime {@link DOCUMENT_STATUSES} array the
 * {@link DocumentStatus} type is derived from, so the check and the type
 * cannot disagree. Strict, unlike this decoder's treatment of `kind` and
 * `contentType`: {@link Document} explains why this one field earns it.
 */
function requireDocumentStatus(response: DecodedResponse, key: string): DocumentStatus {
  const value = response.json[key];
  if (typeof value === 'string' && (DOCUMENT_STATUSES as readonly string[]).includes(value)) {
    return value as DocumentStatus;
  }
  throw malformedField(
    response,
    key,
    `one of ${DOCUMENT_STATUSES.map((status) => `"${status}"`).join(' | ')}`,
  );
}

/**
 * Decodes a {@link Document} from `document_json`'s eight fields. Shared by
 * the create, complete and list endpoints.
 */
function documentFromJson(response: DecodedResponse): Document {
  return {
    id: requireString(response, 'id'),
    caseId: requireString(response, 'caseId'),
    kind: requireString(response, 'kind'),
    fileName: requireString(response, 'fileName'),
    contentType: requireString(response, 'contentType'),
    byteSize: requireNumber(response, 'byteSize'),
    uploadedAt: requireString(response, 'uploadedAt'),
    status: requireDocumentStatus(response, 'status'),
  };
}

/** Decodes the `upload` block of a create response. */
function uploadFromJson(response: DecodedResponse): DocumentUpload {
  return {
    url: requireString(response, 'url'),
    method: requireString(response, 'method'),
    headers: requireStringRecord(response, 'headers'),
    expiresAt: requireString(response, 'expiresAt'),
  };
}

/** {@link requireCaseArray} for documents — per-element, checked, not cast. */
function requireDocumentArray(response: DecodedResponse, key: string): readonly Document[] {
  const value = response.json[key];
  if (!Array.isArray(value)) {
    throw malformedField(response, key, 'Document[]');
  }
  return value.map((item, index) => {
    if (typeof item !== 'object' || item === null || Array.isArray(item)) {
      throw malformedField(response, `${key}[${index}]`, 'object');
    }
    return documentFromJson({
      statusCode: response.statusCode,
      body: response.body,
      json: item as JsonObject,
    });
  });
}
