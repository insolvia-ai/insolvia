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
  BUSINESS_TYPES,
  CASE_COLLECTIONS,
  CLAIM_CLASSES,
  DEBT_CHARACTERS,
  ESTIMATED_CREDITORS_BANDS,
  ESTIMATED_DOLLAR_BANDS,
  FEE_HANDLING,
  FILING_PROFESSIONAL_ROLES,
  SMALL_BUSINESS_STATUSES,
  SOFA_ENTRY_TYPES,
  DOCUMENT_CONTENT_TYPES,
  DOCUMENT_KINDS,
  DOCUMENT_STATUSES,
  InsolviaApiClient,
  MAX_DOCUMENT_BYTE_SIZE,
  isDocumentContentType,
  isDocumentKind,
  isUploadIncomplete,
  permits,
  staffTypedProvenance,
  submittedAtUtc,
} from '@insolvia-ai/api-client';
import type {
  Debtor,
  FetchLike,
  PutDebtorRequest,
  WaitlistSubmission,
} from '@insolvia-ai/api-client';

/**
 * An obviously-fake stand-in for a Cognito access token. This repo is public:
 * nothing here may resemble a real credential, and a real JWT is not needed —
 * the client treats the token as an opaque string and never parses it.
 */
const ACCESS_TOKEN = 'test-access-token';

// Obviously-fake identity values for the firm suites at the end of this file.
const BASE_URL = 'https://staging-api.insolvia.ai';
const SUBJECT = 'a11c0000-0000-4000-8000-00000000a11c';
const USERNAME = '11111111-2222-3333-4444-555555555555';
const CLIENT_ID = 'exampleappclientid000000';

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
    createdBy: '3c9a1f7e-0d52-4a18-b6c3-9e14f7a20b55',
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
      createdBy: '3c9a1f7e-0d52-4a18-b6c3-9e14f7a20b55',
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
    createdBy: '3c9a1f7e-0d52-4a18-b6c3-9e14f7a20b55',
    chapter: 7,
    district: 'D. Del.',
    status: 'intake',
    createdAt: '2026-07-23T09:15:00.123Z',
    updatedAt: '2026-07-23T09:15:00.123Z',
  };
  const CASE_B = {
    id: 'b4a2f0e1-5c3d-4e2f-8b6a-7d9f1e2a3b4c',
    createdBy: '3c9a1f7e-0d52-4a18-b6c3-9e14f7a20b55',
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
    createdBy: '3c9a1f7e-0d52-4a18-b6c3-9e14f7a20b55',
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
    createdBy: '3c9a1f7e-0d52-4a18-b6c3-9e14f7a20b55',
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

// The debtor endpoints are pinned against
// services/api/src/insolvia_api/api/routes/debtors.py and
// .../core/{debtors,provenance}.py. Two things about them differ from every
// other endpoint above and are deliberate on both sides:
//
//   1. THE BODIES ARE snake_case. `case_json` emits `createdAt`; `debtor_json`
//      emits `created_at` and `filing_role`. The models mirror the wire rather
//      than translating, so these literals are the server's spelling verbatim.
//   2. ABSENT MEANS ABSENT. The API omits empty values, empty sub-objects and
//      empty lists entirely instead of sending nulls, because most of a
//      progressive intake is empty most of the time. Several assertions below
//      are `'k' in obj`, not `toBeUndefined()`, for exactly that reason.
const DEBTOR_CASE_ID = 'a3f1e9d0-4b2c-4d1e-9a7f-6c8e0d1f2a3b';

/**
 * A saved debtor as `debtor_json` builds it: identity, an always-present
 * `provenance` map, and only the case-data fields that hold something. The
 * timestamps carry microseconds because the API formats them with
 * `timespec="microseconds"`.
 */
const DEBTOR = {
  id: '7c9e6679-7425-40de-944b-e07fc1f90ae7',
  case_id: DEBTOR_CASE_ID,
  filing_role: 'debtor_1',
  created_at: '2026-08-05T09:15:00.123456Z',
  updated_at: '2026-08-05T09:15:00.123456Z',
  provenance: {
    'name.given': { source: 'staff_typed' },
    'name.surname': { source: 'staff_typed' },
    'residence_address.city': { source: 'staff_typed' },
    'residence_address.state': { source: 'staff_typed' },
    'other_names_used[n1].surname': { source: 'staff_typed' },
    'credit_counseling.status': { source: 'staff_typed' },
  },
  name: { given: 'Ada', surname: 'Lovelace' },
  other_names_used: [{ id: 'n1', surname: 'Byron' }],
  residence_address: { city: 'Wilmington', state: 'DE' },
  credit_counseling: { status: 'completed_with_certificate' },
};

describe('putDebtor', () => {
  const REQUEST: PutDebtorRequest = {
    name: { given: 'Ada', surname: 'Lovelace' },
    other_names_used: [{ id: 'n1', surname: 'Byron' }],
    residence_address: { city: 'Wilmington', state: 'DE' },
    credit_counseling: { status: 'completed_with_certificate' },
    provenance: {
      'name.given': { source: 'staff_typed' },
      'name.surname': { source: 'staff_typed' },
      'residence_address.city': { source: 'staff_typed' },
      'residence_address.state': { source: 'staff_typed' },
      'other_names_used[n1].surname': { source: 'staff_typed' },
      'credit_counseling.status': { source: 'staff_typed' },
    },
  };

  test('PUTs /v1/cases/{caseId}/debtors/{filingRole} and maps the 201 a first save returns', async () => {
    const stub = stubFetch(() => jsonResponse(DEBTOR, 201));
    const client = new InsolviaApiClient('https://staging-api.insolvia.ai', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const saved = await client.putDebtor(DEBTOR_CASE_ID, 'debtor_1', REQUEST);

    const seen = stub.lastRequest();
    expect(seen.method).toBe('PUT');
    expect(seen.url).toBe(
      `https://staging-api.insolvia.ai/v1/cases/${DEBTOR_CASE_ID}/debtors/debtor_1`,
    );
    expect(seen.headers.get('authorization')).toBe(`Bearer ${ACCESS_TOKEN}`);
    expect(seen.headers.get('content-type')).toMatch(/^application\/json/);
    expect(JSON.parse(seen.body)).toEqual({
      name: { given: 'Ada', surname: 'Lovelace' },
      other_names_used: [{ id: 'n1', surname: 'Byron' }],
      residence_address: { city: 'Wilmington', state: 'DE' },
      credit_counseling: { status: 'completed_with_certificate' },
      provenance: REQUEST.provenance,
    });

    expect(saved).toEqual(DEBTOR);
  });

  test('accepts the 200 a repeat save returns, not only the 201', async () => {
    // The endpoint answers 201 when the role had no record and 200 when it
    // replaced one. Autosave sends the same request either way, so a client
    // that only accepted 201 would fail on every save after the first.
    const stub = stubFetch(() => jsonResponse(DEBTOR, 200));
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    await expect(client.putDebtor(DEBTOR_CASE_ID, 'debtor_1', REQUEST)).resolves.toEqual(DEBTOR);
  });

  test('a 2xx that is neither 200 nor 201 is still a failure', async () => {
    // "Any 2xx" would accept a 204 from a proxy that never reached the
    // endpoint, and the failure would then surface as a malformed debtor
    // rather than as the unexpected status it is.
    // A 204 carries no body by definition — which is exactly why accepting it
    // as a success would surface as "the debtor was malformed".
    const stub = stubFetch(() => new Response(null, { status: 204 }));
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const error = asApiException(
      await rejection(client.putDebtor(DEBTOR_CASE_ID, 'debtor_1', REQUEST)),
    );

    expect(error.statusCode).toBe(204);
  });

  test('omits absent members entirely — including sub-objects and lists that hold nothing', async () => {
    // The API prunes empties out of what it returns; the request mirrors that,
    // so a record sent and the record returned are the same shape. `in` rather
    // than toBeUndefined: a key present with an undefined value would still
    // serialise differently and still answer true here.
    const stub = stubFetch(() => jsonResponse(DEBTOR, 201));
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    await client.putDebtor(DEBTOR_CASE_ID, 'debtor_2', {
      name: { given: 'Ada', middle: undefined },
      other_names_used: [],
      employer_ids: [],
      residence_address: {},
      phone: undefined,
      provenance: { 'name.given': { source: 'staff_typed' } },
    });

    const body = JSON.parse(stub.lastRequest().body) as Record<string, unknown>;
    expect(body).toEqual({
      name: { given: 'Ada' },
      provenance: { 'name.given': { source: 'staff_typed' } },
    });
    expect('middle' in (body.name as Record<string, unknown>)).toBe(false);
    expect('other_names_used' in body).toBe(false);
    expect('employer_ids' in body).toBe(false);
    expect('residence_address' in body).toBe(false);
    expect('phone' in body).toBe(false);
  });

  test('an empty provenance map is omitted, and an empty body is a legal save', async () => {
    // Progressive intake: a questionnaire the user has only opened must
    // persist, and it has nothing to attribute.
    const stub = stubFetch(() => jsonResponse({ ...DEBTOR, provenance: {} }, 201));
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    await client.putDebtor(DEBTOR_CASE_ID, 'non_filing_spouse', { provenance: {} });

    expect(JSON.parse(stub.lastRequest().body)).toEqual({});
  });

  test('sends every body member under its exact snake_case wire name', async () => {
    const stub = stubFetch(() => jsonResponse(DEBTOR, 200));
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    await client.putDebtor(DEBTOR_CASE_ID, 'debtor_1', {
      name: { given: 'Ada', middle: 'Augusta', surname: 'Lovelace', suffix: 'Jr.' },
      other_names_used: [{ id: 'n1', given: 'Ada', business_name: 'Analytical Engines' }],
      employer_ids: ['12-3456789'],
      residence_address: { line1: '1 Main St', line2: 'Apt 2', postal_code: '19801' },
      mailing_address: { city: 'Wilmington' },
      phone: '302-555-0100',
      mobile: '302-555-0101',
      email: 'ada@lovelace.law',
      venue: { basis: 'other', explanation: 'Moved 90 days ago.' },
      credit_counseling: { status: 'not_required', exemption_reason: 'active_duty' },
      signed_at: '2026-08-05',
    });

    expect(JSON.parse(stub.lastRequest().body)).toEqual({
      name: { given: 'Ada', middle: 'Augusta', surname: 'Lovelace', suffix: 'Jr.' },
      other_names_used: [{ id: 'n1', given: 'Ada', business_name: 'Analytical Engines' }],
      employer_ids: ['12-3456789'],
      residence_address: { line1: '1 Main St', line2: 'Apt 2', postal_code: '19801' },
      mailing_address: { city: 'Wilmington' },
      phone: '302-555-0100',
      mobile: '302-555-0101',
      email: 'ada@lovelace.law',
      venue: { basis: 'other', explanation: 'Moved 90 days ago.' },
      credit_counseling: { status: 'not_required', exemption_reason: 'active_duty' },
      signed_at: '2026-08-05',
    });
  });

  test('sends a machine-sourced provenance entry whole, keeping an empty locator', async () => {
    // `provenance_json` server-side drops nulls and nothing else, so an
    // explicitly empty locator survives a round trip. Pruning it here would
    // make a re-sent record differ from the one received.
    const stub = stubFetch(() => jsonResponse(DEBTOR, 200));
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    await client.putDebtor(DEBTOR_CASE_ID, 'debtor_1', {
      name: { given: 'Ada' },
      provenance: {
        'name.given': {
          source: 'ai_extracted',
          confirmed_by: 'staff-1',
          confirmed_at: '2026-08-05T12:00:00.000000Z',
          document_id: 'd1',
          extraction_id: 'x1',
          confidence: 0.9,
          locator: {},
        },
      },
    });

    const body = JSON.parse(stub.lastRequest().body) as Record<string, unknown>;
    expect(body.provenance).toEqual({
      'name.given': {
        source: 'ai_extracted',
        confirmed_by: 'staff-1',
        confirmed_at: '2026-08-05T12:00:00.000000Z',
        document_id: 'd1',
        extraction_id: 'x1',
        confidence: 0.9,
        locator: {},
      },
    });
  });

  test('URL-encodes the caseId into the path', async () => {
    const stub = stubFetch(() => jsonResponse(DEBTOR, 201));
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    await client.putDebtor('id with spaces/slash', 'debtor_1', REQUEST);

    expect(new URL(stub.lastRequest().url).pathname).toBe(
      '/v1/cases/id%20with%20spaces%2Fslash/debtors/debtor_1',
    );
  });

  test('a 400 carries the dotted per-field keys through unsplit', async () => {
    // The debtor endpoint keys its field errors by PATH, and provenance
    // failures are keyed by `provenance.<path>`. A client that split on "." to
    // group them would lose the one thing the form needs to find the input.
    const stub = stubFetch(() =>
      jsonResponse(
        {
          error: 'ValidationError',
          fields: {
            'name.given': 'Must be 200 characters or fewer.',
            'other_names_used[0].id': 'Must be letters, digits, hyphen or underscore.',
            'provenance.residence_address.city': 'This field has a value but no provenance.',
            tax_id:
              'Tax identifiers are not accepted yet — they need field-level encryption, which is not built. Leave this out.',
          },
        },
        400,
      ),
    );
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const error = asApiValidationException(
      await rejection(client.putDebtor(DEBTOR_CASE_ID, 'debtor_1', REQUEST)),
    );

    expect(error.statusCode).toBe(400);
    expect(error.fields['name.given']).toBe('Must be 200 characters or fewer.');
    expect(error.fields['provenance.residence_address.city']).toBe(
      'This field has a value but no provenance.',
    );
    expect(error.fields['other_names_used[0].id']).toBe(
      'Must be letters, digits, hyphen or underscore.',
    );
    expect(Object.keys(error.fields)).toHaveLength(4);
  });

  test("maps a 404 to a plain ApiException — the case is unknown or not the caller's", async () => {
    const stub = stubFetch(() =>
      jsonResponse({ error: 'NotFoundError', message: 'case not found' }, 404),
    );
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const error = asApiException(
      await rejection(client.putDebtor(DEBTOR_CASE_ID, 'debtor_1', REQUEST)),
    );

    expect(error.statusCode).toBe(404);
    expect(error).not.toBeInstanceOf(ApiValidationException);
  });

  test('no access token throws ApiUnauthorizedException without calling fetch', async () => {
    const stub = stubFetch(() => jsonResponse(DEBTOR, 201));
    const client = new InsolviaApiClient('http://localhost:8080', { fetch: stub.fetch });

    const error = asApiUnauthorizedException(
      await rejection(client.putDebtor(DEBTOR_CASE_ID, 'debtor_1', REQUEST)),
    );

    expect(stub.callCount()).toBe(0);
    expect(error.source).toBe('client');
  });

  test('a 200 whose filing_role is not one of the three roles is rejected, not cast', async () => {
    const stub = stubFetch(() => jsonResponse({ ...DEBTOR, filing_role: 'debtor_3' }, 200));
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const error = asApiException(
      await rejection(client.putDebtor(DEBTOR_CASE_ID, 'debtor_1', REQUEST)),
    );

    expect(error.message).toContain('filing_role');
  });

  test('a malformed nested field is named by its dotted path, the way the API names it', async () => {
    const stub = stubFetch(() => jsonResponse({ ...DEBTOR, residence_address: { city: 7 } }, 200));
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const error = asApiException(
      await rejection(client.putDebtor(DEBTOR_CASE_ID, 'debtor_1', REQUEST)),
    );

    expect(error.message).toContain('residence_address.city');
  });

  test('the record sent and the record returned hold the same keys', async () => {
    // The property that makes fetch-edit-save work: the client prunes its
    // request exactly as `_prune` prunes the response, so a debtor can be read,
    // changed and written back without growing or losing keys. The stub echoes
    // the request the way the endpoint does.
    const stub = stubFetch((seen) =>
      jsonResponse(
        {
          id: DEBTOR.id,
          case_id: DEBTOR_CASE_ID,
          filing_role: 'debtor_1',
          created_at: DEBTOR.created_at,
          updated_at: DEBTOR.updated_at,
          ...(JSON.parse(seen.body) as Record<string, unknown>),
        },
        200,
      ),
    );
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const saved = await client.putDebtor(DEBTOR_CASE_ID, 'debtor_1', REQUEST);

    const sent = JSON.parse(stub.lastRequest().body) as Record<string, unknown>;
    const identity = ['id', 'case_id', 'filing_role', 'created_at', 'updated_at'];
    expect(
      Object.keys(saved)
        .filter((key) => !identity.includes(key))
        .sort(),
    ).toEqual(Object.keys(sent).sort());

    // And it survives a second pass: handing the decoded record straight back
    // sends the same bytes.
    await client.putDebtor(DEBTOR_CASE_ID, 'debtor_1', saved);
    expect(JSON.parse(stub.lastRequest().body)).toEqual(sent);
  });
});

describe('listDebtors', () => {
  const SECOND_DEBTOR = {
    id: 'b7f1e9d0-4b2c-4d1e-9a7f-6c8e0d1f2a3c',
    case_id: DEBTOR_CASE_ID,
    filing_role: 'debtor_2',
    created_at: '2026-08-05T09:16:00.000000Z',
    updated_at: '2026-08-05T09:16:00.000000Z',
    provenance: {},
  };

  test('GETs /v1/cases/{caseId}/debtors and unwraps the {"debtors": [...]} envelope', async () => {
    const stub = stubFetch(() => jsonResponse({ debtors: [DEBTOR, SECOND_DEBTOR] }, 200));
    const client = new InsolviaApiClient('https://staging-api.insolvia.ai', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const debtors = await client.listDebtors(DEBTOR_CASE_ID);

    const seen = stub.lastRequest();
    expect(seen.method).toBe('GET');
    expect(seen.url).toBe(`https://staging-api.insolvia.ai/v1/cases/${DEBTOR_CASE_ID}/debtors`);
    expect(seen.headers.get('authorization')).toBe(`Bearer ${ACCESS_TOKEN}`);
    expect(seen.body).toBe('');
    expect(seen.headers.has('content-type')).toBe(false);

    expect(debtors).toEqual([DEBTOR, SECOND_DEBTOR]);
    // The order is the order the forms print debtors in, and the API owns it.
    expect(debtors.map((debtor) => debtor.filing_role)).toEqual(['debtor_1', 'debtor_2']);
  });

  test('a case with no debtors saved yet is an empty array, not a 404', async () => {
    const stub = stubFetch(() => jsonResponse({ debtors: [] }, 200));
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    expect(await client.listDebtors(DEBTOR_CASE_ID)).toEqual([]);
  });

  test('an empty record comes back with an empty provenance map and no body keys at all', async () => {
    // `provenance` is the one member the API always sends; every case-data
    // member is genuinely absent rather than null when it holds nothing.
    const stub = stubFetch(() => jsonResponse({ debtors: [SECOND_DEBTOR] }, 200));
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const [debtor] = await client.listDebtors(DEBTOR_CASE_ID);

    expect(debtor?.provenance).toEqual({});
    expect(debtor !== undefined && 'name' in debtor).toBe(false);
    expect(debtor !== undefined && 'phone' in debtor).toBe(false);
    expect(debtor !== undefined && 'other_names_used' in debtor).toBe(false);
  });

  test('maps every field of a populated record, provenance included', async () => {
    const stub = stubFetch(() => jsonResponse({ debtors: [DEBTOR] }, 200));
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const [debtor] = await client.listDebtors(DEBTOR_CASE_ID);

    expect(debtor?.name).toEqual({ given: 'Ada', surname: 'Lovelace' });
    expect(debtor?.other_names_used).toEqual([{ id: 'n1', surname: 'Byron' }]);
    expect(debtor?.credit_counseling).toEqual({ status: 'completed_with_certificate' });
    expect(debtor?.provenance['other_names_used[n1].surname']).toEqual({ source: 'staff_typed' });
  });

  test('URL-encodes the caseId into the path', async () => {
    const stub = stubFetch(() => jsonResponse({ debtors: [] }, 200));
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    await client.listDebtors('id with spaces/slash');

    expect(new URL(stub.lastRequest().url).pathname).toBe(
      '/v1/cases/id%20with%20spaces%2Fslash/debtors',
    );
  });

  test('a 200 without the "debtors" key throws ApiException naming it', async () => {
    const stub = stubFetch(() => jsonResponse({}, 200));
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    expect(asApiException(await rejection(client.listDebtors(DEBTOR_CASE_ID))).message).toContain(
      'debtors',
    );
  });

  test('a malformed element is rejected per-element and named by its index', async () => {
    const stub = stubFetch(() =>
      jsonResponse(
        { debtors: [{ ...DEBTOR, provenance: { 'name.given': { source: 'vibes' } } }] },
        200,
      ),
    );
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const error = asApiException(await rejection(client.listDebtors(DEBTOR_CASE_ID)));

    expect(error.message).toContain('debtors[0].provenance.name.given.source');
  });

  test('maps a 404 to a plain ApiException', async () => {
    const stub = stubFetch(() =>
      jsonResponse({ error: 'NotFoundError', message: 'case not found' }, 404),
    );
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const error = asApiException(await rejection(client.listDebtors(DEBTOR_CASE_ID)));

    expect(error.statusCode).toBe(404);
    expect(error).not.toBeInstanceOf(ApiValidationException);
  });

  test('no access token throws ApiUnauthorizedException without calling fetch', async () => {
    const stub = stubFetch(() => jsonResponse({ debtors: [] }, 200));
    const client = new InsolviaApiClient('http://localhost:8080', { fetch: stub.fetch });

    const error = asApiUnauthorizedException(await rejection(client.listDebtors(DEBTOR_CASE_ID)));

    expect(stub.callCount()).toBe(0);
    expect(error.source).toBe('client');
  });
});

describe('staffTypedProvenance', () => {
  // These cases are transcribed from services/api/tests/test_provenance.py —
  // TestPopulatedPaths and TestEvasionsFoundInReview — one for one. The walk
  // exists twice on purpose (the server is the authority and re-runs it on
  // every write; this copy saves the caller from guessing), and this block is
  // what stops the two from drifting apart silently: a change to
  // `populated_paths` that is not made here fails HERE first, rather than as an
  // unexplainable 400 in the app.
  const paths = (body: Record<string, unknown>): string[] =>
    Object.keys(staffTypedProvenance(body)).sort();

  test('absent values are not populated', () => {
    expect(paths({ a: null, b: '', c: [], d: {} })).toEqual([]);
  });

  test('an explicit undefined is absent too, the way a form hands one over', () => {
    // The TypeScript half's own case: a form field that was never touched is
    // `undefined` here where it would be `None` on the server.
    expect(paths({ a: undefined, name: { given: undefined } })).toEqual([]);
  });

  test('false and zero are answers, not absences', () => {
    // The classic bug this guards: `if (!value)` would treat "no, I do not rent
    // my residence" as an unanswered question, and an extraction that got it
    // wrong would then need no confirmation.
    expect(paths({ rents_residence: false, dependents: 0 })).toEqual([
      'dependents',
      'rents_residence',
    ]);
  });

  test('nested objects produce dotted paths', () => {
    expect(paths({ name: { given: 'Ada', surname: null } })).toEqual(['name.given']);
  });

  test('list elements are addressed by id, not by position', () => {
    expect(paths({ other_names_used: [{ id: 'n1', surname: 'Byron' }] })).toEqual([
      'other_names_used[n1].surname',
    ]);
  });

  test('reordering a list does not move a path', () => {
    // The whole reason embedded list elements carry an id.
    const first = {
      aliases: [
        { id: 'a', surname: 'X' },
        { id: 'b', surname: 'Y' },
      ],
    };
    const second = {
      aliases: [
        { id: 'b', surname: 'Y' },
        { id: 'a', surname: 'X' },
      ],
    };
    expect(paths(first)).toEqual(paths(second));
  });

  test('a list without element ids is attributed whole', () => {
    // Positional paths would be a lie, so the list gets one path instead of
    // element paths that reordering would silently reattach.
    expect(paths({ employer_ids: ['12-3456789'] })).toEqual(['employer_ids']);
  });

  test('a mixed list does not discard the paths before the bad element', () => {
    // Returning mid-loop threw away earlier elements' paths server-side, so one
    // entry for the list covered them all — and which behaviour you got
    // depended on the ORDER of the elements.
    const goodFirst = { aliases: [{ id: 'n1', surname: 'Byron' }, { surname: 'X' }] };
    const badFirst = { aliases: [{ surname: 'X' }, { id: 'n1', surname: 'Byron' }] };
    expect(paths(goodFirst)).toEqual(['aliases']);
    expect(paths(badFirst)).toEqual(['aliases']);
  });

  test('an id that cannot be addressed does not mint an illegal path', () => {
    // An id containing '.' or ']' used to produce a path the server's
    // provenance parser refused, and the caller was stuck.
    expect(paths({ aliases: [{ id: 'n.1', surname: 'Byron' }] })).toEqual(['aliases']);
  });

  test('an element id is never itself a path — it is the address', () => {
    expect(paths({ aliases: [{ id: 'n1' }] })).toEqual([]);
  });

  test('a key that is not a field name fails loudly, before any request', () => {
    // These would otherwise produce a REQUIRED path that the server's
    // provenance parser then refuses as a key: no payload could satisfy both,
    // and the caller would be left with a 400 they could not fix.
    for (const key of ['SSN', 'legalName', '1099_income', 'case-number', '']) {
      expect(() => staffTypedProvenance({ [key]: 'value' })).toThrow(/not a field name/);
    }
  });

  test('stamps staff_typed on every path, and nothing else', () => {
    // `staff_typed` needs no confirmation — the person typing it IS the
    // confirmation. Anything machine-sourced needs a confirmer and a moment,
    // and inventing those is exactly what this helper must not do.
    expect(staffTypedProvenance({ name: { given: 'Ada' } })).toEqual({
      'name.given': { source: 'staff_typed' },
    });
  });

  test('skips server-stamped identity, so a fetched debtor can be handed straight back', async () => {
    // `debtor_body()` drops the same six keys before the server checks the
    // rule, so asking for provenance on them would be asking where an id came
    // from.
    const stub = stubFetch(() => jsonResponse({ debtors: [DEBTOR] }, 200));
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const [debtor] = await client.listDebtors(DEBTOR_CASE_ID);

    expect(debtor).toBeDefined();
    expect(Object.keys(staffTypedProvenance(debtor as Debtor)).sort()).toEqual(
      Object.keys(DEBTOR.provenance).sort(),
    );
  });

  test('every key it produces is a path the API accepts, and covers every populated field', () => {
    // The property the two halves of the rule share, stated directly:
    // `_FIELD_PATH_RE` in core/provenance.py is transcribed here, so a path
    // this helper can mint but the server would refuse fails the test rather
    // than the request.
    const segment = '[a-z][a-z0-9_]*(?:\\[[A-Za-z0-9_-]+\\])?';
    const fieldPath = new RegExp(`^${segment}(?:\\.${segment})*$`);

    const body: PutDebtorRequest = {
      name: { given: 'Ada', surname: 'Lovelace' },
      other_names_used: [{ id: 'n1', surname: 'Byron' }],
      employer_ids: ['12-3456789'],
      residence_address: { city: 'Wilmington', state: 'DE' },
      venue: { basis: 'lived_longest_180_days' },
      credit_counseling: { status: 'not_required', exemption_reason: 'active_duty' },
      email: 'ada@lovelace.law',
    };

    const provenance = staffTypedProvenance(body);

    for (const path of Object.keys(provenance)) {
      expect(path).toMatch(fieldPath);
    }
    expect(Object.keys(provenance).sort()).toEqual([
      'credit_counseling.exemption_reason',
      'credit_counseling.status',
      'email',
      'employer_ids',
      'name.given',
      'name.surname',
      'other_names_used[n1].surname',
      'residence_address.city',
      'residence_address.state',
      'venue.basis',
    ]);
  });

  test('the map it builds satisfies the request it was built from', async () => {
    // End to end: whatever the codec ends up sending, every value in it has an
    // entry. That equality is invariant 1, checked before the request rather
    // than by a 400 after it.
    const body: PutDebtorRequest = {
      name: { given: 'Ada' },
      other_names_used: [{ id: 'n1', surname: 'Byron' }],
      residence_address: { city: 'Wilmington' },
    };
    const stub = stubFetch(() => jsonResponse(DEBTOR, 201));
    const client = new InsolviaApiClient('http://localhost:8080', {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    await client.putDebtor(DEBTOR_CASE_ID, 'debtor_1', {
      ...body,
      provenance: staffTypedProvenance(body),
    });

    const sent = JSON.parse(stub.lastRequest().body) as Record<string, unknown>;
    const { provenance: _sentProvenance, ...sentBody } = sent;
    expect(Object.keys(staffTypedProvenance(sentBody)).sort()).toEqual(
      Object.keys(_sentProvenance as Record<string, unknown>).sort(),
    );
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

// ---------------------------------------------------------------------------
// Firms: the tenancy surface.
//
// A case belongs to a FIRM, not to whoever opened it. These pin the wire shape
// of that model — the `firm` block on /v1/me, the two staff representations,
// and case assignment. Every response body below is copied from what the route
// handler actually returns, per this package's rule.
// ---------------------------------------------------------------------------

describe('the firm block on /v1/me', () => {
  const PERMISSIONS = {
    cases: 'add_edit',
    intake: 'add_edit',
    documents: 'add_edit',
    extraction_review: 'add_edit',
    firm_administration: 'add_edit',
  };

  test('maps the firm block through, with the EFFECTIVE permissions', async () => {
    const stub = stubFetch(() =>
      jsonResponse(
        {
          subject: SUBJECT,
          username: USERNAME,
          clientId: CLIENT_ID,
          scopes: ['aws.cognito.signin.user.admin'],
          expiresAt: 1893456000,
          firm: {
            id: 'f1a2b3c4-0000-4000-8000-000000000001',
            name: 'Example & Partners',
            role: 'attorney',
            firstName: 'Alice',
            lastName: 'Attorney',
            displayName: 'Alice Attorney',
            isAdmin: true,
            accessAllCases: false,
            permissions: PERMISSIONS,
          },
        },
        200,
      ),
    );
    const client = new InsolviaApiClient(BASE_URL, {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const me = await client.me();

    expect(me.firm).toEqual({
      id: 'f1a2b3c4-0000-4000-8000-000000000001',
      name: 'Example & Partners',
      role: 'attorney',
      firstName: 'Alice',
      lastName: 'Attorney',
      displayName: 'Alice Attorney',
      isAdmin: true,
      accessAllCases: false,
      permissions: PERMISSIONS,
    });
  });

  test('a caller in no firm has NO firm key — absent, not null', async () => {
    // The state a client renders as "ask your administrator to add you". A
    // `null` here would be indistinguishable from a decode that dropped it,
    // and `'firm' in me` is what a caller will actually write.
    const stub = stubFetch(() =>
      jsonResponse(
        {
          subject: SUBJECT,
          username: USERNAME,
          clientId: CLIENT_ID,
          scopes: [],
          expiresAt: 1893456000,
        },
        200,
      ),
    );
    const client = new InsolviaApiClient(BASE_URL, {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const me = await client.me();

    expect(me.firm).toBeUndefined();
    expect('firm' in me).toBe(false);
  });

  test('a feature the server did not send reads as hidden, never as permissive', async () => {
    // `extraction_review` ships later. A client that treated a missing key as
    // permissive would show a button the server refuses — fail closed, the
    // same rule the server applies to its own stored map.
    const stub = stubFetch(() =>
      jsonResponse(
        {
          subject: SUBJECT,
          username: null,
          clientId: CLIENT_ID,
          scopes: [],
          expiresAt: null,
          firm: {
            id: 'f1a2b3c4-0000-4000-8000-000000000001',
            name: 'Example & Partners',
            role: 'staff',
            firstName: 'Sam',
            lastName: 'Staff',
            displayName: 'Sam Staff',
            isAdmin: false,
            accessAllCases: false,
            permissions: { cases: 'view_only' },
          },
        },
        200,
      ),
    );
    const client = new InsolviaApiClient(BASE_URL, {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const me = await client.me();

    expect(me.firm?.permissions.extraction_review).toBe('hidden');
    expect(me.firm?.permissions.cases).toBe('view_only');
  });

  test('a level this version cannot rank is malformed, not guessed', async () => {
    // The asymmetry with the case above, and it is the one that matters: an
    // unknown FEATURE is skipped (fail closed), an unknown LEVEL on a feature
    // we do know cannot be ranked at all, and guessing is the one direction
    // that can over-grant.
    const stub = stubFetch(() =>
      jsonResponse(
        {
          subject: SUBJECT,
          username: null,
          clientId: CLIENT_ID,
          scopes: [],
          expiresAt: null,
          firm: {
            id: 'f1a2b3c4-0000-4000-8000-000000000001',
            name: 'Example & Partners',
            role: 'attorney',
            firstName: 'Alice',
            lastName: 'Attorney',
            displayName: 'Alice Attorney',
            isAdmin: false,
            accessAllCases: false,
            permissions: { cases: 'full_control' },
          },
        },
        200,
      ),
    );
    const client = new InsolviaApiClient(BASE_URL, {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    await expect(client.me()).rejects.toThrow(ApiException);
  });
});

describe('updateMe', () => {
  // Pinned against services/api/src/insolvia_api/api/routes/me.py's PATCH,
  // which answers with the SAME body as the GET — one serializer for both,
  // so the client re-renders from the response without a follow-up GET.
  const RENAMED = {
    subject: SUBJECT,
    username: USERNAME,
    clientId: CLIENT_ID,
    scopes: ['aws.cognito.signin.user.admin'],
    expiresAt: 1893456000,
    firm: {
      id: 'f1a2b3c4-0000-4000-8000-000000000001',
      name: 'Example & Partners',
      role: 'staff',
      firstName: 'Corrected',
      lastName: 'Name',
      displayName: 'Corrected Name',
      isAdmin: false,
      accessAllCases: false,
      permissions: {
        cases: 'view_only',
        intake: 'view_only',
        documents: 'view_only',
        extraction_review: 'hidden',
        firm_administration: 'hidden',
      },
    },
  };

  test('PATCHes /v1/me with exactly the two name halves and maps the answered principal', async () => {
    const stub = stubFetch(() => jsonResponse(RENAMED, 200));
    const client = new InsolviaApiClient(BASE_URL, {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const principal = await client.updateMe({ firstName: 'Corrected', lastName: 'Name' });

    const seen = stub.lastRequest();
    expect(seen.method).toBe('PATCH');
    expect(seen.url).toBe(`${BASE_URL}/v1/me`);
    expect(seen.headers.get('authorization')).toBe(`Bearer ${ACCESS_TOKEN}`);
    expect(seen.headers.get('content-type')).toBe('application/json');
    // The whole writable surface. A `displayName` key here would mean the
    // client started sending a field the server derives and never accepts.
    expect(JSON.parse(seen.body)).toEqual({ firstName: 'Corrected', lastName: 'Name' });

    expect(principal.firm?.firstName).toBe('Corrected');
    expect(principal.firm?.lastName).toBe('Name');
    // The derived field rides along, which is what lets a screen that only
    // renders a name stay untouched by the split.
    expect(principal.firm?.displayName).toBe('Corrected Name');
    expect(principal.subject).toBe(SUBJECT);
  });

  test('sends only the half that was supplied', async () => {
    // OMIT-WHEN-ABSENT, this package's standing rule, and it carries meaning
    // here: an unsent half means "leave it alone", while `''` would mean
    // "erase it". Correcting a surname alone is the common case for a row
    // whose halves were derived from a pre-split display name.
    const stub = stubFetch(() => jsonResponse(RENAMED, 200));
    const client = new InsolviaApiClient(BASE_URL, {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    await client.updateMe({ lastName: 'Name' });

    expect(JSON.parse(stub.lastRequest().body)).toEqual({ lastName: 'Name' });
  });

  test('a response missing a name half is a malformed contract, not an empty string', async () => {
    // `requireString` rather than an optional read. `''` is a value the server
    // always SENDS; a missing key is a contract break, and silently defaulting
    // it would hide exactly the drift this package exists to catch.
    const withoutHalves = {
      ...RENAMED,
      firm: Object.fromEntries(Object.entries(RENAMED.firm).filter(([key]) => key !== 'lastName')),
    };
    const stub = stubFetch(() => jsonResponse(withoutHalves, 200));
    const client = new InsolviaApiClient(BASE_URL, {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    await expect(client.updateMe({ firstName: 'Corrected' })).rejects.toThrow(ApiException);
  });

  test('a 400 with a field message surfaces as ApiValidationException', async () => {
    // The literal body app_factory's FieldValidationError handler emits.
    const stub = stubFetch(() =>
      jsonResponse({ error: 'ValidationError', fields: { firstName: 'A name is required.' } }, 400),
    );
    const client = new InsolviaApiClient(BASE_URL, {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const failure = client.updateMe({ firstName: ' ' });

    await expect(failure).rejects.toThrow(ApiValidationException);
    await failure.catch((caught: unknown) => {
      // Keyed by the HALF that was wrong, which is what puts the server's
      // message under the right input rather than under the whole form.
      expect((caught as ApiValidationException).fields.firstName).toBe('A name is required.');
    });
  });

  test('a caller in no firm gets the 403 through as an ApiException', async () => {
    // Unlike the GET, which reports firmlessness as an answer, there is no
    // row to rename — the server's ForbiddenError body comes through.
    const stub = stubFetch(() =>
      jsonResponse({ error: 'ForbiddenError', message: 'no active firm membership' }, 403),
    );
    const client = new InsolviaApiClient(BASE_URL, {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    await expect(client.updateMe({ firstName: 'No', lastName: 'Body' })).rejects.toThrow(
      ApiException,
    );
  });

  test('refuses to send without a token, before any request', async () => {
    const stub = stubFetch(() => jsonResponse(RENAMED, 200));
    const client = new InsolviaApiClient(BASE_URL, { fetch: stub.fetch });

    await expect(client.updateMe({ firstName: 'No', lastName: 'Body' })).rejects.toThrow(
      ApiUnauthorizedException,
    );
    expect(stub.requests()).toHaveLength(0);
  });
});

describe('permits', () => {
  test('add_edit satisfies view_only, and hidden satisfies nothing', () => {
    // Exists so no caller writes `permissions.documents === 'add_edit'` and
    // thereby treats an add_edit holder as unable to view.
    expect(permits('add_edit', 'view_only')).toBe(true);
    expect(permits('add_edit', 'add_edit')).toBe(true);
    expect(permits('view_only', 'add_edit')).toBe(false);
    expect(permits('hidden', 'view_only')).toBe(false);
  });
});

describe('the firm record', () => {
  // Pinned against services/api/src/insolvia_api/api/routes/firm.py's
  // GET/PATCH /v1/firm — the literal firm_summary_json shape, which carries
  // NO createdBy/createdByEmail: those name the Insolvia staff member who
  // provisioned the firm and never appear in a tenant response.
  const FIRM_RECORD = {
    id: 'f1a2b3c4-0000-4000-8000-000000000001',
    name: 'Example & Partners',
    status: 'active',
    createdAt: '2026-01-05T09:00:00.000Z',
    updatedAt: '2026-08-01T12:00:00.000Z',
  };

  test('GETs /v1/firm and maps the tenant shape exactly', async () => {
    const stub = stubFetch(() => jsonResponse(FIRM_RECORD, 200));
    const client = new InsolviaApiClient(BASE_URL, {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const firm = await client.getFirm();

    const seen = stub.lastRequest();
    expect(seen.method).toBe('GET');
    expect(seen.url).toBe(`${BASE_URL}/v1/firm`);
    expect(seen.headers.get('authorization')).toBe(`Bearer ${ACCESS_TOKEN}`);
    expect(firm).toEqual(FIRM_RECORD);
  });

  test('PATCHes /v1/firm with exactly a name and maps the echoed record', async () => {
    const stub = stubFetch(() => jsonResponse({ ...FIRM_RECORD, name: 'Example, LLP' }, 200));
    const client = new InsolviaApiClient(BASE_URL, {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const firm = await client.updateFirm({ name: 'Example, LLP' });

    const seen = stub.lastRequest();
    expect(seen.method).toBe('PATCH');
    expect(seen.url).toBe(`${BASE_URL}/v1/firm`);
    expect(seen.headers.get('content-type')).toBe('application/json');
    // The whole writable surface. `status` here would be the client offering
    // a self-suspension the server's parser refuses to produce.
    expect(JSON.parse(seen.body)).toEqual({ name: 'Example, LLP' });
    expect(firm.name).toBe('Example, LLP');
  });

  test('a status this version cannot rank is malformed, not guessed', async () => {
    const stub = stubFetch(() => jsonResponse({ ...FIRM_RECORD, status: 'archived' }, 200));
    const client = new InsolviaApiClient(BASE_URL, {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    await expect(client.getFirm()).rejects.toThrow(ApiException);
  });

  test('a 400 with a field message surfaces as ApiValidationException', async () => {
    const stub = stubFetch(() =>
      jsonResponse({ error: 'ValidationError', fields: { name: 'A name is required.' } }, 400),
    );
    const client = new InsolviaApiClient(BASE_URL, {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const failure = client.updateFirm({ name: ' ' });

    await expect(failure).rejects.toThrow(ApiValidationException);
    await failure.catch((caught: unknown) => {
      expect((caught as ApiValidationException).fields.name).toBe('A name is required.');
    });
  });
});

describe('listFirmDirectory', () => {
  test('GETs /v1/firm/directory and maps the thin colleague representation', async () => {
    const stub = stubFetch(() =>
      jsonResponse(
        {
          people: [
            {
              subject: 'a-1',
              firstName: 'Alice',
              lastName: 'Attorney',
              displayName: 'Alice Attorney',
              role: 'attorney',
            },
            {
              subject: 'b-2',
              firstName: 'Bob',
              lastName: 'Paralegal',
              displayName: 'Bob Paralegal',
              role: 'paralegal',
            },
          ],
        },
        200,
      ),
    );
    const client = new InsolviaApiClient(BASE_URL, {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const people = await client.listFirmDirectory();

    const seen = stub.lastRequest();
    expect(seen.method).toBe('GET');
    expect(seen.url).toBe(`${BASE_URL}/v1/firm/directory`);
    expect(seen.headers.get('authorization')).toBe(`Bearer ${ACCESS_TOKEN}`);
    expect(people).toEqual([
      {
        subject: 'a-1',
        firstName: 'Alice',
        lastName: 'Attorney',
        displayName: 'Alice Attorney',
        role: 'attorney',
      },
      {
        subject: 'b-2',
        firstName: 'Bob',
        lastName: 'Paralegal',
        displayName: 'Bob Paralegal',
        role: 'paralegal',
      },
    ]);
  });

  test('carries no email and no permissions — the thin representation', async () => {
    // If this ever starts returning more, it is a server change that widened
    // what every paralegal can see about their colleagues, and it should fail
    // here first.
    const stub = stubFetch(() =>
      jsonResponse(
        {
          people: [
            {
              subject: 'a-1',
              firstName: 'Alice',
              lastName: 'Attorney',
              displayName: 'Alice Attorney',
              role: 'attorney',
            },
          ],
        },
        200,
      ),
    );
    const client = new InsolviaApiClient(BASE_URL, {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const people = await client.listFirmDirectory();

    expect(people).toHaveLength(1);
    expect(Object.keys(people[0]!).sort()).toEqual([
      'displayName',
      'firstName',
      'lastName',
      'role',
      'subject',
    ]);
  });
});

describe('the firm user endpoints', () => {
  const SUBJECT_ID = 'b0b00000-0000-4000-8000-00000000b0b0';
  const USER = {
    subject: SUBJECT_ID,
    email: 'bob@example.test',
    firstName: 'Bob',
    lastName: 'Paralegal',
    displayName: 'Bob Paralegal',
    role: 'paralegal',
    isAdmin: false,
    accessAllCases: false,
    permissions: {
      cases: 'add_edit',
      intake: 'add_edit',
      documents: 'add_edit',
      extraction_review: 'add_edit',
      firm_administration: 'hidden',
    },
    status: 'active',
    createdAt: '2026-07-23T09:15:00.123Z',
    updatedAt: '2026-07-23T09:15:00.123Z',
  };

  test('GETs /v1/firm/users and maps the whole record', async () => {
    const stub = stubFetch(() => jsonResponse({ users: [USER] }, 200));
    const client = new InsolviaApiClient(BASE_URL, {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const users = await client.listFirmUsers();

    expect(stub.lastRequest().url).toBe(`${BASE_URL}/v1/firm/users`);
    expect(users).toEqual([USER]);
  });

  test('POSTs only the fields that were supplied — no undefined keys', async () => {
    const stub = stubFetch(() => jsonResponse(USER, 201));
    const client = new InsolviaApiClient(BASE_URL, {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    await client.addFirmUser({
      email: 'bob@example.test',
      firstName: 'Bob',
      lastName: 'Paralegal',
      role: 'paralegal',
    });

    const seen = stub.lastRequest();
    expect(seen.method).toBe('POST');
    expect(seen.url).toBe(`${BASE_URL}/v1/firm/users`);
    // The server treats an absent key as "use the role default" and a present
    // one as an instruction, so sending `isAdmin: undefined` would be a
    // different request from omitting it.
    expect(JSON.parse(seen.body)).toEqual({
      email: 'bob@example.test',
      firstName: 'Bob',
      lastName: 'Paralegal',
      role: 'paralegal',
    });
  });

  test('sends the optional flags when they are given', async () => {
    const stub = stubFetch(() => jsonResponse(USER, 201));
    const client = new InsolviaApiClient(BASE_URL, {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    await client.addFirmUser({
      email: 'dana@example.test',
      firstName: 'Dana',
      lastName: 'Attorney',
      role: 'attorney',
      isAdmin: false,
      accessAllCases: true,
      permissions: { documents: 'view_only' },
    });

    expect(JSON.parse(stub.lastRequest().body)).toEqual({
      email: 'dana@example.test',
      firstName: 'Dana',
      lastName: 'Attorney',
      role: 'attorney',
      isAdmin: false,
      accessAllCases: true,
      permissions: { documents: 'view_only' },
    });
  });

  test('an address that already has an account is a 409', async () => {
    const stub = stubFetch(() =>
      jsonResponse(
        {
          error: 'ConflictError',
          message: 'that email address already has an Insolvia account',
        },
        409,
      ),
    );
    const client = new InsolviaApiClient(BASE_URL, {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const error = await client
      .addFirmUser({
        email: 'taken@example.test',
        firstName: 'Al',
        lastName: 'Ready',
        role: 'staff',
      })
      .catch((thrown: unknown) => thrown);

    expect(error).toBeInstanceOf(ApiException);
    expect((error as ApiException).statusCode).toBe(409);
  });

  test('PATCHes the subject into the path, encoded once, with only the sent fields', async () => {
    const stub = stubFetch(() => jsonResponse({ ...USER, isAdmin: true }, 200));
    const client = new InsolviaApiClient(BASE_URL, {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const updated = await client.updateFirmUser(SUBJECT_ID, { isAdmin: true });

    const seen = stub.lastRequest();
    expect(seen.method).toBe('PATCH');
    expect(seen.url).toBe(`${BASE_URL}/v1/firm/users/${SUBJECT_ID}`);
    expect(JSON.parse(seen.body)).toEqual({ isAdmin: true });
    expect(updated.isAdmin).toBe(true);
  });

  test('the last-administrator refusal is a 409, not a 403', async () => {
    // It is not a permission failure: the caller HAS the permission, and it is
    // the firm's state that does not admit the change. A client that reported
    // it as "you may not do this" would be telling the one person who may.
    const stub = stubFetch(() =>
      jsonResponse(
        {
          error: 'ConflictError',
          message: 'a firm must keep at least one active administrator',
        },
        409,
      ),
    );
    const client = new InsolviaApiClient(BASE_URL, {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const error = await client
      .updateFirmUser(SUBJECT_ID, { isAdmin: false })
      .catch((thrown: unknown) => thrown);

    expect((error as ApiException).statusCode).toBe(409);
  });

  test('DELETEs the subject and resolves on 204', async () => {
    const stub = stubFetch(() => new Response(null, { status: 204 }));
    const client = new InsolviaApiClient(BASE_URL, {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    await expect(client.removeFirmUser(SUBJECT_ID)).resolves.toBeUndefined();
    expect(stub.lastRequest().method).toBe('DELETE');
    expect(stub.lastRequest().url).toBe(`${BASE_URL}/v1/firm/users/${SUBJECT_ID}`);
  });
});

describe('case assignment', () => {
  const CASE_ID = 'a3f1e9d0-4b2c-4d1e-9a7f-6c8e0d1f2a3b';
  const SUBJECT_ID = 'b0b00000-0000-4000-8000-00000000b0b0';

  test('GETs the assignees and maps subjects, not names', async () => {
    const stub = stubFetch(() =>
      jsonResponse(
        {
          assignees: [
            {
              subject: SUBJECT_ID,
              assignedAt: '2026-07-23T09:15:00.123Z',
              assignedBy: 'a11c0000-0000-4000-8000-00000000a11c',
            },
          ],
        },
        200,
      ),
    );
    const client = new InsolviaApiClient(BASE_URL, {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const assignees = await client.listCaseAssignees(CASE_ID);

    expect(stub.lastRequest().url).toBe(`${BASE_URL}/v1/cases/${CASE_ID}/assignees`);
    // Names come from listFirmDirectory. A display name copied onto an
    // assignment would go stale the moment somebody is renamed.
    expect(Object.keys(assignees[0]!).sort()).toEqual(['assignedAt', 'assignedBy', 'subject']);
  });

  test('PUTs to link, with both path segments encoded exactly once', async () => {
    const stub = stubFetch(() => new Response(null, { status: 204 }));
    const client = new InsolviaApiClient(BASE_URL, {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    await client.assignCase('case/with slash', 'sub ject');

    const seen = stub.lastRequest();
    expect(seen.method).toBe('PUT');
    expect(seen.url).toBe(`${BASE_URL}/v1/cases/case%2Fwith%20slash/assignees/sub%20ject`);
  });

  test('DELETEs to unlink', async () => {
    const stub = stubFetch(() => new Response(null, { status: 204 }));
    const client = new InsolviaApiClient(BASE_URL, {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    await expect(client.unassignCase(CASE_ID, SUBJECT_ID)).resolves.toBeUndefined();
    expect(stub.lastRequest().method).toBe('DELETE');
    expect(stub.lastRequest().url).toBe(`${BASE_URL}/v1/cases/${CASE_ID}/assignees/${SUBJECT_ID}`);
  });
});

// The generic case-collection endpoints (issue #249) are pinned against
// services/api/src/insolvia_api/api/routes/case_entities.py and
// .../core/{case_entities,case_collections,creditors,claims,sofa,...}.py.
// The bodies follow the debtor rules exactly: snake_case, absent means
// absent, provenance always present on a response and required per populated
// field on a request.
const ENTITY_CASE_ID = 'b4e2f0a1-5c3d-4e2f-8b90-7d9f1e2a3b4c';
const ENTITY_ID = '8dae7780-8536-41ef-a55c-f180d2a01bf8';

/** A saved creditor as `entity_json` builds it. */
const CREDITOR_RECORD = {
  id: ENTITY_ID,
  case_id: ENTITY_CASE_ID,
  created_at: '2026-09-01T10:00:00.123456Z',
  updated_at: '2026-09-01T10:00:00.123456Z',
  provenance: {
    name: { source: 'staff_typed' },
    'address.line1': { source: 'staff_typed' },
  },
  name: 'Example Bank',
  address: { line1: '1 Example Way' },
};

describe('addCaseEntity', () => {
  const REQUEST = {
    name: 'Example Bank',
    address: { line1: '1 Example Way' },
    provenance: {
      name: { source: 'staff_typed' },
      'address.line1': { source: 'staff_typed' },
    },
  } as const;

  test('POSTs /v1/cases/{caseId}/{collection} and maps the 201', async () => {
    const stub = stubFetch(() => jsonResponse(CREDITOR_RECORD, 201));
    const client = new InsolviaApiClient(BASE_URL, {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const saved = await client.addCaseEntity(ENTITY_CASE_ID, 'creditors', REQUEST);

    const seen = stub.lastRequest();
    expect(seen.method).toBe('POST');
    expect(seen.url).toBe(`${BASE_URL}/v1/cases/${ENTITY_CASE_ID}/creditors`);
    expect(seen.headers.get('authorization')).toBe(`Bearer ${ACCESS_TOKEN}`);
    expect(seen.headers.get('content-type')).toMatch(/^application\/json/);
    expect(JSON.parse(seen.body)).toEqual({
      name: 'Example Bank',
      address: { line1: '1 Example Way' },
      provenance: REQUEST.provenance,
    });
    expect(saved).toEqual(CREDITOR_RECORD);
  });

  test('prunes absent members and empty sub-objects from the request', async () => {
    // Mirrors the server's own prune, so the record sent and the record
    // returned compare equal. `false` survives — it is an answer.
    const stub = stubFetch(() => jsonResponse(CREDITOR_RECORD, 201));
    const client = new InsolviaApiClient(BASE_URL, {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    await client.addCaseEntity(ENTITY_CASE_ID, 'claims', {
      creditor_id: undefined,
      contingent: false,
      notice_parties: [],
      provenance: { contingent: { source: 'staff_typed' } },
    });

    expect(JSON.parse(stub.lastRequest().body)).toEqual({
      contingent: false,
      provenance: { contingent: { source: 'staff_typed' } },
    });
  });

  test('a sofa entry sends its typed payload verbatim', async () => {
    const RECORD = {
      id: ENTITY_ID,
      case_id: ENTITY_CASE_ID,
      created_at: '2026-09-01T10:00:00.123456Z',
      updated_at: '2026-09-01T10:00:00.123456Z',
      provenance: {
        entry_type: { source: 'staff_typed' },
        'payload.recipient.name': { source: 'staff_typed' },
        'payload.value': { source: 'staff_typed' },
      },
      entry_type: 'gift',
      payload: { recipient: { name: 'Example Recipient' }, value: '700.00' },
    };
    const stub = stubFetch(() => jsonResponse(RECORD, 201));
    const client = new InsolviaApiClient(BASE_URL, {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const saved = await client.addCaseEntity(ENTITY_CASE_ID, 'sofa_entries', {
      entry_type: 'gift',
      payload: { recipient: { name: 'Example Recipient' }, value: '700.00' },
      provenance: {
        entry_type: { source: 'staff_typed' },
        'payload.recipient.name': { source: 'staff_typed' },
        'payload.value': { source: 'staff_typed' },
      },
    });

    expect(JSON.parse(stub.lastRequest().body).payload).toEqual({
      recipient: { name: 'Example Recipient' },
      value: '700.00',
    });
    expect(saved).toEqual(RECORD);
  });

  test('a 400 with fields becomes ApiValidationException keyed by field path', async () => {
    // The server's shape for a missing provenance entry — the failure a
    // caller that skipped staffTypedProvenance sees.
    const stub = stubFetch(() =>
      jsonResponse(
        {
          error: 'ValidationError',
          fields: { 'provenance.name': 'This field has a value but no provenance.' },
        },
        400,
      ),
    );
    const client = new InsolviaApiClient(BASE_URL, {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const error = asApiValidationException(
      await rejection(client.addCaseEntity(ENTITY_CASE_ID, 'creditors', { name: 'X' })),
    );
    expect(error.fields['provenance.name']).toBe('This field has a value but no provenance.');
  });

  test('staffTypedProvenance covers an entity body, ready to send', async () => {
    // The same walk the debtor uses works for every collection — the sample
    // here carries a nested object, a boolean false, and a plain string list
    // (attributed whole, the employer_ids rule).
    const body = {
      name: 'Example Cosigner',
      address: { city: 'Exampleville' },
      claim_ids: ['cl-1', 'cl-2'],
    };
    expect(staffTypedProvenance(body)).toEqual({
      name: { source: 'staff_typed' },
      'address.city': { source: 'staff_typed' },
      claim_ids: { source: 'staff_typed' },
    });
  });
});

describe('listCaseEntities', () => {
  test('GETs the collection and unwraps its own envelope key', async () => {
    const stub = stubFetch(() => jsonResponse({ creditors: [CREDITOR_RECORD] }, 200));
    const client = new InsolviaApiClient(BASE_URL, {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const listed = await client.listCaseEntities(ENTITY_CASE_ID, 'creditors');

    expect(stub.lastRequest().method).toBe('GET');
    expect(stub.lastRequest().url).toBe(`${BASE_URL}/v1/cases/${ENTITY_CASE_ID}/creditors`);
    expect(listed).toEqual([CREDITOR_RECORD]);
  });

  test('an empty collection is an empty array, not a failure', async () => {
    const stub = stubFetch(() => jsonResponse({ expenses: [] }, 200));
    const client = new InsolviaApiClient(BASE_URL, {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    await expect(client.listCaseEntities(ENTITY_CASE_ID, 'expenses')).resolves.toEqual([]);
  });

  test('a response missing the collection key is a contract failure', async () => {
    // The envelope is keyed by the collection's own name; `{"entities": []}`
    // would be a different server.
    const stub = stubFetch(() => jsonResponse({ entities: [] }, 200));
    const client = new InsolviaApiClient(BASE_URL, {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const error = asApiException(
      await rejection(client.listCaseEntities(ENTITY_CASE_ID, 'creditors')),
    );
    expect(error.message).toContain('creditors');
  });
});

describe('getCaseEntity / putCaseEntity / deleteCaseEntity', () => {
  test('GETs one record by id, with every segment encoded', async () => {
    const stub = stubFetch(() => jsonResponse(CREDITOR_RECORD, 200));
    const client = new InsolviaApiClient(BASE_URL, {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    await client.getCaseEntity('id with spaces/slash', 'creditors', 'entity/id');

    expect(stub.lastRequest().url).toBe(
      `${BASE_URL}/v1/cases/id%20with%20spaces%2Fslash/creditors/entity%2Fid`,
    );
  });

  test('PUTs the whole record and maps the 200', async () => {
    const renamed = { ...CREDITOR_RECORD, name: 'Renamed Bank' };
    const stub = stubFetch(() => jsonResponse(renamed, 200));
    const client = new InsolviaApiClient(BASE_URL, {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const saved = await client.putCaseEntity(ENTITY_CASE_ID, 'creditors', ENTITY_ID, {
      name: 'Renamed Bank',
      address: { line1: '1 Example Way' },
      provenance: {
        name: { source: 'staff_typed' },
        'address.line1': { source: 'staff_typed' },
      },
    });

    const seen = stub.lastRequest();
    expect(seen.method).toBe('PUT');
    expect(seen.url).toBe(`${BASE_URL}/v1/cases/${ENTITY_CASE_ID}/creditors/${ENTITY_ID}`);
    expect(saved).toEqual(renamed);
  });

  test('a PUT to an id the server never minted surfaces the 404', async () => {
    const stub = stubFetch(() =>
      jsonResponse({ error: 'NotFoundError', message: 'record not found' }, 404),
    );
    const client = new InsolviaApiClient(BASE_URL, {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const error = asApiException(
      await rejection(client.putCaseEntity(ENTITY_CASE_ID, 'creditors', 'never-minted', {})),
    );
    expect(error.statusCode).toBe(404);
  });

  test('DELETEs one record and resolves on the bodyless 204', async () => {
    const stub = stubFetch(() => new Response(null, { status: 204 }));
    const client = new InsolviaApiClient(BASE_URL, {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    await expect(
      client.deleteCaseEntity(ENTITY_CASE_ID, 'creditors', ENTITY_ID),
    ).resolves.toBeUndefined();
    expect(stub.lastRequest().method).toBe('DELETE');
    expect(stub.lastRequest().url).toBe(
      `${BASE_URL}/v1/cases/${ENTITY_CASE_ID}/creditors/${ENTITY_ID}`,
    );
  });

  test('no access token throws without calling fetch at all', async () => {
    const stub = stubFetch(() => jsonResponse(CREDITOR_RECORD, 200));
    const client = new InsolviaApiClient(BASE_URL, { fetch: stub.fetch });

    const error = asApiUnauthorizedException(
      await rejection(client.getCaseEntity(ENTITY_CASE_ID, 'creditors', ENTITY_ID)),
    );
    expect(stub.callCount()).toBe(0);
    expect(error.source).toBe('client');
  });
});

describe('getCreditorMatrix', () => {
  // The literal 200 body routes/creditor_matrix.py answers when every
  // creditor is mailable — content is the exact CRLF text of the .txt file.
  const GENERATED = {
    fileName: 'creditor-matrix.txt',
    creditorCount: 1,
    duplicatesOmitted: 0,
    problems: [],
    content: 'Example Bank\r\nPO Box 15168\r\nWilmington DE 19850\r\n',
  };

  test('GETs /v1/cases/{caseId}/creditor-matrix and maps the file outcome', async () => {
    const stub = stubFetch(() => jsonResponse(GENERATED, 200));
    const client = new InsolviaApiClient(BASE_URL, {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const matrix = await client.getCreditorMatrix(ENTITY_CASE_ID);

    const seen = stub.lastRequest();
    expect(seen.method).toBe('GET');
    expect(seen.url).toBe(`${BASE_URL}/v1/cases/${ENTITY_CASE_ID}/creditor-matrix`);
    expect(seen.headers.get('authorization')).toBe(`Bearer ${ACCESS_TOKEN}`);
    expect(seen.body).toBe('');

    expect(matrix).toEqual(GENERATED);
  });

  test('a refused matrix carries problems and NO content member at all', async () => {
    // The server omits `content` rather than sending null; the case-level
    // problem (no creditors) likewise omits `creditorId`.
    const stub = stubFetch(() =>
      jsonResponse(
        {
          fileName: 'creditor-matrix.txt',
          creditorCount: 0,
          duplicatesOmitted: 0,
          problems: [
            {
              creditorId: ENTITY_ID,
              field: 'address.state',
              message: 'A state is required.',
            },
            {
              field: 'creditors',
              message:
                'The case has no creditors — a matrix must list every creditor before it can be filed.',
            },
          ],
        },
        200,
      ),
    );
    const client = new InsolviaApiClient(BASE_URL, {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const matrix = await client.getCreditorMatrix(ENTITY_CASE_ID);

    expect('content' in matrix).toBe(false);
    expect(matrix.problems).toHaveLength(2);
    expect(matrix.problems[0]).toEqual({
      creditorId: ENTITY_ID,
      field: 'address.state',
      message: 'A state is required.',
    });
    expect(matrix.problems[1] !== undefined && 'creditorId' in matrix.problems[1]).toBe(false);
  });

  test('URL-encodes the caseId into the path', async () => {
    const stub = stubFetch(() => jsonResponse(GENERATED, 200));
    const client = new InsolviaApiClient(BASE_URL, {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    await client.getCreditorMatrix('id with spaces/slash');

    expect(new URL(stub.lastRequest().url).pathname).toBe(
      '/v1/cases/id%20with%20spaces%2Fslash/creditor-matrix',
    );
  });

  test('a foreign or unknown case surfaces the 404', async () => {
    const stub = stubFetch(() =>
      jsonResponse({ error: 'NotFoundError', message: 'case not found' }, 404),
    );
    const client = new InsolviaApiClient(BASE_URL, {
      fetch: stub.fetch,
      accessToken: () => ACCESS_TOKEN,
    });

    const error = asApiException(await rejection(client.getCreditorMatrix(ENTITY_CASE_ID)));
    expect(error.statusCode).toBe(404);
    expect(error.message).toContain('case not found');
  });

  test('no access token throws without calling fetch at all', async () => {
    const stub = stubFetch(() => jsonResponse(GENERATED, 200));
    const client = new InsolviaApiClient(BASE_URL, { fetch: stub.fetch });

    const error = asApiUnauthorizedException(
      await rejection(client.getCreditorMatrix(ENTITY_CASE_ID)),
    );
    expect(stub.callCount()).toBe(0);
    expect(error.source).toBe('client');
  });
});

describe('the case-collection enums', () => {
  test('CASE_COLLECTIONS mirrors core/case_collections.py, in order', () => {
    expect(CASE_COLLECTIONS).toEqual([
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
    ]);
  });

  test('the B101 enums mirror core/petitions.py, member for member', () => {
    expect(FEE_HANDLING).toEqual(['full', 'installments', 'waiver']);
    expect(BUSINESS_TYPES).toEqual([
      'health_care_business',
      'single_asset_real_estate',
      'stockbroker',
      'commodity_broker',
      'none_of_the_above',
    ]);
    expect(SMALL_BUSINESS_STATUSES).toEqual([
      'not_filing_under_chapter_11',
      'chapter_11_not_small_business',
      'chapter_11_small_business',
      'chapter_11_subchapter_v',
    ]);
    expect(DEBT_CHARACTERS).toEqual(['consumer', 'business', 'other']);
    expect(FILING_PROFESSIONAL_ROLES).toEqual(['attorney', 'bankruptcy_petition_preparer']);
    // The estimate bands are the form's own printed brackets; ten creditor
    // bands, twelve dollar bands shared by lines 19 and 20.
    expect(ESTIMATED_CREDITORS_BANDS).toHaveLength(10);
    expect(ESTIMATED_CREDITORS_BANDS[0]).toBe('1_49');
    expect(ESTIMATED_CREDITORS_BANDS[9]).toBe('more_than_100000');
    expect(ESTIMATED_DOLLAR_BANDS).toHaveLength(12);
    expect(ESTIMATED_DOLLAR_BANDS[0]).toBe('0_50000');
    expect(ESTIMATED_DOLLAR_BANDS[11]).toBe('more_than_50000000000');
  });

  test('SOFA_ENTRY_TYPES mirrors the dispatch table in core/sofa.py', () => {
    // Member for member: a type added to one side and not the other must
    // fail here, because the app's picker renders THIS list.
    expect(SOFA_ENTRY_TYPES).toEqual([
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
    ]);
  });

  test('CLAIM_CLASSES mirrors core/claims.py', () => {
    expect(CLAIM_CLASSES).toEqual(['secured', 'priority_unsecured', 'nonpriority_unsecured']);
  });
});
