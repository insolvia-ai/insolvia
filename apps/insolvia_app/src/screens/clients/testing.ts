/**
 * Fixtures shared by the three client screens' suites — never imported by
 * app code. Shapes are the API's wire shapes (`firm_client_json`,
 * `case_json`, `/v1/me`), so a suite exercises the real api-client decoding.
 */
import { jsonResponse, tokenEndpointResponse } from '@/session/testing';

export const ALICE = '00000000-0000-4000-8000-00000000a11c';

type Level = 'hidden' | 'view_only' | 'add_edit';

/** A `/v1/me` body whose firm grants `clients` and `cases` as given. */
export function member({
  clients = 'add_edit',
  cases = 'add_edit',
}: { clients?: Level; cases?: Level } = {}) {
  return {
    subject: ALICE,
    username: null,
    clientId: 'exampleappclientid000000',
    scopes: [],
    expiresAt: null,
    firm: {
      id: '00000000-0000-4000-8000-00000000f18a',
      name: 'Example & Partners',
      role: 'paralegal',
      firstName: 'Alice',
      lastName: 'Attorney',
      displayName: 'Alice Attorney',
      isAdmin: false,
      accessAllCases: false,
      permissions: { clients, cases, intake: 'add_edit' },
      defaultCourt: null,
      defaultDivision: null,
      defaultChapter: null,
      letterhead: null,
      signatureBlock: null,
    },
  };
}

/** A firm client; `extra` adds or overrides wire fields. */
export function firmClient(
  id: string,
  given: string,
  surname: string,
  extra: Record<string, unknown> = {},
) {
  return {
    id,
    status: 'active',
    name: { given, surname },
    created_at: '2026-09-27T09:00:00.000000Z',
    updated_at: '2026-09-27T09:00:00.000000Z',
    created_by: ALICE,
    ...extra,
  };
}

export function caseBody(id: string, extra: Record<string, unknown> = {}) {
  return {
    id,
    createdBy: ALICE,
    chapter: 7,
    district: 'Middle District of Florida',
    court: 'flmb',
    division: 'tampa',
    status: 'intake',
    createdAt: '2026-08-04T10:00:00.000000Z',
    updatedAt: '2026-08-04T10:00:00.000000Z',
    ...extra,
  };
}

/** Answers one request, or `undefined` to let the defaults answer it. */
export type Respond = (url: string, init: RequestInit | undefined) => Response | undefined;

/**
 * A fetch that answers the token endpoint and `/v1/me` itself and hands
 * everything else to `respond`. Anything `respond` declines is an error —
 * an unexpected request should fail a test, not hang it.
 */
export function clientFetch(respond: Respond, me: unknown = member()) {
  return jest.fn((url: string, init?: RequestInit) => {
    if (url.includes('/oauth2/token')) return Promise.resolve(tokenEndpointResponse());
    const answer = respond(url, init);
    if (answer !== undefined) return Promise.resolve(answer);
    if (url.includes('/v1/me')) return Promise.resolve(jsonResponse(200, me));
    return Promise.reject(new Error(`unexpected request to ${url}`));
  });
}

/** The JSON bodies of every request matching `method` and `fragment`. */
export function bodiesOf(fetchMock: jest.Mock, method: string, fragment: string): unknown[] {
  return (fetchMock.mock.calls as [string, RequestInit | undefined][])
    .filter(([url, init]) => url.includes(fragment) && init?.method === method)
    .map(([, init]) => JSON.parse(String(init?.body)) as unknown);
}
