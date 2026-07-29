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
