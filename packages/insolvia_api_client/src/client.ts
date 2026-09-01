import { ApiException, ApiUnauthorizedException, ApiValidationException } from './exceptions.ts';
import {
  DOCUMENT_STATUSES,
  COUNSELING_EXEMPTIONS,
  COUNSELING_STATUSES,
  FILING_ROLES,
  PROVENANCE_SOURCES,
  VENUE_BASES,
  addFirmUserRequestToJson,
  caseEntityRequestToJson,
  createCaseRequestToJson,
  createDocumentRequestToJson,
  listCasesQuery,
  putDebtorRequestToJson,
  updateCaseChangesToJson,
  updateFirmRequestToJson,
  updateFirmUserRequestToJson,
  updateMeRequestToJson,
  waitlistSubmissionToJson,
} from './models.ts';
import type {
  AddFirmUserRequest,
  Address,
  Case,
  CaseAssignee,
  CaseChapter,
  CaseStatus,
  Firm,
  FirmColleague,
  FirmFeature,
  FirmMembership,
  FirmRole,
  FirmStatus,
  FirmUser,
  FirmUserStatus,
  PermissionLevel,
  UpdateFirmRequest,
  UpdateFirmUserRequest,
  UpdateMeRequest,
  CreateCaseRequest,
  CreateDocumentRequest,
  CreateDocumentResult,
  Document,
  DocumentDownload,
  DocumentStatus,
  DocumentUpload,
  CaseCollection,
  CaseEntity,
  CaseEntityRequest,
  CreditCounseling,
  Debtor,
  FilingRole,
  HealthStatus,
  ListCasesOptions,
  ListCasesResult,
  OtherName,
  PersonName,
  Principal,
  ProvenanceEntry,
  ProvenanceMap,
  PutDebtorRequest,
  UpdateCaseChanges,
  UploadDocumentOptions,
  Venue,
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
    // `firm` is absent — not null — for a caller nobody has added to a firm
    // yet. That is a state, not a failure: every other authenticated endpoint
    // answers 403 for them, and this is the one that says so as an answer, so
    // a client can render "ask your administrator" instead of an error screen.
    const firm = optionalFirmMembership(decoded);
    return {
      subject: requireString(decoded, 'subject'),
      username: requireNullableString(decoded, 'username'),
      clientId: requireString(decoded, 'clientId'),
      scopes: requireStringArray(decoded, 'scopes'),
      expiresAt: requireNullableNumber(decoded, 'expiresAt'),
      ...(firm === undefined ? {} : { firm }),
    };
  }

  /**
   * `PATCH /v1/me` — correct your own name.
   *
   * Your name only; see {@link UpdateMeRequest} for why nothing else is
   * self-service, and why either half alone is a legitimate body. Any ACTIVE
   * firm member may call it — no permission level is required, which is the
   * point of the endpoint. Answers with the same body as {@link me}, so the
   * caller re-renders from the response instead of following up with a GET —
   * which is also how a client holding a cached `/v1/me` refreshes it after a
   * rename. Throws {@link ApiValidationException} on a 400 with a per-field
   * message (`firstName` / `lastName`), and a plain {@link ApiException} with
   * `statusCode` 403 for a caller in no firm — unlike the GET, which reports
   * that state as an answer, there is no row to rename.
   */
  async updateMe(request: UpdateMeRequest): Promise<Principal> {
    const headers = await this.#protectedHeaders();
    const response = await this.#fetch(`${this.#baseUrl}/v1/me`, {
      method: 'PATCH',
      headers: { ...headers, 'Content-Type': 'application/json' },
      body: JSON.stringify(updateMeRequestToJson(request)),
    });
    const decoded = await decodeExpected(response, 200);
    const firm = optionalFirmMembership(decoded);
    return {
      subject: requireString(decoded, 'subject'),
      username: requireNullableString(decoded, 'username'),
      clientId: requireString(decoded, 'clientId'),
      scopes: requireStringArray(decoded, 'scopes'),
      expiresAt: requireNullableNumber(decoded, 'expiresAt'),
      ...(firm === undefined ? {} : { firm }),
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

  /**
   * `PUT /v1/cases/{caseId}/debtors/{filingRole}` — save one debtor of a case,
   * whole, and get the saved record back.
   *
   * **The whole record, every time.** This replaces rather than merges, so a
   * field left out of `debtor` is cleared. See {@link PutDebtorRequest} for why
   * the endpoint is a PUT.
   *
   * Every populated field needs a `provenance` entry or the API answers 400;
   * {@link staffTypedProvenance} builds that map.
   *
   * **Accepts 200 and 201, and nothing else in between.** The API answers 201
   * when the role had no record yet and 200 when it replaced one — the same
   * request either way, since an autosave neither knows nor cares which it is
   * doing. The two are listed explicitly rather than testing `response.ok`,
   * because "any 2xx" would quietly swallow a 202 or a 204 from a proxy that
   * never reached this endpoint, and then fail on the missing body with a
   * message about a malformed debtor.
   *
   * The distinction is deliberately not returned: nothing in the app branches
   * on it, and the record carries `created_at` and `updated_at` for anything
   * that would.
   *
   * Like {@link getCase}, a 404 means the case is unknown *or* not the
   * caller's; see that method's note.
   */
  async putDebtor(
    caseId: string,
    filingRole: FilingRole,
    debtor: PutDebtorRequest,
  ): Promise<Debtor> {
    const headers = await this.#protectedHeaders();
    // `filingRole` is a union of three URL-safe literals, so encoding it is a
    // no-op today. It is encoded anyway: this client is consumed from
    // JavaScript too, where the type says nothing at runtime, and a path
    // segment that reaches a URL unencoded is a rule with no exceptions.
    const url = `${this.#baseUrl}/v1/cases/${encodeURIComponent(caseId)}/debtors/${encodeURIComponent(filingRole)}`;
    const response = await this.#fetch(url, {
      method: 'PUT',
      headers: { ...headers, 'Content-Type': 'application/json' },
      body: JSON.stringify(putDebtorRequestToJson(debtor)),
    });
    const decoded = await decodeExpectedOneOf(response, [200, 201]);
    return debtorFromJson(decoded);
  }

  /**
   * `GET /v1/cases/{caseId}/debtors` — every debtor of one case, in the order
   * the forms print them (`debtor_1`, `debtor_2`, `non_filing_spouse`).
   *
   * The wire body is `{"debtors": [...]}`; this returns the array. There is no
   * pagination and no second key to carry — a case has at most three debtors —
   * so a result object would be an envelope with one thing in it, unlike
   * {@link ListCasesResult}, which exists to carry `nextCursor`.
   *
   * A case with no debtors saved yet is an empty array, not a 404. Like
   * {@link getCase}, a 404 means the case is unknown *or* not the caller's.
   */
  async listDebtors(caseId: string): Promise<readonly Debtor[]> {
    const headers = await this.#protectedHeaders();
    const response = await this.#fetch(
      `${this.#baseUrl}/v1/cases/${encodeURIComponent(caseId)}/debtors`,
      { method: 'GET', headers },
    );
    const decoded = await decodeExpected(response, 200);
    return requireDebtorArray(decoded, 'debtors');
  }

  /**
   * `POST /v1/cases/{caseId}/{collection}` — add one record to a generic case
   * collection (issue #249): creditors, claims, assets, employments,
   * income_summaries, households, expenses, dependents, codebtors,
   * sofa_entries. The server mints the id and answers 201 with the stored
   * record.
   *
   * The provenance rules are the debtor's, enforced identically: every
   * populated field needs an entry ({@link staffTypedProvenance} builds the
   * ordinary map), and machine-supplied values must be confirmed. Like
   * {@link getCase}, a 404 means the case is unknown *or* not the caller's.
   */
  async addCaseEntity<C extends CaseCollection>(
    caseId: string,
    collection: C,
    request: CaseEntityRequest<C>,
  ): Promise<CaseEntity<C>> {
    const headers = await this.#protectedHeaders();
    const response = await this.#fetch(this.#collectionUrl(caseId, collection), {
      method: 'POST',
      headers: { ...headers, 'Content-Type': 'application/json' },
      body: JSON.stringify(caseEntityRequestToJson(request)),
    });
    const decoded = await decodeExpected(response, 201);
    return caseEntityFromJson<C>(decoded);
  }

  /**
   * `GET /v1/cases/{caseId}/{collection}` — every record of one collection in
   * one case, in creation order (the order the rows were added, which holds
   * still while someone works down the schedule).
   *
   * The wire body is `{"<collection>": [...]}`; this returns the array. No
   * pagination — the answer is bounded by one case's schedule, and the server
   * promises all of it.
   */
  async listCaseEntities<C extends CaseCollection>(
    caseId: string,
    collection: C,
  ): Promise<readonly CaseEntity<C>[]> {
    const headers = await this.#protectedHeaders();
    const response = await this.#fetch(this.#collectionUrl(caseId, collection), {
      method: 'GET',
      headers,
    });
    const decoded = await decodeExpected(response, 200);
    return requireCaseEntityArray<C>(decoded, collection);
  }

  /**
   * `GET /v1/cases/{caseId}/{collection}/{entityId}` — one record. A 404
   * covers the unknown id, the foreign case, AND an id that belongs to a
   * different collection of the same case — none are distinguishable, by the
   * same anti-oracle rule {@link getCase} states.
   */
  async getCaseEntity<C extends CaseCollection>(
    caseId: string,
    collection: C,
    entityId: string,
  ): Promise<CaseEntity<C>> {
    const headers = await this.#protectedHeaders();
    const response = await this.#fetch(
      `${this.#collectionUrl(caseId, collection)}/${encodeURIComponent(entityId)}`,
      { method: 'GET', headers },
    );
    const decoded = await decodeExpected(response, 200);
    return caseEntityFromJson<C>(decoded);
  }

  /**
   * `PUT /v1/cases/{caseId}/{collection}/{entityId}` — replace one record,
   * WHOLE: anything left out is gone, for the same invariant-1 reason
   * {@link putDebtor} states. There is no upsert — ids are server-minted, so
   * an id the server never issued answers 404 rather than creating.
   */
  async putCaseEntity<C extends CaseCollection>(
    caseId: string,
    collection: C,
    entityId: string,
    request: CaseEntityRequest<C>,
  ): Promise<CaseEntity<C>> {
    const headers = await this.#protectedHeaders();
    const response = await this.#fetch(
      `${this.#collectionUrl(caseId, collection)}/${encodeURIComponent(entityId)}`,
      {
        method: 'PUT',
        headers: { ...headers, 'Content-Type': 'application/json' },
        body: JSON.stringify(caseEntityRequestToJson(request)),
      },
    );
    const decoded = await decodeExpected(response, 200);
    return caseEntityFromJson<C>(decoded);
  }

  /**
   * `DELETE /v1/cases/{caseId}/{collection}/{entityId}` — remove one record.
   * 204 with no body; a second delete of the same id answers 404.
   *
   * References are not cascaded server-side: a claim naming a deleted
   * creditor keeps its `creditor_id`, and the completeness gate (9.6) is
   * where the dangling reference becomes an error.
   */
  async deleteCaseEntity(
    caseId: string,
    collection: CaseCollection,
    entityId: string,
  ): Promise<void> {
    const headers = await this.#protectedHeaders();
    const response = await this.#fetch(
      `${this.#collectionUrl(caseId, collection)}/${encodeURIComponent(entityId)}`,
      { method: 'DELETE', headers },
    );
    await expectNoContent(response, 204);
  }

  /**
   * The base URL of one collection. `collection` is a union of URL-safe
   * literals, encoded anyway — the same rule {@link putDebtor} states about
   * its role segment.
   */
  #collectionUrl(caseId: string, collection: CaseCollection): string {
    return `${this.#baseUrl}/v1/cases/${encodeURIComponent(caseId)}/${encodeURIComponent(collection)}`;
  }

  /**
   * `GET /v1/firm` — the firm's own record (issue #217).
   *
   * Administrators only (`firm_administration` at `view_only`). The name it
   * returns is the same row `/v1/me` reads, so this is what every member's
   * header already shows. See {@link Firm} for what the tenant shape
   * deliberately does not carry.
   */
  async getFirm(): Promise<Firm> {
    const headers = await this.#protectedHeaders();
    const response = await this.#fetch(`${this.#baseUrl}/v1/firm`, {
      method: 'GET',
      headers,
    });
    const decoded = await decodeExpected(response, 200);
    return firmFromJson(decoded);
  }

  /**
   * `PATCH /v1/firm` — rename the firm (issue #217).
   *
   * `firm_administration` at `add_edit`. Name only — {@link UpdateFirmRequest}
   * owns why `status` is absent and never joins. The new name reaches every
   * member on their next `/v1/me`; nothing else needs invalidating. Throws
   * {@link ApiValidationException} on a 400 with a per-field message.
   */
  async updateFirm(request: UpdateFirmRequest): Promise<Firm> {
    const headers = await this.#protectedHeaders();
    const response = await this.#fetch(`${this.#baseUrl}/v1/firm`, {
      method: 'PATCH',
      headers: { ...headers, 'Content-Type': 'application/json' },
      body: JSON.stringify(updateFirmRequestToJson(request)),
    });
    const decoded = await decodeExpected(response, 200);
    return firmFromJson(decoded);
  }

  /**
   * `GET /v1/firm/directory` — everyone in the caller's firm, as names.
   *
   * Available to anyone who can view cases, not only administrators, because
   * this is what turns a subject into a person: {@link Case.createdBy} and
   * every {@link CaseAssignee} are Cognito subjects, and without this the case
   * list reads "opened by 00000000-0000-…".
   *
   * Includes colleagues whose accounts are DISABLED. A case opened by somebody
   * who has since left still names them, and dropping them here would turn
   * that into an unresolvable id — this is for rendering history, not for
   * populating a picker.
   *
   * Throws {@link ApiException} (403) when the caller is in no firm, or their
   * firm has not granted them `cases`.
   */
  async listFirmDirectory(): Promise<readonly FirmColleague[]> {
    const headers = await this.#protectedHeaders();
    const response = await this.#fetch(`${this.#baseUrl}/v1/firm/directory`, {
      method: 'GET',
      headers,
    });
    const decoded = await decodeExpected(response, 200);
    return requireArrayOf(decoded, 'people', 'FirmColleague', (element) => ({
      subject: requireString(element, 'subject'),
      firstName: requireString(element, 'firstName'),
      lastName: requireString(element, 'lastName'),
      displayName: requireString(element, 'displayName'),
      role: requireFirmRole(element, 'role'),
    }));
  }

  /**
   * `GET /v1/firm/users` — the firm's whole staff list, with everything on it.
   *
   * Administrators only (`firm_administration`). For rendering a name, use
   * {@link listFirmDirectory} instead: it needs no administrative permission
   * and carries no email addresses.
   */
  async listFirmUsers(): Promise<readonly FirmUser[]> {
    const headers = await this.#protectedHeaders();
    const response = await this.#fetch(`${this.#baseUrl}/v1/firm/users`, {
      method: 'GET',
      headers,
    });
    const decoded = await decodeExpected(response, 200);
    return requireArrayOf(decoded, 'users', 'FirmUser', firmUserFromJson);
  }

  /**
   * `POST /v1/firm/users` — add a colleague to the caller's firm.
   *
   * The server creates their Cognito account and emails them a temporary
   * password; **nothing in this flow returns or reveals it**, here or on the
   * server. There is no firm id in the request — the colleague joins the
   * caller's own firm, which is not a value a client gets to choose.
   *
   * Throws {@link ApiValidationException} on a 400 (per-field: `email`,
   * `firstName`, `lastName`, `role`, `isAdmin`, `accessAllCases`,
   * `permissions`), and a
   * plain {@link ApiException} with status 409 when the address already has an
   * Insolvia account.
   */
  async addFirmUser(request: AddFirmUserRequest): Promise<FirmUser> {
    const headers = await this.#protectedHeaders();
    const response = await this.#fetch(`${this.#baseUrl}/v1/firm/users`, {
      method: 'POST',
      headers: { ...headers, 'Content-Type': 'application/json' },
      body: JSON.stringify(addFirmUserRequestToJson(request)),
    });
    const decoded = await decodeExpected(response, 201);
    return firmUserFromJson(decoded);
  }

  /**
   * `PATCH /v1/firm/users/{subject}` — change a colleague's standing.
   *
   * **`permissions` replaces the stored map** — see
   * {@link UpdateFirmUserRequest}.
   *
   * Throws {@link ApiException} with status **409** when the change would
   * leave the firm with no active administrator. That is not a permission
   * failure and should not be reported as one: self-signup is disabled, so
   * such a firm could not appoint one back. Surface it as "appoint another
   * administrator first".
   *
   * A 404 means the subject is not in the caller's firm *or* does not exist —
   * the two are deliberately indistinguishable.
   */
  async updateFirmUser(subject: string, request: UpdateFirmUserRequest): Promise<FirmUser> {
    const headers = await this.#protectedHeaders();
    const response = await this.#fetch(this.#firmUserUrl(subject), {
      method: 'PATCH',
      headers: { ...headers, 'Content-Type': 'application/json' },
      body: JSON.stringify(updateFirmUserRequestToJson(request)),
    });
    const decoded = await decodeExpected(response, 200);
    return firmUserFromJson(decoded);
  }

  /**
   * `DELETE /v1/firm/users/{subject}` — remove a colleague from the firm.
   *
   * **Removes the membership, not the person.** Their Cognito account
   * survives, so what they get is a working sign-in and a 403 everywhere —
   * the same state as somebody who has not been added yet. Disabling
   * (`updateFirmUser(subject, { status: 'disabled' })`) is usually what a firm
   * actually wants, because it keeps the record and the history.
   *
   * 409 when it would leave the firm with no active administrator; 404 when
   * the subject is not in the caller's firm.
   */
  async removeFirmUser(subject: string): Promise<void> {
    const headers = await this.#protectedHeaders();
    const response = await this.#fetch(this.#firmUserUrl(subject), {
      method: 'DELETE',
      headers,
    });
    await expectNoContent(response, 204);
  }

  /** `/v1/firm/users/{subject}`, with the subject encoded exactly once. */
  #firmUserUrl(subject: string): string {
    return `${this.#baseUrl}/v1/firm/users/${encodeURIComponent(subject)}`;
  }

  /** `/v1/cases/{caseId}/assignees/{subject}`, each segment encoded once. */
  #assigneeUrl(caseId: string, subject: string): string {
    return `${this.#baseUrl}/v1/cases/${encodeURIComponent(caseId)}/assignees/${encodeURIComponent(subject)}`;
  }

  /**
   * `GET /v1/cases/{caseId}/assignees` — who is linked to this case.
   *
   * Subjects, not names: resolve them through {@link listFirmDirectory}.
   * Copying a display name onto an assignment would go stale the moment
   * somebody is renamed.
   *
   * A 404 means the case is unknown, another firm's, *or* one the caller is
   * not linked to — see {@link getCase}.
   */
  async listCaseAssignees(caseId: string): Promise<readonly CaseAssignee[]> {
    const headers = await this.#protectedHeaders();
    const response = await this.#fetch(
      `${this.#baseUrl}/v1/cases/${encodeURIComponent(caseId)}/assignees`,
      { method: 'GET', headers },
    );
    const decoded = await decodeExpected(response, 200);
    return requireArrayOf(decoded, 'assignees', 'CaseAssignee', (element) => ({
      subject: requireString(element, 'subject'),
      assignedAt: requireString(element, 'assignedAt'),
      assignedBy: requireString(element, 'assignedBy'),
    }));
  }

  /**
   * `PUT /v1/cases/{caseId}/assignees/{subject}` — put a colleague on a case.
   *
   * **Idempotent.** Linking somebody already on the matter succeeds, which is
   * why it is a PUT: a client that lost a response can simply repeat it.
   *
   * Needs `cases: add_edit`, not an administrative permission — putting a
   * colleague on a matter is case work. A 404 covers a case the caller cannot
   * reach *and* a subject who is not in their firm, deliberately: telling them
   * apart would be a probe for who works where.
   */
  async assignCase(caseId: string, subject: string): Promise<void> {
    const headers = await this.#protectedHeaders();
    const response = await this.#fetch(this.#assigneeUrl(caseId, subject), {
      method: 'PUT',
      headers,
    });
    await expectNoContent(response, 204);
  }

  /**
   * `DELETE /v1/cases/{caseId}/assignees/{subject}` — take a colleague off.
   *
   * **A caller can unlink themselves and lose the case they were just
   * editing.** That is the honest consequence of "I am no longer on this
   * matter"; a client should confirm before doing it to the signed-in user.
   *
   * Unlinking the LAST person is allowed — the firm's administrators still
   * reach the case, so it can always be reassigned. 404 when the subject is
   * not on the case.
   */
  async unassignCase(caseId: string, subject: string): Promise<void> {
    const headers = await this.#protectedHeaders();
    const response = await this.#fetch(this.#assigneeUrl(caseId, subject), {
      method: 'DELETE',
      headers,
    });
    await expectNoContent(response, 204);
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
  /**
   * The dotted path of the object {@link json} sits at within the response
   * body, or `undefined` at the top level. Only {@link malformedField} reads
   * it, so a top-level read's message is unchanged.
   *
   * It exists for debtors, whose fields nest three deep. The API keys its own
   * 400 bodies and its provenance map by exactly these dotted paths, so
   * reporting `residence_address.city` rather than a bare `city` means a
   * decode failure and a server-side complaint name the same thing.
   */
  readonly path?: string | undefined;
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
  return decodeExpectedOneOf(response, [expectedStatus]);
}

/**
 * {@link decodeExpected} for an endpoint with more than one success status —
 * today only `putDebtor`, which is 201 on create and 200 on update.
 *
 * The statuses are ENUMERATED rather than matched as a range. "Any 2xx" would
 * accept a 204 or a 202 that some proxy produced without ever reaching the
 * endpoint, and the failure would then surface as a malformed body rather than
 * as the unexpected status it is.
 */
async function decodeExpectedOneOf(
  response: Response,
  expectedStatuses: readonly number[],
): Promise<DecodedResponse> {
  // Read once, as text: the raw body has to survive onto the exception, and
  // a response body can only be consumed a single time.
  const body = await response.text();
  const statusCode = response.status;
  if (!expectedStatuses.includes(statusCode)) {
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
  const path = response.path === undefined ? key : `${response.path}.${key}`;
  return new ApiException({
    statusCode: response.statusCode,
    body: response.body,
    message: `response body was missing the ${expected} field "${path}"`,
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
    createdBy: requireString(response, 'createdBy'),
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

// ---------------------------------------------------------------------------
// Debtor decoding. Mirrors `debtor_json` in
// `services/api/src/insolvia_api/core/debtors.py`: server-stamped identity and
// `provenance` are always present, and every case-data member is absent when
// it holds nothing — including nested objects and lists, which the API prunes
// away entirely rather than sending empty.
//
// So almost everything here is an `optional*` reader, and each one distinguishes
// "absent" from "present but the wrong type": the first is the ordinary state
// of a half-finished intake, the second is a contract violation and throws.
// ---------------------------------------------------------------------------

/**
 * Builds an object out of only the members that have a value.
 *
 * A member whose value is `undefined` is left OUT rather than set to
 * `undefined`, for the reason `listCases` states about `nextCursor`: a present
 * key with an undefined value still answers `true` to `'k' in obj`, and a
 * decoded {@link Debtor} is handed straight back to `putDebtor`, so "the API
 * did not send a name" and "the name is empty" have to stay distinguishable.
 *
 * The argument type demands every member of `T` — misspell one and it is a
 * compile error, not a silently missing field — while allowing each to be
 * `undefined`. The cast at the end is sound by construction: every member was
 * produced by a reader on the line above, and a *required* member of `T` comes
 * from a `require*` reader, which throws rather than returning `undefined`.
 */
function definedMembers<T extends object>(members: {
  readonly [K in keyof T]: T[K] | undefined;
}): T {
  const built: Record<string, unknown> = {};
  for (const [key, value] of Object.entries(members)) {
    if (value !== undefined) {
      built[key] = value;
    }
  }
  return built as T;
}

/**
 * A nested JSON object read as a {@link DecodedResponse} of its own, so every
 * reader in this file works on it unchanged, and `undefined` when the key is
 * absent. `path` carries the dotted prefix so a failure inside names the field
 * the way the API does.
 */
function optionalObject(response: DecodedResponse, key: string): DecodedResponse | undefined {
  const value = response.json[key];
  if (value === undefined) {
    return undefined;
  }
  if (typeof value !== 'object' || value === null || Array.isArray(value)) {
    throw malformedField(response, key, 'object');
  }
  return {
    statusCode: response.statusCode,
    body: response.body,
    json: value as JsonObject,
    path: response.path === undefined ? key : `${response.path}.${key}`,
  };
}

/** A number field that may be absent. `NaN`/`Infinity` cannot appear in JSON. */
function optionalNumber(response: DecodedResponse, key: string): number | undefined {
  const value = response.json[key];
  if (value === undefined) {
    return undefined;
  }
  if (typeof value !== 'number') {
    throw malformedField(response, key, 'number');
  }
  return value;
}

/** An array of strings that may be absent — the API omits it when empty. */
function optionalStringArray(
  response: DecodedResponse,
  key: string,
): readonly string[] | undefined {
  const value = response.json[key];
  if (value === undefined) {
    return undefined;
  }
  if (!Array.isArray(value) || !value.every((item) => typeof item === 'string')) {
    throw malformedField(response, key, 'string[]');
  }
  return value as readonly string[];
}

/**
 * A field that must be one of `allowed`, or absent.
 *
 * Generic where {@link requireCaseChapter} and {@link requireCaseStatus} are
 * hand-written: a debtor carries four of these, and each `allowed` list is the
 * exported constant that also derives the union type — so the check and the
 * type cannot drift, and neither can drift from `core/debtors.py` without a
 * test here failing.
 */
function optionalChoice<T extends string>(
  response: DecodedResponse,
  key: string,
  allowed: readonly T[],
): T | undefined {
  const value = response.json[key];
  if (value === undefined) {
    return undefined;
  }
  if (typeof value !== 'string' || !allowed.includes(value as T)) {
    throw malformedField(response, key, `one of ${allowed.map((one) => `"${one}"`).join(' | ')}`);
  }
  return value as T;
}

/** {@link optionalChoice} for a field the API always sends. */
function requireChoice<T extends string>(
  response: DecodedResponse,
  key: string,
  allowed: readonly T[],
): T {
  const value = optionalChoice(response, key, allowed);
  if (value === undefined) {
    throw malformedField(response, key, `one of ${allowed.map((one) => `"${one}"`).join(' | ')}`);
  }
  return value;
}

function optionalPersonName(response: DecodedResponse, key: string): PersonName | undefined {
  const nested = optionalObject(response, key);
  if (nested === undefined) {
    return undefined;
  }
  return definedMembers<PersonName>({
    given: optionalString(nested, 'given'),
    middle: optionalString(nested, 'middle'),
    surname: optionalString(nested, 'surname'),
    suffix: optionalString(nested, 'suffix'),
  });
}

function optionalAddress(response: DecodedResponse, key: string): Address | undefined {
  const nested = optionalObject(response, key);
  if (nested === undefined) {
    return undefined;
  }
  return definedMembers<Address>({
    line1: optionalString(nested, 'line1'),
    line2: optionalString(nested, 'line2'),
    city: optionalString(nested, 'city'),
    state: optionalString(nested, 'state'),
    postal_code: optionalString(nested, 'postal_code'),
  });
}

function optionalVenue(response: DecodedResponse, key: string): Venue | undefined {
  const nested = optionalObject(response, key);
  if (nested === undefined) {
    return undefined;
  }
  return definedMembers<Venue>({
    basis: optionalChoice(nested, 'basis', VENUE_BASES),
    explanation: optionalString(nested, 'explanation'),
  });
}

function optionalCreditCounseling(
  response: DecodedResponse,
  key: string,
): CreditCounseling | undefined {
  const nested = optionalObject(response, key);
  if (nested === undefined) {
    return undefined;
  }
  return definedMembers<CreditCounseling>({
    status: optionalChoice(nested, 'status', COUNSELING_STATUSES),
    exemption_reason: optionalChoice(nested, 'exemption_reason', COUNSELING_EXEMPTIONS),
  });
}

/**
 * The alias rows, checked per element. Elements are reported by INDEX here
 * (`other_names_used[0]`) rather than by the id provenance addresses them
 * with: a malformed row is exactly the case where the id may be the thing
 * that is wrong, and the API's own per-row 400 keys are indexed the same way.
 */
function optionalOtherNames(
  response: DecodedResponse,
  key: string,
): readonly OtherName[] | undefined {
  const value = response.json[key];
  if (value === undefined) {
    return undefined;
  }
  if (!Array.isArray(value)) {
    throw malformedField(response, key, 'OtherName[]');
  }
  return value.map((item, index) => {
    const label = `${key}[${index}]`;
    if (typeof item !== 'object' || item === null || Array.isArray(item)) {
      throw malformedField(response, label, 'object');
    }
    const row: DecodedResponse = {
      statusCode: response.statusCode,
      body: response.body,
      json: item as JsonObject,
      path: response.path === undefined ? label : `${response.path}.${label}`,
    };
    return definedMembers<OtherName>({
      id: requireString(row, 'id'),
      given: optionalString(row, 'given'),
      middle: optionalString(row, 'middle'),
      surname: optionalString(row, 'surname'),
      business_name: optionalString(row, 'business_name'),
    });
  });
}

/**
 * The `provenance` map — always present on a debtor, `{}` on a record with
 * nothing in it.
 *
 * **The keys are not validated against the field-path grammar.** The server
 * refused anything malformed on the way in, and re-deriving the grammar here
 * would put it in two places and produce a client that cannot read a record
 * the API was happy to store. What is checked is the shape of each entry,
 * which the app reads.
 */
function requireProvenanceMap(response: DecodedResponse, key: string): ProvenanceMap {
  const map = optionalObject(response, key);
  if (map === undefined) {
    throw malformedField(response, key, 'object');
  }
  const entries: Record<string, ProvenanceEntry> = {};
  for (const path of Object.keys(map.json)) {
    const entry = optionalObject(map, path);
    if (entry === undefined) {
      throw malformedField(map, path, 'object');
    }
    entries[path] = definedMembers<ProvenanceEntry>({
      source: requireChoice(entry, 'source', PROVENANCE_SOURCES),
      confirmed_by: optionalString(entry, 'confirmed_by'),
      confirmed_at: optionalString(entry, 'confirmed_at'),
      document_id: optionalString(entry, 'document_id'),
      locator: optionalObject(entry, 'locator')?.json,
      extraction_id: optionalString(entry, 'extraction_id'),
      confidence: optionalNumber(entry, 'confidence'),
    });
  }
  return entries;
}

/** Decodes a {@link Debtor} from a response body — `debtor_json`'s exact shape. */
function debtorFromJson(response: DecodedResponse): Debtor {
  return definedMembers<Debtor>({
    id: requireString(response, 'id'),
    case_id: requireString(response, 'case_id'),
    filing_role: requireChoice(response, 'filing_role', FILING_ROLES),
    created_at: requireString(response, 'created_at'),
    updated_at: requireString(response, 'updated_at'),
    provenance: requireProvenanceMap(response, 'provenance'),
    name: optionalPersonName(response, 'name'),
    other_names_used: optionalOtherNames(response, 'other_names_used'),
    employer_ids: optionalStringArray(response, 'employer_ids'),
    residence_address: optionalAddress(response, 'residence_address'),
    mailing_address: optionalAddress(response, 'mailing_address'),
    phone: optionalString(response, 'phone'),
    mobile: optionalString(response, 'mobile'),
    email: optionalString(response, 'email'),
    venue: optionalVenue(response, 'venue'),
    credit_counseling: optionalCreditCounseling(response, 'credit_counseling'),
    signed_at: optionalString(response, 'signed_at'),
  });
}

/**
 * The `{"debtors": [...]}` envelope's array, checked per element — the same
 * shape as {@link requireCaseArray}, and for the same reason: a cast would
 * check nothing at runtime.
 */
function requireDebtorArray(response: DecodedResponse, key: string): readonly Debtor[] {
  const value = response.json[key];
  if (!Array.isArray(value)) {
    throw malformedField(response, key, 'Debtor[]');
  }
  return value.map((item, index) => {
    const label = `${key}[${index}]`;
    if (typeof item !== 'object' || item === null || Array.isArray(item)) {
      throw malformedField(response, label, 'object');
    }
    return debtorFromJson({
      statusCode: response.statusCode,
      body: response.body,
      json: item as JsonObject,
      path: label,
    });
  });
}

// ---------------------------------------------------------------------------
// Generic case-entity decoding (issue #249). Mirrors `entity_json` in
// `services/api/src/insolvia_api/core/case_entities.py`: server-stamped
// identity and `provenance` are always present; every body member is absent
// when it holds nothing.
//
// THE IDENTITY ENVELOPE IS CHECKED; THE BODY IS PASSED THROUGH. That is a
// deliberate departure from `debtorFromJson`, which re-types every field. The
// ten collection bodies span some hundred members plus twenty-seven SOFA
// payload shapes, all owned and validated by the server's one parse path —
// re-deriving each per-field check here would be a second hand-written copy of
// that contract, maintained forever, whose only reader today re-serialises the
// record straight back to the same server. The contract tests still pin the
// exact wire literals per collection, so a server rename still fails this
// package first. If the app ever computes on a body field, promote that field
// to a checked read here.
// ---------------------------------------------------------------------------

/** The five members `entity_json` stamps; everything else is the body. */
const ENTITY_IDENTITY_KEYS: readonly string[] = [
  'id',
  'case_id',
  'created_at',
  'updated_at',
  'provenance',
];

/** Decodes a {@link CaseEntity} — `entity_json`'s exact shape. */
function caseEntityFromJson<C extends CaseCollection>(response: DecodedResponse): CaseEntity<C> {
  const body: Record<string, unknown> = {};
  for (const [key, value] of Object.entries(response.json)) {
    if (!ENTITY_IDENTITY_KEYS.includes(key)) {
      body[key] = value;
    }
  }
  return {
    ...body,
    id: requireString(response, 'id'),
    case_id: requireString(response, 'case_id'),
    created_at: requireString(response, 'created_at'),
    updated_at: requireString(response, 'updated_at'),
    provenance: requireProvenanceMap(response, 'provenance'),
  } as CaseEntity<C>;
}

/** The `{"<collection>": [...]}` envelope's array, checked per element. */
function requireCaseEntityArray<C extends CaseCollection>(
  response: DecodedResponse,
  key: C,
): readonly CaseEntity<C>[] {
  const value = response.json[key];
  if (!Array.isArray(value)) {
    throw malformedField(response, key, 'CaseEntity[]');
  }
  return value.map((item, index) => {
    if (typeof item !== 'object' || item === null || Array.isArray(item)) {
      throw malformedField(response, `${key}[${index}]`, 'object');
    }
    return caseEntityFromJson<C>({
      statusCode: response.statusCode,
      body: response.body,
      json: item as JsonObject,
      path: `${key}[${index}]`,
    });
  });
}

/**
 * An array field whose every element must decode as `T` — checked per-element,
 * never cast, the same rule {@link requireCaseArray} follows. Generic because
 * three firm endpoints need it and copying the element-shape check three times
 * is how one of them ends up not doing it.
 */
function requireArrayOf<T>(
  response: DecodedResponse,
  key: string,
  typeName: string,
  decode: (element: DecodedResponse) => T,
): readonly T[] {
  const value = response.json[key];
  if (!Array.isArray(value)) {
    throw malformedField(response, key, `${typeName}[]`);
  }
  return value.map((item, index) => {
    if (typeof item !== 'object' || item === null || Array.isArray(item)) {
      throw malformedField(response, `${key}[${index}]`, 'object');
    }
    return decode({
      statusCode: response.statusCode,
      body: response.body,
      json: item as JsonObject,
      path: response.path === undefined ? `${key}[${index}]` : `${response.path}.${key}[${index}]`,
    });
  });
}

const FIRM_ROLES: readonly FirmRole[] = ['attorney', 'paralegal', 'staff'];
const PERMISSION_LEVELS: readonly PermissionLevel[] = ['hidden', 'view_only', 'add_edit'];
const FIRM_USER_STATUSES = ['active', 'disabled'] as const;
const FIRM_FEATURES = [
  'cases',
  'intake',
  'documents',
  'extraction_review',
  'firm_administration',
] as const;

function requireFirmRole(response: DecodedResponse, key: string): FirmRole {
  const value = response.json[key];
  if (typeof value !== 'string' || !FIRM_ROLES.includes(value as FirmRole)) {
    throw malformedField(response, key, FIRM_ROLES.join(' | '));
  }
  return value as FirmRole;
}

/**
 * The permission map, checked key by key.
 *
 * A FEATURE THIS VERSION DOES NOT KNOW IS DROPPED, and an unknown LEVEL makes
 * the whole map malformed. The asymmetry is deliberate and matches the
 * server's: a new feature added server-side must not break an older client, so
 * an unrecognised key is skipped and reads as `hidden` through
 * {@link permits}' caller — fail closed. An unrecognised LEVEL on a feature we
 * DO know cannot be ranked at all, and guessing would be the one direction
 * that can over-grant.
 */
function requirePermissions(
  response: DecodedResponse,
  key: string,
): Readonly<Record<FirmFeature, PermissionLevel>> {
  const value = response.json[key];
  if (typeof value !== 'object' || value === null || Array.isArray(value)) {
    throw malformedField(response, key, 'object');
  }
  const source = value as Record<string, unknown>;
  const permissions: Partial<Record<FirmFeature, PermissionLevel>> = {};
  for (const feature of FIRM_FEATURES) {
    const level = source[feature];
    if (level === undefined) continue;
    if (typeof level !== 'string' || !PERMISSION_LEVELS.includes(level as PermissionLevel)) {
      throw malformedField(response, `${key}.${feature}`, PERMISSION_LEVELS.join(' | '));
    }
    permissions[feature] = level as PermissionLevel;
  }
  // Anything the server did not send is `hidden`. A client that treated a
  // missing key as permissive would show a button the server refuses.
  for (const feature of FIRM_FEATURES) {
    permissions[feature] ??= 'hidden';
  }
  return permissions as Record<FirmFeature, PermissionLevel>;
}

const FIRM_STATUSES = ['active', 'suspended'] as const;

function firmFromJson(response: DecodedResponse): Firm {
  const status = response.json.status;
  if (typeof status !== 'string' || !(FIRM_STATUSES as readonly string[]).includes(status)) {
    throw malformedField(response, 'status', FIRM_STATUSES.join(' | '));
  }
  return {
    id: requireString(response, 'id'),
    name: requireString(response, 'name'),
    status: status as FirmStatus,
    createdAt: requireString(response, 'createdAt'),
    updatedAt: requireString(response, 'updatedAt'),
  };
}

function firmUserFromJson(response: DecodedResponse): FirmUser {
  const status = response.json.status;
  if (typeof status !== 'string' || !(FIRM_USER_STATUSES as readonly string[]).includes(status)) {
    throw malformedField(response, 'status', FIRM_USER_STATUSES.join(' | '));
  }
  return {
    subject: requireString(response, 'subject'),
    email: requireString(response, 'email'),
    firstName: requireString(response, 'firstName'),
    lastName: requireString(response, 'lastName'),
    displayName: requireString(response, 'displayName'),
    role: requireFirmRole(response, 'role'),
    isAdmin: requireBoolean(response, 'isAdmin'),
    accessAllCases: requireBoolean(response, 'accessAllCases'),
    permissions: requirePermissions(response, 'permissions'),
    status: status as FirmUserStatus,
    createdAt: requireString(response, 'createdAt'),
    updatedAt: requireString(response, 'updatedAt'),
  };
}

/**
 * The `firm` block of `GET /v1/me`, or `undefined` when the caller is in no
 * firm — absent, not null. See {@link FirmMembership}.
 */
function optionalFirmMembership(response: DecodedResponse): FirmMembership | undefined {
  const nested = optionalObject(response, 'firm');
  if (nested === undefined) return undefined;
  return {
    id: requireString(nested, 'id'),
    name: requireString(nested, 'name'),
    role: requireFirmRole(nested, 'role'),
    // `requireString`, not an optional read, even though either half may be
    // the empty string: `''` is a value the server always SENDS, and a
    // response missing the key entirely is a contract break this package
    // exists to catch. That strictness is why the API emits both keys
    // unconditionally rather than sparsely.
    firstName: requireString(nested, 'firstName'),
    lastName: requireString(nested, 'lastName'),
    displayName: requireString(nested, 'displayName'),
    isAdmin: requireBoolean(nested, 'isAdmin'),
    accessAllCases: requireBoolean(nested, 'accessAllCases'),
    permissions: requirePermissions(nested, 'permissions'),
  };
}

/** A boolean field that must be present and must be a boolean. */
function requireBoolean(response: DecodedResponse, key: string): boolean {
  const value = response.json[key];
  if (typeof value !== 'boolean') {
    throw malformedField(response, key, 'boolean');
  }
  return value;
}
