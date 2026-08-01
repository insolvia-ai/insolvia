import { ApiException, ApiUnauthorizedException, ApiValidationException } from './exceptions.ts';
import { waitlistSubmissionToJson } from './models.ts';
import type {
  HealthStatus,
  Principal,
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
