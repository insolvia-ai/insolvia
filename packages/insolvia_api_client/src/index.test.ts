// These tests ARE the contract pin.
//
// There is no OpenAPI spec and no codegen (see README.md): the JSON shapes
// asserted here — request paths, methods, field names, status codes, and
// error bodies — are the authoritative record of what `services/api`
// actually speaks. If the API's contract changes, these tests must fail;
// keep every literal in sync with:
//   services/api/src/insolvia_api/api/routes/{health,waitlist,me,cases,documents}.py
//   services/api/src/insolvia_api/api/app_factory.py (error handlers)
//   services/api/src/insolvia_api/api/auth.py (UNAUTHORIZED_BODY, the 401)
//   services/api/src/insolvia_api/core/{waitlist,auth,documents}.py
//
// No real credentials appear here — this repo is public. The access token is
// the literal string `test-access-token`; the client treats it as opaque and
// never parses it, so a real JWT would buy nothing but risk.
//
// The transport is a stubbed `fetch` — no network, no test server — so every
// assertion here is about the bytes this client sends and the objects it
// builds from the bytes it is given.
//
// The client is imported by package name rather than by relative path: that
// is the specifier the export map in package.json publishes, so the test
// exercises what a consumer actually resolves.
import { describe, expect, test } from 'vitest';

import {
  ApiException,
  ApiUnauthorizedException,
  ApiValidationException,
  DOCUMENT_CONTENT_TYPES,
  DOCUMENT_KINDS,
  DOCUMENT_STATUSES,
  InsolviaApiClient,
  MAX_DOCUMENT_BYTE_SIZE,
  isDocumentContentType,
  isDocumentKind,
  isUploadIncomplete,
  submittedAtUtc,
} from '@insolvia-ai/api-client';
import type { FetchLike, WaitlistSubmission } from '@insolvia-ai/api-client';

/**
 * An obviously-fake stand-in for a Cognito access token. This repo is public:
 * nothing here may resemble a real credential, and a real JWT is not needed —
 * the client treats the token as an opaque string and never parses it.
 */
const ACCESS_TOKEN = 'test-access-token';

/** What the stub captured about a request, flattened for assertions. */
interface SeenRequest {
  readonly method: string;
  readonly url: string;
  readonly headers: Headers;
  readonly body: string;
  /**
   * The body exactly as it was handed to `fetch`, undecoded. `body` above
   * flattens a non-string to `''`, which is right for every JSON endpoint and
   * useless for the presigned upload, whose body is the `Blob` itself.
   */
  readonly rawBody: RequestInit['body'];
}

/**
 * A `fetch` stub that records what it was called with. `lastRequest()`
 * throws rather than handing back `undefined`, so assertions never need a
 * non-null assertion to reach the captured request.
 *
 * `callCount()` exists for the assertion that matters most on the protected
 * path: that a call with no token never reaches the transport at all.
 *
 * `requests()` exposes the whole sequence, for the multi-request upload
 * sequence where the ORDER is part of the contract.
 */
function stubFetch(respond: (seen: SeenRequest) => Response): {
  fetch: FetchLike;
  lastRequest: () => SeenRequest;
  requests: () => readonly SeenRequest[];
  callCount: () => number;
} {
  const requests: SeenRequest[] = [];
  return {
    fetch: (input, init) => {
      const seen: SeenRequest = {
        method: init?.method ?? 'GET',
        url: input,
        headers: new Headers(init?.headers),
        body: typeof init?.body === 'string' ? init.body : '',
        rawBody: init?.body,
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
    requests: () => requests,
    callCount: () => requests.length,
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

function asApiUnauthorizedException(error: unknown): ApiUnauthorizedException {
  if (error instanceof ApiUnauthorizedException) {
    return error;
  }
  throw new Error(`expected an ApiUnauthorizedException, got: ${String(error)}`);
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
    // The reason the optional fields are typed `string | undefined`: callers
    // hand this
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
    // `fetch` rejects with a TypeError on a network-level failure, and it
    // reaches the caller as-is: "the network is down" stays distinguishable
    // from "the API rejected this" by type alone.
    const transportFailure = new TypeError('Connection refused');
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: () => Promise.reject(transportFailure),
    });

    const error = await rejection(client.joinWaitlist(submission));

    expect(error).toBe(transportFailure);
    expect(error).not.toBeInstanceOf(ApiException);
  });
});

describe('me', () => {
  // Pinned against services/api/src/insolvia_api/api/routes/me.py, which
  // returns {"subject", "username", "clientId", "scopes", "expiresAt"} —
  // snake_case, unlike the waitlist endpoints. The path is /v1/me, not /me.
  const IDENTITY = {
    subject: '9f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f',
    username: '1a2b3c4d-5e6f-7a8b-9c0d-1e2f3a4b5c6d',
    clientId: 'test-app-client-id',
    scopes: ['aws.cognito.signin.user.admin'],
    expiresAt: 1785312000,
  };

  test('GETs /v1/me with a bearer token and maps every contract field', async () => {
    const stub = stubFetch(() => jsonResponse(IDENTITY, 200));
    const client = new InsolviaApiClient('https://staging-api.insolvia.ai', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const principal = await client.me();

    const seen = stub.lastRequest();
    expect(seen.method).toBe('GET');
    expect(seen.url).toBe('https://staging-api.insolvia.ai/v1/me');
    expect(seen.headers.get('authorization')).toBe(`Bearer ${ACCESS_TOKEN}`);
    expect(seen.headers.get('accept')).toBe('application/json');
    // A GET carries no body and must not announce a content type.
    expect(seen.body).toBe('');
    expect(seen.headers.has('content-type')).toBe(false);

    expect(principal.subject).toBe('9f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f');
    expect(principal.username).toBe('1a2b3c4d-5e6f-7a8b-9c0d-1e2f3a4b5c6d');
    expect(principal.clientId).toBe('test-app-client-id');
    expect(principal.scopes).toEqual(['aws.cognito.signin.user.admin']);
    expect(principal.expiresAt).toBe(1785312000);
  });

  test('a trailing slash on baseUrl does not double the path', async () => {
    const stub = stubFetch(() => jsonResponse(IDENTITY, 200));
    const client = new InsolviaApiClient('http://localhost:8080/', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    await client.me();

    expect(new URL(stub.lastRequest().url).pathname).toBe('/v1/me');
  });

  test('null username and expiresAt survive as null, not undefined', async () => {
    // Principal.username and .expiresAt are `str | None` / `int | None`
    // server-side, so jsonify really can emit null. The model mirrors the
    // wire rather than smoothing null into undefined.
    const stub = stubFetch(() =>
      jsonResponse({ ...IDENTITY, username: null, expiresAt: null }, 200),
    );
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const principal = await client.me();

    expect(principal.username).toBeNull();
    expect(principal.expiresAt).toBeNull();
  });

  test('an empty scopes array is preserved', async () => {
    const stub = stubFetch(() => jsonResponse({ ...IDENTITY, scopes: [] }, 200));
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    expect((await client.me()).scopes).toEqual([]);
  });

  test('awaits an async token provider', async () => {
    // Native secure storage is async; an in-memory store is not. Both work,
    // because the client awaits either.
    const stub = stubFetch(() => jsonResponse(IDENTITY, 200));
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => Promise.resolve(ACCESS_TOKEN),
    });

    await client.me();

    expect(stub.lastRequest().headers.get('authorization')).toBe(`Bearer ${ACCESS_TOKEN}`);
  });

  test('consults the provider on every call, so a refreshed token is picked up', async () => {
    const tokens = ['test-access-token-first', 'test-access-token-second'];
    const stub = stubFetch(() => jsonResponse(IDENTITY, 200));
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => tokens.shift(),
    });

    await client.me();
    expect(stub.lastRequest().headers.get('authorization')).toBe('Bearer test-access-token-first');

    await client.me();
    expect(stub.lastRequest().headers.get('authorization')).toBe('Bearer test-access-token-second');
  });

  test('a provider returning undefined throws ApiUnauthorizedException WITHOUT calling fetch', async () => {
    // The assertion that matters: no round trip. A request certain to be
    // rejected is not worth making, and the app gets the same exception type
    // it would get from a server 401.
    const stub = stubFetch(() => jsonResponse(IDENTITY, 200));
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => undefined,
    });

    const error = asApiUnauthorizedException(await rejection(client.me()));

    expect(stub.callCount()).toBe(0);
    expect(error.source).toBe('client');
    expect(error.body).toBe('');
    expect(error.message).not.toContain(ACCESS_TOKEN);
  });

  test('an async provider resolving to undefined also throws without calling fetch', async () => {
    const stub = stubFetch(() => jsonResponse(IDENTITY, 200));
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => Promise.resolve(undefined),
    });

    const error = asApiUnauthorizedException(await rejection(client.me()));

    expect(stub.callCount()).toBe(0);
    expect(error.source).toBe('client');
  });

  test('an empty or blank token is treated as no token at all', async () => {
    const stub = stubFetch(() => jsonResponse(IDENTITY, 200));
    for (const blank of ['', '   ']) {
      const client = new InsolviaApiClient('http://localhost:8080', {
        fetch: stub.fetch,
        accessToken: () => blank,
      });

      expect(asApiUnauthorizedException(await rejection(client.me())).source).toBe('client');
    }
    expect(stub.callCount()).toBe(0);
  });

  test('no provider configured at all throws ApiUnauthorizedException without calling fetch', async () => {
    const stub = stubFetch(() => jsonResponse(IDENTITY, 200));
    const client = new InsolviaApiClient('http://localhost:8080', { fetch: stub.fetch });

    const error = asApiUnauthorizedException(await rejection(client.me()));

    expect(stub.callCount()).toBe(0);
    expect(error.source).toBe('client');
  });

  test('a server 401 maps to ApiUnauthorizedException carrying the status and raw body', async () => {
    // The exact single body every 401 gets — api/auth.py's UNAUTHORIZED_BODY.
    // The API deliberately never says which check failed.
    const body = JSON.stringify({ error: 'Unauthorized', message: 'authentication required' });
    const stub = stubFetch(() => new Response(body, { status: 401 }));
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const error = asApiUnauthorizedException(await rejection(client.me()));

    expect(stub.callCount()).toBe(1);
    expect(error.statusCode).toBe(401);
    expect(error.body).toBe(body);
    expect(error.source).toBe('server');
    expect(error.message).toContain('Unauthorized');
    expect(error.message).toContain('authentication required');
    // A token must never reach a message, and messages get logged.
    expect(error.message).not.toContain(ACCESS_TOKEN);
  });

  test('a 401 is still an ApiException, so existing catch blocks keep working', async () => {
    const stub = stubFetch(() => jsonResponse({ error: 'Unauthorized' }, 401));
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const error = await rejection(client.me());

    expect(error).toBeInstanceOf(ApiException);
    expect(error).toBeInstanceOf(ApiUnauthorizedException);
    expect(error).not.toBeInstanceOf(ApiValidationException);
  });

  test('a 401 with an unparseable body is still ApiUnauthorizedException', async () => {
    // An ALB or API Gateway can answer 401 with HTML the app never sees the
    // shape of. It still has to reach the refresh-or-redirect path.
    const stub = stubFetch(() => new Response('<html>401</html>', { status: 401 }));
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const error = asApiUnauthorizedException(await rejection(client.me()));

    expect(error.statusCode).toBe(401);
    expect(error.body).toBe('<html>401</html>');
  });

  test('status beats body shape: a 401 carrying "fields" is NOT a validation error', async () => {
    // Precedence, decided deliberately. Were the body to win, the app would
    // render field errors under a form instead of re-authenticating.
    const stub = stubFetch(() =>
      jsonResponse({ error: 'Unauthorized', fields: { token: 'expired' } }, 401),
    );
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const error = asApiUnauthorizedException(await rejection(client.me()));

    expect(error).not.toBeInstanceOf(ApiValidationException);
    expect(error.source).toBe('server');
  });

  test('a non-401 failure on a protected call is a plain ApiException', async () => {
    // ApiUnauthorizedException must not swallow every protected-call failure.
    const stub = stubFetch(() =>
      jsonResponse({ error: 'InternalError', message: 'request failed' }, 500),
    );
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const error = asApiException(await rejection(client.me()));

    expect(error.statusCode).toBe(500);
    expect(error).not.toBeInstanceOf(ApiUnauthorizedException);
  });

  test('a 200 missing a contract field throws ApiException naming the field', async () => {
    const stub = stubFetch(() => {
      const { clientId: _omitted, ...withoutClientId } = IDENTITY;
      return jsonResponse(withoutClientId, 200);
    });
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const error = asApiException(await rejection(client.me()));

    expect(error.message).toContain('clientId');
  });

  test('a scopes array holding a non-string is rejected, not cast', async () => {
    const stub = stubFetch(() => jsonResponse({ ...IDENTITY, scopes: ['ok', 7] }, 200));
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    expect(asApiException(await rejection(client.me())).message).toContain('scopes');
  });

  test('a provider that throws propagates untouched', async () => {
    // Secure storage can fail. That is not a 401 — the caller must see the
    // real cause rather than a fabricated auth failure.
    const storageFailure = new Error('secure storage unavailable');
    const stub = stubFetch(() => jsonResponse(IDENTITY, 200));
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => {
        throw storageFailure;
      },
    });

    const error = await rejection(client.me());

    expect(error).toBe(storageFailure);
    expect(error).not.toBeInstanceOf(ApiException);
    expect(stub.callCount()).toBe(0);
  });
});

describe('createCase', () => {
  // Pinned against the shape this package's contract requires of
  // `POST /v1/cases`: request `{"chapter", "district"}`, 201 response is a
  // full Case.
  const CASE = {
    id: 'a3f1e9d0-4b2c-4d1e-9a7f-6c8e0d1f2a3b',
    chapter: 7,
    district: 'D. Del.',
    status: 'intake',
    createdAt: '2026-07-23T09:15:00.123Z',
    updatedAt: '2026-07-23T09:15:00.123Z',
  };

  test('POSTs /v1/cases with a bearer token and the request body, and maps the 201 Case', async () => {
    const stub = stubFetch(() => jsonResponse(CASE, 201));
    const client = new InsolviaApiClient('https://staging-api.insolvia.ai', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const created = await client.createCase({ chapter: 7, district: 'D. Del.' });

    const seen = stub.lastRequest();
    expect(seen.method).toBe('POST');
    expect(seen.url).toBe('https://staging-api.insolvia.ai/v1/cases');
    expect(seen.headers.get('authorization')).toBe(`Bearer ${ACCESS_TOKEN}`);
    expect(seen.headers.get('content-type')).toMatch(/^application\/json/);
    expect(JSON.parse(seen.body)).toEqual({ chapter: 7, district: 'D. Del.' });

    expect(created).toEqual({
      id: 'a3f1e9d0-4b2c-4d1e-9a7f-6c8e0d1f2a3b',
      chapter: 7,
      district: 'D. Del.',
      status: 'intake',
      createdAt: '2026-07-23T09:15:00.123Z',
      updatedAt: '2026-07-23T09:15:00.123Z',
    });
  });

  test('maps a 400 {"error","fields"} body to ApiValidationException', async () => {
    const stub = stubFetch(() =>
      jsonResponse(
        { error: 'ValidationError', fields: { chapter: 'must be one of 7, 11, 12, 13' } },
        400,
      ),
    );
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const error = asApiValidationException(
      await rejection(client.createCase({ chapter: 7, district: 'D. Del.' })),
    );

    expect(error.statusCode).toBe(400);
    expect(error.fields).toEqual({ chapter: 'must be one of 7, 11, 12, 13' });
  });

  test('maps a 400 {"error","message"} body (no "fields") to a plain ApiException', async () => {
    const stub = stubFetch(() =>
      jsonResponse(
        { error: 'ValidationError', message: 'request body must be a JSON object' },
        400,
      ),
    );
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const error = asApiException(
      await rejection(client.createCase({ chapter: 7, district: 'D. Del.' })),
    );

    expect(error.statusCode).toBe(400);
    expect(error).not.toBeInstanceOf(ApiValidationException);
  });

  test('a server 401 maps to ApiUnauthorizedException', async () => {
    const stub = stubFetch(() =>
      jsonResponse({ error: 'Unauthorized', message: 'authentication required' }, 401),
    );
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const error = asApiUnauthorizedException(
      await rejection(client.createCase({ chapter: 7, district: 'D. Del.' })),
    );

    expect(error.statusCode).toBe(401);
    expect(error.source).toBe('server');
  });

  test('no access token throws ApiUnauthorizedException without calling fetch', async () => {
    const stub = stubFetch(() => jsonResponse(CASE, 201));
    const client = new InsolviaApiClient('http://localhost:8080', { fetch: stub.fetch });

    const error = asApiUnauthorizedException(
      await rejection(client.createCase({ chapter: 7, district: 'D. Del.' })),
    );

    expect(stub.callCount()).toBe(0);
    expect(error.source).toBe('client');
  });
});

describe('listCases', () => {
  const CASE_A = {
    id: 'a3f1e9d0-4b2c-4d1e-9a7f-6c8e0d1f2a3b',
    chapter: 7,
    district: 'D. Del.',
    status: 'intake',
    createdAt: '2026-07-23T09:15:00.123Z',
    updatedAt: '2026-07-23T09:15:00.123Z',
  };
  const CASE_B = {
    id: 'b4a2f0e1-5c3d-4e2f-8b6a-7d9f1e2a3b4c',
    chapter: 13,
    district: 'N.D. Ga.',
    status: 'ready_to_file',
    createdAt: '2026-07-24T09:15:00.123Z',
    updatedAt: '2026-07-25T09:15:00.123Z',
  };

  test('GETs /v1/cases with no query string when limit/cursor are both omitted', async () => {
    const stub = stubFetch(() => jsonResponse({ cases: [CASE_A, CASE_B] }, 200));
    const client = new InsolviaApiClient('https://staging-api.insolvia.ai', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const result = await client.listCases();

    const seen = stub.lastRequest();
    expect(seen.method).toBe('GET');
    expect(seen.url).toBe('https://staging-api.insolvia.ai/v1/cases');
    expect(seen.headers.get('authorization')).toBe(`Bearer ${ACCESS_TOKEN}`);
    expect(seen.body).toBe('');
    expect(seen.headers.has('content-type')).toBe(false);

    expect(result.cases).toEqual([CASE_A, CASE_B]);
    // Absent, not null, when there is no next page.
    expect('nextCursor' in result).toBe(false);
  });

  test('sends limit and cursor as query params when both are supplied', async () => {
    const stub = stubFetch(() => jsonResponse({ cases: [] }, 200));
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    await client.listCases({ limit: 25, cursor: 'opaque-cursor-value' });

    const url = new URL(stub.lastRequest().url);
    expect(url.pathname).toBe('/v1/cases');
    expect(url.searchParams.get('limit')).toBe('25');
    expect(url.searchParams.get('cursor')).toBe('opaque-cursor-value');
  });

  test('omits only the missing param when just one of limit/cursor is supplied', async () => {
    const stub = stubFetch(() => jsonResponse({ cases: [] }, 200));
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    await client.listCases({ limit: 10 });

    const url = new URL(stub.lastRequest().url);
    expect(url.searchParams.get('limit')).toBe('10');
    expect(url.searchParams.has('cursor')).toBe(false);
  });

  test('maps a present nextCursor through', async () => {
    const stub = stubFetch(() => jsonResponse({ cases: [CASE_A], nextCursor: 'next-page' }, 200));
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const result = await client.listCases();

    expect(result.nextCursor).toBe('next-page');
  });

  test('an empty page is preserved as an empty array', async () => {
    const stub = stubFetch(() => jsonResponse({ cases: [] }, 200));
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    expect((await client.listCases()).cases).toEqual([]);
  });

  test('no access token throws ApiUnauthorizedException without calling fetch', async () => {
    const stub = stubFetch(() => jsonResponse({ cases: [] }, 200));
    const client = new InsolviaApiClient('http://localhost:8080', { fetch: stub.fetch });

    const error = asApiUnauthorizedException(await rejection(client.listCases()));

    expect(stub.callCount()).toBe(0);
    expect(error.source).toBe('client');
  });

  test('a server 401 maps to ApiUnauthorizedException', async () => {
    const stub = stubFetch(() => jsonResponse({ error: 'Unauthorized' }, 401));
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const error = asApiUnauthorizedException(await rejection(client.listCases()));

    expect(error.statusCode).toBe(401);
    expect(error.source).toBe('server');
  });
});

describe('getCase', () => {
  const CASE_ID = 'a3f1e9d0-4b2c-4d1e-9a7f-6c8e0d1f2a3b';
  const CASE = {
    id: CASE_ID,
    chapter: 11,
    district: 'S.D.N.Y.',
    status: 'filed',
    createdAt: '2026-07-23T09:15:00.123Z',
    updatedAt: '2026-07-26T09:15:00.123Z',
  };

  test('GETs /v1/cases/{caseId} with a bearer token and maps the 200 Case', async () => {
    const stub = stubFetch(() => jsonResponse(CASE, 200));
    const client = new InsolviaApiClient('https://staging-api.insolvia.ai', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const found = await client.getCase(CASE_ID);

    const seen = stub.lastRequest();
    expect(seen.method).toBe('GET');
    expect(seen.url).toBe(`https://staging-api.insolvia.ai/v1/cases/${CASE_ID}`);
    expect(seen.headers.get('authorization')).toBe(`Bearer ${ACCESS_TOKEN}`);
    expect(found).toEqual(CASE);
  });

  test('URL-encodes the caseId into the path', async () => {
    const stub = stubFetch(() => jsonResponse(CASE, 200));
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    await client.getCase('id with spaces/slash');

    expect(new URL(stub.lastRequest().url).pathname).toBe('/v1/cases/id%20with%20spaces%2Fslash');
  });

  test('maps a 404 {"error","message"} body to a plain ApiException, not a special "missing" type', async () => {
    // The API returns this same shape whether the case does not exist or
    // exists but belongs to someone else — see getCase's doc comment. This
    // test only pins that a 404 stays a plain ApiException, not that it means
    // "does not exist".
    const stub = stubFetch(() =>
      jsonResponse({ error: 'NotFoundError', message: 'case not found' }, 404),
    );
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const error = asApiException(await rejection(client.getCase(CASE_ID)));

    expect(error.statusCode).toBe(404);
    expect(error).not.toBeInstanceOf(ApiValidationException);
    expect(error).not.toBeInstanceOf(ApiUnauthorizedException);
    expect(error.message).toContain('NotFoundError');
    expect(error.message).toContain('case not found');
  });

  test('no access token throws ApiUnauthorizedException without calling fetch', async () => {
    const stub = stubFetch(() => jsonResponse(CASE, 200));
    const client = new InsolviaApiClient('http://localhost:8080', { fetch: stub.fetch });

    const error = asApiUnauthorizedException(await rejection(client.getCase(CASE_ID)));

    expect(stub.callCount()).toBe(0);
    expect(error.source).toBe('client');
  });
});

describe('updateCase', () => {
  const CASE_ID = 'a3f1e9d0-4b2c-4d1e-9a7f-6c8e0d1f2a3b';
  const UPDATED_CASE = {
    id: CASE_ID,
    chapter: 13,
    district: 'D. Del.',
    status: 'ready_to_file',
    createdAt: '2026-07-23T09:15:00.123Z',
    updatedAt: '2026-07-27T10:00:00.000Z',
  };

  test('PATCHes /v1/cases/{caseId} sending only the supplied fields, and maps the 200 Case', async () => {
    const stub = stubFetch(() => jsonResponse(UPDATED_CASE, 200));
    const client = new InsolviaApiClient('https://staging-api.insolvia.ai', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const updated = await client.updateCase(CASE_ID, { status: 'ready_to_file' });

    const seen = stub.lastRequest();
    expect(seen.method).toBe('PATCH');
    expect(seen.url).toBe(`https://staging-api.insolvia.ai/v1/cases/${CASE_ID}`);
    expect(seen.headers.get('authorization')).toBe(`Bearer ${ACCESS_TOKEN}`);
    expect(seen.headers.get('content-type')).toMatch(/^application\/json/);
    const body = JSON.parse(seen.body) as Record<string, unknown>;
    expect(body).toEqual({ status: 'ready_to_file' });
    expect('chapter' in body).toBe(false);
    expect('district' in body).toBe(false);

    expect(updated).toEqual(UPDATED_CASE);
  });

  test('sends multiple supplied fields together, under their exact wire names', async () => {
    const stub = stubFetch(() => jsonResponse(UPDATED_CASE, 200));
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    await client.updateCase(CASE_ID, { chapter: 13, district: 'D. Del.' });

    const body = JSON.parse(stub.lastRequest().body) as Record<string, unknown>;
    expect(body).toEqual({ chapter: 13, district: 'D. Del.' });
    expect('status' in body).toBe(false);
  });

  test('an explicit undefined field is omitted, not sent as null', async () => {
    const stub = stubFetch(() => jsonResponse(UPDATED_CASE, 200));
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    await client.updateCase(CASE_ID, {
      status: 'ready_to_file',
      chapter: undefined,
      district: undefined,
    });

    const body = JSON.parse(stub.lastRequest().body) as Record<string, unknown>;
    expect(body).toEqual({ status: 'ready_to_file' });
  });

  test('maps a 400 {"error","fields"} body to ApiValidationException', async () => {
    const stub = stubFetch(() =>
      jsonResponse(
        {
          error: 'ValidationError',
          fields: { status: 'must be one of intake, ready_to_file, filed' },
        },
        400,
      ),
    );
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const error = asApiValidationException(
      await rejection(client.updateCase(CASE_ID, { status: 'filed' })),
    );

    expect(error.statusCode).toBe(400);
    expect(error.fields).toEqual({ status: 'must be one of intake, ready_to_file, filed' });
  });

  test('maps a 404 {"error","message"} body to a plain ApiException', async () => {
    const stub = stubFetch(() =>
      jsonResponse({ error: 'NotFoundError', message: 'case not found' }, 404),
    );
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const error = asApiException(await rejection(client.updateCase(CASE_ID, { status: 'filed' })));

    expect(error.statusCode).toBe(404);
    expect(error).not.toBeInstanceOf(ApiValidationException);
  });

  test('no access token throws ApiUnauthorizedException without calling fetch', async () => {
    const stub = stubFetch(() => jsonResponse(UPDATED_CASE, 200));
    const client = new InsolviaApiClient('http://localhost:8080', { fetch: stub.fetch });

    const error = asApiUnauthorizedException(
      await rejection(client.updateCase(CASE_ID, { status: 'filed' })),
    );

    expect(stub.callCount()).toBe(0);
    expect(error.source).toBe('client');
  });
});

// ---------------------------------------------------------------------------
// Documents. Pinned against services/api/.../routes/documents.py and
// core/documents.py: `document_json`'s eight fields, the `upload` block on the
// 201, the 204 on delete, and the 409 that means the bytes never arrived.
// ---------------------------------------------------------------------------

const DOC_CASE_ID = 'a3f1e9d0-4b2c-4d1e-9a7f-6c8e0d1f2a3b';
const DOCUMENT_ID = 'c5d3a1b2-6e4f-4a3b-9c8d-2e1f0a9b8c7d';

/** A pending record: the bytes are not known to have landed. */
const PENDING_DOCUMENT = {
  id: DOCUMENT_ID,
  caseId: DOC_CASE_ID,
  kind: 'bank_statement',
  fileName: 'statement-june.pdf',
  contentType: 'application/pdf',
  byteSize: 24,
  uploadedAt: '2026-07-28T11:02:03.456Z',
  status: 'pending',
};

/** The same record after completion: `stored`, with the size S3 counted. */
const STORED_DOCUMENT = { ...PENDING_DOCUMENT, status: 'stored', byteSize: 25 };

/**
 * The `upload` block. The URL is an obvious fake — no real bucket, no real
 * signature; this repo is public and a presigned URL is a bearer capability.
 */
const UPLOAD_BLOCK = {
  url: 'https://documents.example.invalid/cases/x/y?X-Amz-Signature=not-a-real-signature',
  method: 'PUT',
  headers: {
    'Content-Type': 'application/pdf',
    'x-amz-server-side-encryption': 'aws:kms',
    'x-amz-tagging': 'upload=unconfirmed',
  },
  expiresAt: '2026-07-28T11:17:03.456Z',
};

/** The bytes. `Blob.size` is what the client must declare — see uploadDocument. */
const FILE = new Blob(['%PDF-1.7 pretend bytes'], { type: 'application/pdf' });

function headerMap(headers: Headers): Record<string, string> {
  return Object.fromEntries(headers.entries());
}

describe('document model constants', () => {
  // The types are DERIVED from these arrays, so the arrays are the contract:
  // they are what a picker renders, and what the API's own allowlists say.
  test('kinds, content types and statuses match the API allowlists verbatim', () => {
    expect(DOCUMENT_KINDS).toEqual([
      'credit_report',
      'pay_stub',
      'bank_statement',
      'tax_return',
      'identification',
      'court_notice',
      'other',
    ]);
    expect(DOCUMENT_CONTENT_TYPES).toEqual([
      'application/pdf',
      'image/jpeg',
      'image/png',
      'image/heic',
      'image/tiff',
    ]);
    expect(DOCUMENT_STATUSES).toEqual(['pending', 'stored']);
    expect(MAX_DOCUMENT_BYTE_SIZE).toBe(50 * 1024 * 1024);
  });

  test('the narrowing guards accept members and refuse everything else', () => {
    // The point of the guards: a File.type is a plain string, and the caller
    // has to find out at the picker rather than from a 400.
    expect(isDocumentKind('pay_stub')).toBe(true);
    expect(isDocumentKind('PAY_STUB')).toBe(false);
    expect(isDocumentKind('mortgage')).toBe(false);
    expect(isDocumentContentType('image/heic')).toBe(true);
    // Refused server-side on purpose: script-bearing markup wearing an image
    // content type.
    expect(isDocumentContentType('image/svg+xml')).toBe(false);
    // Media types are case-insensitive, but the API signs the lowercased
    // spelling — sending anything else is an opaque signature failure.
    expect(isDocumentContentType('Application/PDF')).toBe(false);
    expect(isDocumentContentType('')).toBe(false);
  });
});

describe('createDocument', () => {
  test('POSTs /v1/cases/{caseId}/documents and maps the 201 {document, upload}', async () => {
    const stub = stubFetch(() =>
      jsonResponse({ document: PENDING_DOCUMENT, upload: UPLOAD_BLOCK }, 201),
    );
    const client = new InsolviaApiClient('https://staging-api.insolvia.ai', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const created = await client.createDocument(DOC_CASE_ID, {
      kind: 'bank_statement',
      fileName: 'statement-june.pdf',
      contentType: 'application/pdf',
      byteSize: 24,
    });

    const seen = stub.lastRequest();
    expect(seen.method).toBe('POST');
    expect(seen.url).toBe(`https://staging-api.insolvia.ai/v1/cases/${DOC_CASE_ID}/documents`);
    expect(seen.headers.get('authorization')).toBe(`Bearer ${ACCESS_TOKEN}`);
    expect(seen.headers.get('content-type')).toMatch(/^application\/json/);
    // All four fields, under their exact wire names, and nothing else — the
    // API refuses a `provenance` key outright.
    expect(JSON.parse(seen.body)).toEqual({
      kind: 'bank_statement',
      fileName: 'statement-june.pdf',
      contentType: 'application/pdf',
      byteSize: 24,
    });

    expect(created.document).toEqual(PENDING_DOCUMENT);
    expect(created.upload.url).toBe(UPLOAD_BLOCK.url);
    expect(created.upload.method).toBe('PUT');
    expect(created.upload.expiresAt).toBe('2026-07-28T11:17:03.456Z');
  });

  test('passes the upload headers through as an open map, keys and all', async () => {
    // THE REGRESSION THIS GUARDS. The server chooses which headers it signs.
    // A client that enumerated the three it knows would silently drop a fourth
    // added server-side, and every upload would 403 with nothing to explain it.
    const withAnExtraSignedHeader = {
      ...UPLOAD_BLOCK,
      headers: { ...UPLOAD_BLOCK.headers, 'x-amz-checksum-algorithm': 'CRC32' },
    };
    const stub = stubFetch(() =>
      jsonResponse({ document: PENDING_DOCUMENT, upload: withAnExtraSignedHeader }, 201),
    );
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const created = await client.createDocument(DOC_CASE_ID, {
      kind: 'other',
      fileName: 'scan.pdf',
      contentType: 'application/pdf',
      byteSize: 1,
    });

    expect(created.upload.headers).toEqual({
      'Content-Type': 'application/pdf',
      'x-amz-server-side-encryption': 'aws:kms',
      'x-amz-tagging': 'upload=unconfirmed',
      'x-amz-checksum-algorithm': 'CRC32',
    });
  });

  test('a non-string header value is rejected rather than stringified', async () => {
    // `String(undefined)` in a signed header is a 403 from S3 that looks like
    // nothing. Fail here, where the message names the field.
    const stub = stubFetch(() =>
      jsonResponse(
        {
          document: PENDING_DOCUMENT,
          upload: { ...UPLOAD_BLOCK, headers: { 'Content-Type': 7 } },
        },
        201,
      ),
    );
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const error = asApiException(
      await rejection(
        client.createDocument(DOC_CASE_ID, {
          kind: 'other',
          fileName: 'scan.pdf',
          contentType: 'application/pdf',
          byteSize: 1,
        }),
      ),
    );

    expect(error.message).toContain('headers.Content-Type');
  });

  test('maps a 400 {"error","fields"} body to ApiValidationException', async () => {
    const stub = stubFetch(() =>
      jsonResponse(
        {
          error: 'ValidationError',
          fields: { byteSize: 'A document must be 50 MB or smaller.' },
        },
        400,
      ),
    );
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const error = asApiValidationException(
      await rejection(
        client.createDocument(DOC_CASE_ID, {
          kind: 'tax_return',
          fileName: 'return.pdf',
          contentType: 'application/pdf',
          byteSize: MAX_DOCUMENT_BYTE_SIZE + 1,
        }),
      ),
    );

    expect(error.statusCode).toBe(400);
    expect(error.fields).toEqual({ byteSize: 'A document must be 50 MB or smaller.' });
  });

  test('no access token throws ApiUnauthorizedException without calling fetch', async () => {
    const stub = stubFetch(() =>
      jsonResponse({ document: PENDING_DOCUMENT, upload: UPLOAD_BLOCK }, 201),
    );
    const client = new InsolviaApiClient('http://localhost:8080', { fetch: stub.fetch });

    const error = asApiUnauthorizedException(
      await rejection(
        client.createDocument(DOC_CASE_ID, {
          kind: 'other',
          fileName: 'scan.pdf',
          contentType: 'application/pdf',
          byteSize: 1,
        }),
      ),
    );

    expect(stub.callCount()).toBe(0);
    expect(error.source).toBe('client');
  });
});

describe('listDocuments', () => {
  test('GETs /v1/cases/{caseId}/documents and returns the array itself', async () => {
    const stub = stubFetch(() =>
      jsonResponse({ documents: [STORED_DOCUMENT, PENDING_DOCUMENT] }, 200),
    );
    const client = new InsolviaApiClient('https://staging-api.insolvia.ai', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const documents = await client.listDocuments(DOC_CASE_ID);

    const seen = stub.lastRequest();
    expect(seen.method).toBe('GET');
    expect(seen.url).toBe(`https://staging-api.insolvia.ai/v1/cases/${DOC_CASE_ID}/documents`);
    expect(seen.headers.get('authorization')).toBe(`Bearer ${ACCESS_TOKEN}`);
    expect(seen.body).toBe('');
    expect(seen.headers.has('content-type')).toBe(false);

    // Not paginated: no cursor, no result wrapper.
    expect(documents).toEqual([STORED_DOCUMENT, PENDING_DOCUMENT]);
  });

  test('pending documents survive the decode — they are not filtered out', async () => {
    // The API lists them deliberately: a row whose upload never finished is
    // the case's record of a file the user tried to add, and dropping it here
    // would leave them unable to see or retry it.
    const stub = stubFetch(() => jsonResponse({ documents: [PENDING_DOCUMENT] }, 200));
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const documents = await client.listDocuments(DOC_CASE_ID);

    expect(documents).toHaveLength(1);
    expect(documents[0]?.status).toBe('pending');
  });

  test('an empty case is an empty array', async () => {
    const stub = stubFetch(() => jsonResponse({ documents: [] }, 200));
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    expect(await client.listDocuments(DOC_CASE_ID)).toEqual([]);
  });

  test('an unknown status is rejected, not cast', async () => {
    // `status` is the one field a client branches on: an unrecognised value
    // means it cannot tell "you can open this" from "this never uploaded".
    const stub = stubFetch(() =>
      jsonResponse({ documents: [{ ...PENDING_DOCUMENT, status: 'uploading' }] }, 200),
    );
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const error = asApiException(await rejection(client.listDocuments(DOC_CASE_ID)));

    expect(error.message).toContain('status');
    expect(error.message).toContain('"pending" | "stored"');
  });

  test('an unfamiliar kind or content type is NOT rejected', async () => {
    // The deliberate asymmetry with `status`: the request types are unions so
    // a call site cannot invent one, but a server that grows its allowlist
    // must not break a deployed client's whole document list.
    const stub = stubFetch(() =>
      jsonResponse(
        {
          documents: [
            { ...STORED_DOCUMENT, kind: 'mortgage_statement', contentType: 'image/webp' },
          ],
        },
        200,
      ),
    );
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const documents = await client.listDocuments(DOC_CASE_ID);

    expect(documents[0]?.kind).toBe('mortgage_statement');
    expect(documents[0]?.contentType).toBe('image/webp');
  });

  test('a 404 on the case is a plain ApiException', async () => {
    const stub = stubFetch(() =>
      jsonResponse({ error: 'NotFoundError', message: 'case not found' }, 404),
    );
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const error = asApiException(await rejection(client.listDocuments(DOC_CASE_ID)));

    expect(error.statusCode).toBe(404);
    expect(error).not.toBeInstanceOf(ApiValidationException);
  });

  test('no access token throws ApiUnauthorizedException without calling fetch', async () => {
    const stub = stubFetch(() => jsonResponse({ documents: [] }, 200));
    const client = new InsolviaApiClient('http://localhost:8080', { fetch: stub.fetch });

    const error = asApiUnauthorizedException(await rejection(client.listDocuments(DOC_CASE_ID)));

    expect(stub.callCount()).toBe(0);
    expect(error.source).toBe('client');
  });
});

describe('getDocumentUrl', () => {
  const DOWNLOAD = {
    url: 'https://documents.example.invalid/cases/x/y?X-Amz-Signature=not-a-real-signature',
    method: 'GET',
    expiresAt: '2026-07-28T11:07:03.456Z',
  };

  test('GETs /v1/cases/{caseId}/documents/{documentId}/url and maps the three fields', async () => {
    const stub = stubFetch(() => jsonResponse(DOWNLOAD, 200));
    const client = new InsolviaApiClient('https://staging-api.insolvia.ai', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const download = await client.getDocumentUrl(DOC_CASE_ID, DOCUMENT_ID);

    const seen = stub.lastRequest();
    expect(seen.method).toBe('GET');
    expect(seen.url).toBe(
      `https://staging-api.insolvia.ai/v1/cases/${DOC_CASE_ID}/documents/${DOCUMENT_ID}/url`,
    );
    expect(seen.headers.get('authorization')).toBe(`Bearer ${ACCESS_TOKEN}`);
    expect(download).toEqual(DOWNLOAD);
  });

  test('URL-encodes both ids into the path', async () => {
    const stub = stubFetch(() => jsonResponse(DOWNLOAD, 200));
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    await client.getDocumentUrl('case/one', 'doc two');

    expect(new URL(stub.lastRequest().url).pathname).toBe(
      '/v1/cases/case%2Fone/documents/doc%20two/url',
    );
  });

  test('a 404 for an unknown document is a plain ApiException', async () => {
    const stub = stubFetch(() =>
      jsonResponse({ error: 'NotFoundError', message: 'document not found' }, 404),
    );
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const error = asApiException(await rejection(client.getDocumentUrl(DOC_CASE_ID, DOCUMENT_ID)));

    expect(error.statusCode).toBe(404);
    expect(error.message).toContain('document not found');
  });

  test('no access token throws ApiUnauthorizedException without calling fetch', async () => {
    const stub = stubFetch(() => jsonResponse(DOWNLOAD, 200));
    const client = new InsolviaApiClient('http://localhost:8080', { fetch: stub.fetch });

    const error = asApiUnauthorizedException(
      await rejection(client.getDocumentUrl(DOC_CASE_ID, DOCUMENT_ID)),
    );

    expect(stub.callCount()).toBe(0);
    expect(error.source).toBe('client');
  });
});

describe('completeDocument', () => {
  test('POSTs .../complete with no body and maps the 200 {document}', async () => {
    const stub = stubFetch(() => jsonResponse({ document: STORED_DOCUMENT }, 200));
    const client = new InsolviaApiClient('https://staging-api.insolvia.ai', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const confirmed = await client.completeDocument(DOC_CASE_ID, DOCUMENT_ID);

    const seen = stub.lastRequest();
    expect(seen.method).toBe('POST');
    expect(seen.url).toBe(
      `https://staging-api.insolvia.ai/v1/cases/${DOC_CASE_ID}/documents/${DOCUMENT_ID}/complete`,
    );
    expect(seen.headers.get('authorization')).toBe(`Bearer ${ACCESS_TOKEN}`);
    // No request body, so no content type either.
    expect(seen.body).toBe('');
    expect(seen.headers.has('content-type')).toBe(false);

    expect(confirmed.status).toBe('stored');
    // The size is REPLACED by what S3 counted, not the 24 that was declared.
    expect(confirmed.byteSize).toBe(25);
  });

  test('a 409 means the bytes never arrived, and isUploadIncomplete says so', async () => {
    const stub = stubFetch(() =>
      jsonResponse(
        {
          error: 'ConflictError',
          message: 'the object for this document is not in the bucket; the upload did not complete',
        },
        409,
      ),
    );
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const error = asApiException(
      await rejection(client.completeDocument(DOC_CASE_ID, DOCUMENT_ID)),
    );

    expect(error.statusCode).toBe(409);
    expect(isUploadIncomplete(error)).toBe(true);
    // The action is "upload again", not "retry this call", so the message has
    // to survive to the caller.
    expect(error.message).toContain('the upload did not complete');
  });

  test('isUploadIncomplete is false for every other failure', async () => {
    expect(isUploadIncomplete(new ApiException({ statusCode: 404, body: '' }))).toBe(false);
    expect(isUploadIncomplete(new TypeError('Connection refused'))).toBe(false);
    expect(isUploadIncomplete(undefined)).toBe(false);
  });

  test('a 404 (deleted mid-flight) stays a plain ApiException', async () => {
    const stub = stubFetch(() =>
      jsonResponse({ error: 'NotFoundError', message: 'document not found' }, 404),
    );
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const error = asApiException(
      await rejection(client.completeDocument(DOC_CASE_ID, DOCUMENT_ID)),
    );

    expect(error.statusCode).toBe(404);
    expect(isUploadIncomplete(error)).toBe(false);
  });

  test('no access token throws ApiUnauthorizedException without calling fetch', async () => {
    const stub = stubFetch(() => jsonResponse({ document: STORED_DOCUMENT }, 200));
    const client = new InsolviaApiClient('http://localhost:8080', { fetch: stub.fetch });

    const error = asApiUnauthorizedException(
      await rejection(client.completeDocument(DOC_CASE_ID, DOCUMENT_ID)),
    );

    expect(stub.callCount()).toBe(0);
    expect(error.source).toBe('client');
  });
});

describe('deleteDocument', () => {
  test('DELETEs the document and resolves on a 204 with no body', async () => {
    // The assertion that matters: a 204 carries no JSON, and the client must
    // not try to parse one. A decoder that assumed a body would turn every
    // successful delete into "response body was not valid JSON".
    const stub = stubFetch(() => new Response(null, { status: 204 }));
    const client = new InsolviaApiClient('https://staging-api.insolvia.ai', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    await expect(client.deleteDocument(DOC_CASE_ID, DOCUMENT_ID)).resolves.toBeUndefined();

    const seen = stub.lastRequest();
    expect(seen.method).toBe('DELETE');
    expect(seen.url).toBe(
      `https://staging-api.insolvia.ai/v1/cases/${DOC_CASE_ID}/documents/${DOCUMENT_ID}`,
    );
    expect(seen.headers.get('authorization')).toBe(`Bearer ${ACCESS_TOKEN}`);
    expect(seen.body).toBe('');
    expect(seen.headers.has('content-type')).toBe(false);
  });

  test('a 404 still maps to a plain ApiException with the body', async () => {
    // The no-body path must not lose the error path: a failure here has a JSON
    // envelope like every other.
    const body = JSON.stringify({ error: 'NotFoundError', message: 'document not found' });
    const stub = stubFetch(() => new Response(body, { status: 404 }));
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const error = asApiException(await rejection(client.deleteDocument(DOC_CASE_ID, DOCUMENT_ID)));

    expect(error.statusCode).toBe(404);
    expect(error.body).toBe(body);
    expect(error.message).toContain('document not found');
  });

  test('a 401 on the delete is still ApiUnauthorizedException', async () => {
    const stub = stubFetch(() => jsonResponse({ error: 'Unauthorized' }, 401));
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const error = asApiUnauthorizedException(
      await rejection(client.deleteDocument(DOC_CASE_ID, DOCUMENT_ID)),
    );

    expect(error.source).toBe('server');
  });

  test('no access token throws ApiUnauthorizedException without calling fetch', async () => {
    const stub = stubFetch(() => new Response(null, { status: 204 }));
    const client = new InsolviaApiClient('http://localhost:8080', { fetch: stub.fetch });

    const error = asApiUnauthorizedException(
      await rejection(client.deleteDocument(DOC_CASE_ID, DOCUMENT_ID)),
    );

    expect(stub.callCount()).toBe(0);
    expect(error.source).toBe('client');
  });
});

describe('uploadDocument', () => {
  /**
   * The happy-path transport: the API's two calls plus the presigned PUT,
   * dispatched on what the client actually asked for — so a wrong URL or verb
   * fails as an unrecognised request rather than passing quietly.
   */
  function uploadStub(overrides: { readonly putStatus?: number } = {}) {
    return stubFetch((seen) => {
      if (seen.url.startsWith(UPLOAD_BLOCK.url)) {
        return new Response(overrides.putStatus === undefined ? '' : 'AccessDenied', {
          status: overrides.putStatus ?? 200,
        });
      }
      if (seen.url.endsWith('/complete')) {
        return jsonResponse({ document: STORED_DOCUMENT }, 200);
      }
      if (seen.url.endsWith('/documents') && seen.method === 'POST') {
        return jsonResponse({ document: PENDING_DOCUMENT, upload: UPLOAD_BLOCK }, 201);
      }
      return jsonResponse({ error: 'unexpected request', url: seen.url }, 418);
    });
  }

  const OPTIONS = {
    file: FILE,
    fileName: 'statement-june.pdf',
    kind: 'bank_statement',
    contentType: 'application/pdf',
  } as const;

  test('runs create → PUT → complete and returns the confirmed document', async () => {
    const stub = uploadStub();
    const client = new InsolviaApiClient('https://staging-api.insolvia.ai', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const document = await client.uploadDocument(DOC_CASE_ID, OPTIONS);

    const [create, put, complete] = stub.requests();
    expect(stub.callCount()).toBe(3);
    expect(create?.method).toBe('POST');
    expect(create?.url).toBe(`https://staging-api.insolvia.ai/v1/cases/${DOC_CASE_ID}/documents`);
    expect(put?.method).toBe('PUT');
    expect(put?.url).toBe(UPLOAD_BLOCK.url);
    expect(complete?.method).toBe('POST');
    expect(complete?.url).toBe(
      `https://staging-api.insolvia.ai/v1/cases/${DOC_CASE_ID}/documents/${DOCUMENT_ID}/complete`,
    );

    // The confirmed record, not the pending one it started from.
    expect(document).toEqual(STORED_DOCUMENT);
    expect(document.status).toBe('stored');
  });

  test('declares the size from the bytes, never from the caller', async () => {
    // byteSize is bound into the presigned signature: a declared size that
    // disagreed with the body would be a 403 with nothing in it to explain
    // why. There is deliberately no option to override this.
    const stub = uploadStub();
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    await client.uploadDocument(DOC_CASE_ID, OPTIONS);

    const body = JSON.parse(stub.requests()[0]?.body ?? '') as Record<string, unknown>;
    expect(body).toEqual({
      kind: 'bank_statement',
      fileName: 'statement-june.pdf',
      contentType: 'application/pdf',
      byteSize: FILE.size,
    });
  });

  test('the PUT sends exactly the returned headers — no Authorization, nothing added', async () => {
    // THE ASSERTION THIS WHOLE METHOD EXISTS FOR. Those headers are signed, and
    // the URL is itself the credential: an Authorization header alongside it
    // makes S3 authenticate the request that way instead, and the signature
    // check fails.
    const stub = uploadStub();
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    await client.uploadDocument(DOC_CASE_ID, OPTIONS);

    const put = stub.requests()[1];
    expect(put).toBeDefined();
    expect(headerMap(put?.headers ?? new Headers())).toEqual({
      'content-type': 'application/pdf',
      'x-amz-server-side-encryption': 'aws:kms',
      'x-amz-tagging': 'upload=unconfirmed',
    });
    expect(put?.headers.has('authorization')).toBe(false);
    expect(put?.headers.has('accept')).toBe(false);
    // The bytes themselves, not a re-encoding of them.
    expect(put?.rawBody).toBe(FILE);
  });

  test('a header the server starts signing rides along untouched', async () => {
    const extended = {
      ...UPLOAD_BLOCK,
      headers: { ...UPLOAD_BLOCK.headers, 'x-amz-checksum-crc32': 'AAAAAA==' },
    };
    const stub = stubFetch((seen) => {
      if (seen.url.startsWith(UPLOAD_BLOCK.url)) {
        return new Response('', { status: 200 });
      }
      if (seen.url.endsWith('/complete')) {
        return jsonResponse({ document: STORED_DOCUMENT }, 200);
      }
      return jsonResponse({ document: PENDING_DOCUMENT, upload: extended }, 201);
    });
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    await client.uploadDocument(DOC_CASE_ID, OPTIONS);

    expect(stub.requests()[1]?.headers.get('x-amz-checksum-crc32')).toBe('AAAAAA==');
  });

  test('a failed PUT throws with S3’s status and body, and does NOT complete', async () => {
    // The pending record is deliberately left in place: the case still shows
    // the file as "upload didn't finish", the user can retry, and the bucket
    // reaps it within a day if they do not.
    const stub = uploadStub({ putStatus: 403 });
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const error = asApiException(await rejection(client.uploadDocument(DOC_CASE_ID, OPTIONS)));

    expect(error.statusCode).toBe(403);
    expect(error.body).toBe('AccessDenied');
    expect(error.message).toContain('presigned upload failed');
    // Two calls, not three: nothing claims an upload that did not land.
    expect(stub.callCount()).toBe(2);
  });

  test('a 409 from the confirm step surfaces as isUploadIncomplete', async () => {
    const stub = stubFetch((seen) => {
      if (seen.url.startsWith(UPLOAD_BLOCK.url)) {
        return new Response('', { status: 200 });
      }
      if (seen.url.endsWith('/complete')) {
        return jsonResponse(
          { error: 'ConflictError', message: 'the upload did not complete' },
          409,
        );
      }
      return jsonResponse({ document: PENDING_DOCUMENT, upload: UPLOAD_BLOCK }, 201);
    });
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const error = await rejection(client.uploadDocument(DOC_CASE_ID, OPTIONS));

    expect(isUploadIncomplete(error)).toBe(true);
    expect(stub.callCount()).toBe(3);
  });

  test('a 400 on the create step never reaches the network for the PUT', async () => {
    const stub = stubFetch(() =>
      jsonResponse({ error: 'ValidationError', fields: { fileName: 'Must be a file name.' } }, 400),
    );
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const error = asApiValidationException(
      await rejection(client.uploadDocument(DOC_CASE_ID, { ...OPTIONS, fileName: '..' })),
    );

    expect(error.fields).toEqual({ fileName: 'Must be a file name.' });
    expect(stub.callCount()).toBe(1);
  });

  test('no access token throws ApiUnauthorizedException without calling fetch at all', async () => {
    const stub = uploadStub();
    const client = new InsolviaApiClient('http://localhost:8080', { fetch: stub.fetch });

    const error = asApiUnauthorizedException(
      await rejection(client.uploadDocument(DOC_CASE_ID, OPTIONS)),
    );

    expect(stub.callCount()).toBe(0);
    expect(error.source).toBe('client');
  });
});

describe('public endpoints stay public', () => {
  // The regression guard for the whole auth feature. `GET /health` and
  // `POST /v1/waitlist` are unauthenticated on the server, and the marketing
  // site calls the second one server-to-server with no user in sight.
  // Attaching a bearer token to either would leak a credential to an endpoint
  // that has no business seeing one, so configuring a provider must change
  // NOTHING about what these two send.
  test('health() sends no Authorization header even with a token provider configured', async () => {
    const stub = stubFetch(() =>
      jsonResponse(
        { status: 'ok', service: 'insolvia-api', version: '0.1.0', environment: 'local' },
        200,
      ),
    );
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    await client.health();

    const seen = stub.lastRequest();
    expect(seen.headers.has('authorization')).toBe(false);
    // The full header set, pinned: exactly what it sent before auth existed.
    expect(seen.headers.get('accept')).toBe('application/json');
  });

  test('joinWaitlist() sends no Authorization header even with a token provider configured', async () => {
    const stub = stubFetch(() =>
      jsonResponse({ id: 'x', submittedAt: '2026-07-23T00:00:00.000Z' }, 201),
    );
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    await client.joinWaitlist({
      name: 'Ada Lovelace',
      firm: 'Lovelace Law LLC',
      email: 'ada@lovelace.law',
    });

    const seen = stub.lastRequest();
    expect(seen.headers.has('authorization')).toBe(false);
    expect(seen.headers.get('accept')).toBe('application/json');
    expect(seen.headers.get('content-type')).toMatch(/^application\/json/);
  });

  test('a public call still works with no token provider at all', async () => {
    // The provider is optional: a client built for marketing or for a
    // signed-out app must not need one.
    const stub = stubFetch(() =>
      jsonResponse(
        { status: 'ok', service: 'insolvia-api', version: '0.1.0', environment: 'local' },
        200,
      ),
    );
    const client = new InsolviaApiClient('http://localhost:8080', { fetch: stub.fetch });

    await expect(client.health()).resolves.toMatchObject({ status: 'ok' });
    expect(stub.lastRequest().headers.has('authorization')).toBe(false);
  });
});
