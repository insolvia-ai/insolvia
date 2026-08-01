// Typed failures for Insolvia API calls.
//
// Only *API-level* failures are modelled here. Transport failures (DNS,
// refused connection, timeout) propagate untouched as whatever `fetch`
// rejects with (a `TypeError` in Node/undici and in the browser) — the
// caller can distinguish "the API rejected this" from "the network is down"
// by exception type alone.

/** Options accepted by {@link ApiException}. */
export interface ApiExceptionOptions {
  /** The HTTP status code the API responded with. */
  readonly statusCode: number;
  /** The raw (undecoded) response body, for diagnostics. */
  readonly body: string;
  /** Overrides the default `API request failed with status <code>` summary. */
  readonly message?: string | undefined;
}

/**
 * The API answered with an unexpected status or an undecodable body.
 *
 * Carries the raw `statusCode` and `body` so callers (and logs) can see
 * exactly what came back. Subclassed by {@link ApiValidationException} for
 * the one failure shape callers are expected to handle field-by-field.
 */
export class ApiException extends Error {
  /** The HTTP status code the API responded with. */
  readonly statusCode: number;

  /** The raw (undecoded) response body, for diagnostics. */
  readonly body: string;

  constructor(options: ApiExceptionOptions) {
    super(options.message ?? `API request failed with status ${options.statusCode}`);
    this.name = 'ApiException';
    this.statusCode = options.statusCode;
    this.body = options.body;
  }

  override toString(): string {
    return `${this.name}(${this.statusCode}): ${this.message}`;
  }
}

/**
 * Where a 401 came from.
 *
 * - `'client'` — this client refused to send the request at all, because no
 *   access token was available. No network round trip happened.
 * - `'server'` — the API answered 401: the token was missing, expired, or
 *   rejected.
 *
 * The distinction is the app's: a `'server'` 401 is worth one refresh attempt,
 * a `'client'` 401 means there is nothing to refresh — go to sign-in. A union
 * of string literals rather than an `enum`, because `erasableSyntaxOnly` is on
 * (see the root `tsconfig.base.json`).
 */
export type UnauthorizedSource = 'client' | 'server';

/** Options accepted by {@link ApiUnauthorizedException}. */
export interface ApiUnauthorizedExceptionOptions {
  /** The HTTP status code, or `401` when no request was made. */
  readonly statusCode: number;
  /** The raw response body, or `''` when no request was made. */
  readonly body: string;
  /** Which side produced the failure. See {@link UnauthorizedSource}. */
  readonly source: UnauthorizedSource;
  /** Overrides the default summary for this `source`. */
  readonly message?: string | undefined;
}

const UNAUTHORIZED_MESSAGES = {
  client: 'no access token available: the request was not sent',
  server: 'the API rejected the access token',
} as const;

/**
 * The call was not authenticated — either the API answered 401, or this
 * client had no access token to send and refused to make the request.
 *
 * Callers translate this into refresh-or-redirect; {@link source} says which.
 * It extends {@link ApiException}, so a consumer that only catches
 * `ApiException` keeps working unchanged.
 *
 * The access token never appears on this exception — not in `message`, not in
 * `body`, not in any field. Nothing in this package stringifies a token.
 */
export class ApiUnauthorizedException extends ApiException {
  /** Which side produced the failure. See {@link UnauthorizedSource}. */
  readonly source: UnauthorizedSource;

  constructor(options: ApiUnauthorizedExceptionOptions) {
    super({
      statusCode: options.statusCode,
      body: options.body,
      message: options.message ?? UNAUTHORIZED_MESSAGES[options.source],
    });
    this.name = 'ApiUnauthorizedException';
    this.source = options.source;
  }
}

/** Options accepted by {@link ApiValidationException}. */
export interface ApiValidationExceptionOptions {
  readonly statusCode: number;
  readonly body: string;
  /** Per-field messages keyed by the request's JSON field names. */
  readonly fields: Record<string, string>;
}

/**
 * A 400 `{"error": "ValidationError", "fields": {...}}` response — per-field
 * messages keyed by the request's JSON field names, exactly as the API sent
 * them, so a form can surface each message next to its input.
 */
export class ApiValidationException extends ApiException {
  /**
   * Per-field validation messages, keyed by JSON field name
   * (e.g. `name`, `firm`, `email`).
   */
  readonly fields: Record<string, string>;

  constructor(options: ApiValidationExceptionOptions) {
    super({
      statusCode: options.statusCode,
      body: options.body,
      message: `validation failed: ${Object.keys(options.fields).sort().join(', ')}`,
    });
    this.name = 'ApiValidationException';
    this.fields = options.fields;
  }
}
