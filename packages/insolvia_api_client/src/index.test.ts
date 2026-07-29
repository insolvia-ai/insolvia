// These tests ARE the contract pin.
//
// There is no OpenAPI spec and no codegen (see README.md): the JSON shapes
// asserted here — request paths, methods, field names, status codes, and
// error bodies — are the authoritative record of what `services/api`
// actually speaks. If the API's contract changes, these tests must fail;
// keep every literal in sync with:
//   services/api/src/insolvia_api/api/routes/{health,waitlist}.py
//   services/api/src/insolvia_api/api/app_factory.py (error handlers)
//   services/api/src/insolvia_api/core/waitlist.py
//
// This is the TypeScript half of a pair: `test/insolvia_api_client_test.dart`
// pins the same contract for the Dart client, assertion for assertion, so the
// two can be read side by side until the Dart half is deleted. The transport
// is a stubbed `fetch` — no network, no test server — the analogue of the
// Dart suite's `MockClient`.
//
// The client is imported by package name rather than by relative path: that
// is the specifier the export map in package.json publishes, so the test
// exercises what a consumer actually resolves.
import { describe, expect, test } from 'vitest';

import {
  ApiException,
  ApiValidationException,
  InsolviaApiClient,
  submittedAtUtc,
} from '@insolvia-ai/api-client';
import type { FetchLike, WaitlistSubmission } from '@insolvia-ai/api-client';

/** What the stub captured about a request, flattened for assertions. */
interface SeenRequest {
  readonly method: string;
  readonly url: string;
  readonly headers: Headers;
  readonly body: string;
}

/**
 * A `fetch` stub that records what it was called with. `lastRequest()`
 * throws rather than handing back `undefined`, so assertions never need a
 * non-null assertion to reach the captured request.
 */
function stubFetch(respond: (seen: SeenRequest) => Response): {
  fetch: FetchLike;
  lastRequest: () => SeenRequest;
} {
  const requests: SeenRequest[] = [];
  return {
    fetch: (input, init) => {
      const seen: SeenRequest = {
        method: init?.method ?? 'GET',
        url: input,
        headers: new Headers(init?.headers),
        body: typeof init?.body === 'string' ? init.body : '',
      };
      requests.push(seen);
      return Promise.resolve(respond(seen));
    },
    lastRequest: () => {
      const seen = requests.at(-1);
      if (seen === undefined) {
        throw new Error('the client made no request');
      }
      return seen;
    },
  };
}

function jsonResponse(body: unknown, status: number): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  });
}

/** Resolves to whatever `call` rejected with; fails the test if it resolves. */
async function rejection(call: Promise<unknown>): Promise<unknown> {
  try {
    await call;
  } catch (error) {
    return error;
  }
  throw new Error('expected the call to reject, but it resolved');
}

function asApiException(error: unknown): ApiException {
  if (error instanceof ApiException) {
    return error;
  }
  throw new Error(`expected an ApiException, got: ${String(error)}`);
}

function asApiValidationException(error: unknown): ApiValidationException {
  if (error instanceof ApiValidationException) {
    return error;
  }
  throw new Error(`expected an ApiValidationException, got: ${String(error)}`);
}

describe('health', () => {
  test('GETs /health and maps the four contract fields', async () => {
    const stub = stubFetch(() =>
      jsonResponse(
        {
          status: 'ok',
          service: 'insolvia-api',
          version: '0.1.0',
          environment: 'staging',
        },
        200,
      ),
    );
    const client = new InsolviaApiClient('http://localhost:8080', { fetch: stub.fetch });

    const status = await client.health();

    const seen = stub.lastRequest();
    expect(seen.method).toBe('GET');
    expect(seen.url).toBe('http://localhost:8080/health');
    expect(status.status).toBe('ok');
    expect(status.service).toBe('insolvia-api');
    expect(status.version).toBe('0.1.0');
    expect(status.environment).toBe('staging');
  });

  test('a trailing slash on baseUrl does not double the path', async () => {
    const stub = stubFetch(() =>
      jsonResponse(
        {
          status: 'ok',
          service: 'insolvia-api',
          version: '0.1.0',
          environment: 'local',
        },
        200,
      ),
    );
    const client = new InsolviaApiClient('http://localhost:8080/', { fetch: stub.fetch });

    await client.health();

    expect(new URL(stub.lastRequest().url).pathname).toBe('/health');
  });
});

describe('joinWaitlist', () => {
  const submission: WaitlistSubmission = {
    name: 'Ada Lovelace',
    firm: 'Lovelace Law LLC',
    email: 'ada@lovelace.law',
  };

  test('POSTs /v1/waitlist as JSON and maps the 201 confirmation', async () => {
    const stub = stubFetch(() =>
      jsonResponse(
        {
          id: '0b1e9a4e-8c1f-4a7e-9c39-b1c5b7d9f2a1',
          submittedAt: '2026-07-23T09:15:00.123Z',
        },
        201,
      ),
    );
    const client = new InsolviaApiClient('https://staging-api.insolvia.ai', {
      fetch: stub.fetch,
    });

    const confirmation = await client.joinWaitlist(submission);

    const seen = stub.lastRequest();
    expect(seen.method).toBe('POST');
    expect(seen.url).toBe('https://staging-api.insolvia.ai/v1/waitlist');
    expect(seen.headers.get('content-type')).toMatch(/^application\/json/);
    const body = JSON.parse(seen.body) as Record<string, unknown>;
    expect(body).toEqual({
      name: 'Ada Lovelace',
      firm: 'Lovelace Law LLC',
      email: 'ada@lovelace.law',
    });
    // Optional fields are omitted when absent, not sent as null/"".
    expect('currentSoftware' in body).toBe(false);
    expect('message' in body).toBe(false);
    expect('host' in body).toBe(false);

    expect(confirmation.id).toBe('0b1e9a4e-8c1f-4a7e-9c39-b1c5b7d9f2a1');
    expect(confirmation.submittedAt).toBe('2026-07-23T09:15:00.123Z');
    // `Date.UTC`'s month is zero-based: 6 is July.
    expect(submittedAtUtc(confirmation).getTime()).toBe(Date.UTC(2026, 6, 23, 9, 15, 0, 123));
  });

  test('an explicit undefined optional field is omitted, not sent as null', async () => {
    // A TypeScript-only hazard with no Dart counterpart, and the reason the
    // optional fields are typed `string | undefined`: callers hand this
    // client `string | undefined` values straight out of a form. Absent and
    // explicitly-undefined must produce the same wire body.
    const stub = stubFetch(() =>
      jsonResponse({ id: 'x', submittedAt: '2026-07-23T00:00:00.000Z' }, 201),
    );
    const client = new InsolviaApiClient('http://localhost:8080', { fetch: stub.fetch });

    await client.joinWaitlist({
      name: 'Ada Lovelace',
      firm: 'Lovelace Law LLC',
      email: 'ada@lovelace.law',
      currentSoftware: undefined,
      message: undefined,
      host: undefined,
    });

    const body = JSON.parse(stub.lastRequest().body) as Record<string, unknown>;
    expect(body).toEqual({
      name: 'Ada Lovelace',
      firm: 'Lovelace Law LLC',
      email: 'ada@lovelace.law',
    });
    expect('currentSoftware' in body).toBe(false);
    expect('message' in body).toBe(false);
    expect('host' in body).toBe(false);
  });

  test('sends optional fields under their exact wire names when set', async () => {
    const stub = stubFetch(() =>
      jsonResponse({ id: 'x', submittedAt: '2026-07-23T00:00:00.000Z' }, 201),
    );
    const client = new InsolviaApiClient('http://localhost:8080', { fetch: stub.fetch });

    await client.joinWaitlist({
      name: 'Ada Lovelace',
      firm: 'Lovelace Law LLC',
      email: 'ada@lovelace.law',
      currentSoftware: 'Best Case',
      message: 'Interested in the desktop app.',
      host: 'www.insolvia.ai',
    });

    const sentBody = JSON.parse(stub.lastRequest().body) as Record<string, unknown>;
    expect(sentBody.currentSoftware).toBe('Best Case');
    expect(sentBody.message).toBe('Interested in the desktop app.');
    expect(sentBody.host).toBe('www.insolvia.ai');
  });

  test('maps a 400 {"error","fields"} body to ApiValidationException carrying the per-field messages verbatim', async () => {
    // Exact shape produced by app_factory's FieldValidationError handler.
    const stub = stubFetch(() =>
      jsonResponse(
        {
          error: 'ValidationError',
          fields: {
            name: 'Please tell us your name.',
            email: "That doesn't look like a valid email address.",
          },
        },
        400,
      ),
    );
    const client = new InsolviaApiClient('http://localhost:8080', { fetch: stub.fetch });

    const error = asApiValidationException(await rejection(client.joinWaitlist(submission)));

    expect(error.statusCode).toBe(400);
    expect(error.fields).toEqual({
      name: 'Please tell us your name.',
      email: "That doesn't look like a valid email address.",
    });
  });

  test('a 400 without "fields" is a plain ApiException, not a validation one', async () => {
    // Shape produced by the non-field ValidationError handler.
    const stub = stubFetch(() =>
      jsonResponse(
        { error: 'ValidationError', message: 'request body must be a JSON object' },
        400,
      ),
    );
    const client = new InsolviaApiClient('http://localhost:8080', { fetch: stub.fetch });

    const error = asApiException(await rejection(client.joinWaitlist(submission)));

    expect(error.statusCode).toBe(400);
    expect(error).not.toBeInstanceOf(ApiValidationException);
  });

  test('maps a 500 to ApiException with the status and raw body', async () => {
    // Exact shape produced by the catch-all Exception handler.
    const body = JSON.stringify({ error: 'InternalError', message: 'request failed' });
    const stub = stubFetch(() => new Response(body, { status: 500 }));
    const client = new InsolviaApiClient('http://localhost:8080', { fetch: stub.fetch });

    const error = asApiException(await rejection(client.joinWaitlist(submission)));

    expect(error.statusCode).toBe(500);
    expect(error.body).toBe(body);
    expect(error.message).toContain('InternalError');
  });

  test('a success status with a malformed JSON body throws ApiException', async () => {
    const stub = stubFetch(() => new Response('<html>gateway timeout</html>', { status: 201 }));
    const client = new InsolviaApiClient('http://localhost:8080', { fetch: stub.fetch });

    const error = asApiException(await rejection(client.joinWaitlist(submission)));

    expect(error.message).toContain('not valid JSON');
  });

  test('a non-2xx with an unparseable body still throws ApiException', async () => {
    const stub = stubFetch(() => new Response('Bad Gateway', { status: 502 }));
    const client = new InsolviaApiClient('http://localhost:8080', { fetch: stub.fetch });

    const error = asApiException(await rejection(client.joinWaitlist(submission)));

    expect(error.statusCode).toBe(502);
    expect(error.body).toBe('Bad Gateway');
  });

  test('transport failures propagate untouched (no ApiException wrapping)', async () => {
    // `fetch` rejects with a TypeError on a network-level failure — the
    // analogue of the Dart client letting an http.ClientException through.
    const transportFailure = new TypeError('Connection refused');
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: () => Promise.reject(transportFailure),
    });

    const error = await rejection(client.joinWaitlist(submission));

    expect(error).toBe(transportFailure);
    expect(error).not.toBeInstanceOf(ApiException);
  });
});
