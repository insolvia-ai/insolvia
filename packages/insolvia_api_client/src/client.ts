import { ApiException, ApiValidationException } from './exceptions.ts';
import { waitlistSubmissionToJson } from './models.ts';
import type { HealthStatus, WaitlistConfirmation, WaitlistSubmission } from './models.ts';

/**
 * The subset of the platform `fetch` this client uses. Injectable so tests
 * can stub the transport without a server, and so a host that polyfills
 * `fetch` can hand its own in.
 */
export type FetchLike = (input: string, init?: RequestInit) => Promise<Response>;

/** Options accepted by the {@link InsolviaApiClient} constructor. */
export interface InsolviaApiClientOptions {
  /**
   * Transport override. Defaults to the platform `fetch`, looked up at call
   * time so a client constructed before a polyfill lands still works.
   */
  readonly fetch?: FetchLike | undefined;
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
 * - 400 with per-field messages → {@link ApiValidationException};
 * - any other unexpected status, or an undecodable success body →
 *   {@link ApiException};
 * - transport failures (DNS, refused connection, …) propagate untouched as
 *   whatever `fetch` rejects with.
 *
 * There is no `close()` counterpart to the Dart client's: `fetch` owns no
 * client object the caller has to release.
 */
export class InsolviaApiClient {
  readonly #baseUrl: string;
  readonly #fetch: FetchLike;

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
}

// ---------------------------------------------------------------------------
// Decoding internals — the private methods of the Dart client, as module
// functions. Not exported: nothing outside this file may depend on them.
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
 * `{"error": ..., "fields": {...}}` → {@link ApiValidationException},
 * anything else (including unparseable bodies) → {@link ApiException}.
 */
function errorFor(statusCode: number, body: string): ApiException {
  const parsed = parseJsonBody(body);
  if (parsed.kind === 'object') {
    const fields = parsed.value.fields;
    if (typeof fields === 'object' && fields !== null && !Array.isArray(fields)) {
      return new ApiValidationException({
        statusCode,
        body,
        fields: toStringMap(fields as JsonObject),
      });
    }
    const error = parsed.value.error;
    const message = parsed.value.message;
    if (typeof error === 'string') {
      return new ApiException({
        statusCode,
        body,
        message: typeof message === 'string' ? `${error}: ${message}` : error,
      });
    }
  }
  return new ApiException({ statusCode, body });
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
 * `undefined` typed as `string` — the trap a bare cast would set here, and
 * the one place TypeScript is weaker than the Dart original's `as String`.
 */
function requireString(response: DecodedResponse, key: string): string {
  const value = response.json[key];
  if (typeof value !== 'string') {
    throw new ApiException({
      statusCode: response.statusCode,
      body: response.body,
      message: `response body was missing the string field "${key}"`,
    });
  }
  return value;
}
