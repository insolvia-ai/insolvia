import { screen, userEvent } from '@testing-library/react-native';
import { renderRouter } from 'expo-router/testing-library';

import type { AuthConfig } from '@/config/environment';
import { writeRefreshToken } from '@/session';
import {
  installFakeBrowser,
  jsonResponse,
  routeFetch,
  TEST_AUTH_CONFIG,
  tokenEndpointResponse,
} from '@/session/testing';
import type { FakeBrowser } from '@/session/testing';

let mockAuthConfig: AuthConfig | null = null;

jest.mock('@/config/environment', () => ({
  ...jest.requireActual('@/config/environment'),
  resolveAuthConfig: () => mockAuthConfig,
}));

const FIRM_ID = '00000000-0000-4000-8000-00000000f18a';
const ALICE = '00000000-0000-4000-8000-00000000a11c';
const BOB = '00000000-0000-4000-8000-00000000b0b0';

const ALL_ADD_EDIT = {
  cases: 'add_edit',
  intake: 'add_edit',
  documents: 'add_edit',
  extraction_review: 'add_edit',
  firm_administration: 'add_edit',
};

function membership(overrides: Record<string, unknown> = {}) {
  return {
    subject: ALICE,
    username: null,
    clientId: 'exampleappclientid000000',
    scopes: [],
    expiresAt: null,
    firm: {
      id: FIRM_ID,
      name: 'Example & Partners',
      role: 'attorney',
      firstName: 'Alice',
      lastName: 'Admin',
      displayName: 'Alice Admin',
      isAdmin: true,
      accessAllCases: false,
      permissions: ALL_ADD_EDIT,
      ...overrides,
    },
  };
}

const BOB_RECORD = {
  subject: BOB,
  email: 'bob@example.test',
  firstName: 'Bob',
  lastName: 'Paralegal',
  displayName: 'Bob Paralegal',
  role: 'paralegal',
  isAdmin: false,
  accessAllCases: false,
  permissions: { ...ALL_ADD_EDIT, firm_administration: 'hidden' },
  status: 'active',
  createdAt: '2026-08-04T10:00:00.000Z',
  updatedAt: '2026-08-04T10:00:00.000Z',
};

const FIRM_RECORD = {
  id: FIRM_ID,
  name: 'Example & Partners',
  status: 'active',
  createdAt: '2026-01-05T09:00:00.000Z',
  updatedAt: '2026-08-01T12:00:00.000Z',
};

/**
 * `/firm` — the firm's own people.
 *
 * Rendered through the real router, so a route file that moved or stopped
 * compiling fails here. The screen sits behind two guards, and the second one
 * (`RequireFirm`) is why every test has to answer `/v1/me` before anything
 * else happens.
 *
 * What is asserted is the wiring the server cannot check: that the screen sends
 * what the API expects, that a 409 is reported as the firm-state problem it is
 * rather than as a permission failure, and that per-colleague controls carry
 * accessible names a screen reader can tell apart.
 */
describe('the firm screen', () => {
  let browser: FakeBrowser;
  const realFetch = globalThis.fetch;

  function signedIn(
    handlers: Readonly<Record<string, () => Response>>,
    patchFirm?: () => Response,
  ) {
    // `/v1/firm` LAST: routeFetch matches by substring in insertion order, so
    // registered any earlier it would swallow `/v1/firm/users` too. The PATCH
    // to the same URL as the GET is told apart by method, which routeFetch
    // cannot see — hence the wrapper.
    const route = routeFetch({
      '/oauth2/token': tokenEndpointResponse,
      ...handlers,
      '/v1/firm': () => jsonResponse(200, FIRM_RECORD),
    });
    const fetchMock = jest.fn((url: string, init?: RequestInit) =>
      patchFirm !== undefined && init?.method === 'PATCH' && url.endsWith('/v1/firm')
        ? Promise.resolve(patchFirm())
        : route(url),
    );
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    renderRouter('src/app', { initialUrl: '/firm' });
    return fetchMock;
  }

  beforeEach(() => {
    mockAuthConfig = TEST_AUTH_CONFIG;
    browser = installFakeBrowser();
    writeRefreshToken('stored-refresh-token');
  });

  afterEach(() => {
    globalThis.fetch = realFetch;
    browser.restore();
    jest.clearAllMocks();
  });

  it('lists the firm’s people under the firm’s name', async () => {
    signedIn({
      '/v1/me': () => jsonResponse(200, membership()),
      '/v1/firm/users': () => jsonResponse(200, { users: [BOB_RECORD] }),
    });

    expect(await screen.findByText('Bob Paralegal')).toBeTruthy();
    expect(screen.getByRole('heading', { name: 'Example & Partners' })).toBeTruthy();
  });

  it('tells somebody who has no firm what to do, rather than erroring', async () => {
    // THE STATE THIS GUARD EXISTS FOR. Self-signup is disabled, so an account
    // exists before a membership does — and every case endpoint answers 403
    // for that person. A sign-in redirect would loop: signing in again works
    // perfectly and changes nothing.
    signedIn({
      '/v1/me': () =>
        jsonResponse(200, {
          subject: ALICE,
          username: null,
          clientId: 'exampleappclientid000000',
          scopes: [],
          expiresAt: null,
        }),
    });

    expect(await screen.findByText(/not in a firm yet/i)).toBeTruthy();
    expect(screen.getByText(/ask your firm/i)).toBeTruthy();
  });

  it('explains rather than lists when the caller may not administer', async () => {
    // Seeing every case and managing the firm's people are separate axes. A
    // supervising attorney has the first without the second.
    signedIn({
      '/v1/me': () =>
        jsonResponse(
          200,
          membership({
            isAdmin: false,
            accessAllCases: true,
            permissions: { ...ALL_ADD_EDIT, firm_administration: 'hidden' },
          }),
        ),
    });

    expect(await screen.findByText(/administrator’s job/i)).toBeTruthy();
    expect(screen.queryByRole('heading', { name: 'Add a colleague' })).toBeNull();
  });

  it('sends the colleague the form describes, and nothing it does not', async () => {
    const fetchMock = signedIn({
      '/v1/me': () => jsonResponse(200, membership()),
      '/v1/firm/users': () => jsonResponse(200, { users: [] }),
    });
    await screen.findByRole('heading', { name: 'Add a colleague' });

    const user = userEvent.setup();
    await user.type(screen.getByLabelText('First name'), 'Dana');
    await user.type(screen.getByLabelText('Last name'), 'Attorney');
    await user.type(screen.getByLabelText('Work email'), 'dana@example.test');
    await user.press(screen.getByRole('radio', { name: 'Attorney' }));
    await user.press(screen.getByRole('button', { name: 'Add colleague' }));

    const post = fetchMock.mock.calls.find(
      ([url, init]) => url.includes('/v1/firm/users') && init?.method === 'POST',
    );
    expect(post).toBeTruthy();
    // Absent optionals are OMITTED, not sent as null: the server treats an
    // absent key as "use the role default" and a present one as an instruction.
    expect(JSON.parse(String(post?.[1]?.body))).toEqual({
      email: 'dana@example.test',
      firstName: 'Dana',
      lastName: 'Attorney',
      role: 'attorney',
    });
  });

  it('reports the last-administrator refusal as a firm-state problem', async () => {
    // NOT "you may not do this" — it is being said to the one person who may.
    // Self-signup is off, so a firm that loses its last administrator cannot
    // appoint one and needs us to fix it by hand.
    signedIn({
      '/v1/me': () => jsonResponse(200, membership()),
      // MORE SPECIFIC FIRST. `routeFetch` matches by substring in insertion
      // order, so the collection key would otherwise swallow the item key and
      // the PATCH would quietly get the 200 the list returns.
      [`/v1/firm/users/${BOB}`]: () =>
        jsonResponse(409, {
          error: 'ConflictError',
          message: 'a firm must keep at least one active administrator',
        }),
      '/v1/firm/users': () => jsonResponse(200, { users: [BOB_RECORD] }),
    });
    await screen.findByText('Bob Paralegal');

    await userEvent.setup().press(screen.getByRole('button', { name: 'Make administrator' }));

    expect(await screen.findByText(/at least one active administrator/i)).toBeTruthy();
  });

  it('names every per-colleague control after the colleague', async () => {
    // Without this a screen reader hears "Documents" once per person and
    // cannot tell whose it is — the WCAG 2.4.4 problem a list of identical
    // links has, moved to a list of identical selects.
    signedIn({
      '/v1/me': () => jsonResponse(200, membership()),
      '/v1/firm/users': () => jsonResponse(200, { users: [BOB_RECORD] }),
    });
    await screen.findByText('Bob Paralegal');

    // By ROLE and name, not by label alone: the label alone matches more than
    // one node, because the design system's Select puts `aria-label` on both
    // its root and its combobox (a defect this test surfaced — the root should
    // not carry it). Querying the role is the correct assertion regardless: it
    // is the combobox a screen reader lands on.
    expect(screen.getByRole('combobox', { name: 'Documents for Bob Paralegal' })).toBeTruthy();
    expect(screen.getByRole('combobox', { name: 'Cases for Bob Paralegal' })).toBeTruthy();
  });

  it('renames the firm, and the page title follows the server’s echo', async () => {
    const fetchMock = signedIn(
      {
        '/v1/me': () => jsonResponse(200, membership()),
        '/v1/firm/users': () => jsonResponse(200, { users: [] }),
      },
      () => jsonResponse(200, { ...FIRM_RECORD, name: 'Example, LLP' }),
    );
    await screen.findByDisplayValue('Example & Partners');

    const user = userEvent.setup();
    const name = screen.getByLabelText('Firm name');
    await user.clear(name);
    await user.type(name, 'Example, LLP');
    await user.press(screen.getByRole('button', { name: 'Save firm name' }));

    expect(await screen.findByText('Your firm’s name is saved.')).toBeTruthy();
    expect(screen.getByRole('heading', { name: 'Example, LLP' })).toBeTruthy();

    const patch = fetchMock.mock.calls.find(
      ([url, init]) => url.endsWith('/v1/firm') && init?.method === 'PATCH',
    );
    expect(patch).toBeTruthy();
    // Name only. A `status` here would be the client offering a
    // self-suspension the server refuses to parse.
    expect(JSON.parse(String(patch?.[1]?.body))).toEqual({ name: 'Example, LLP' });
  });

  it('shows the record read-only to a viewer, with no rename control', async () => {
    signedIn({
      '/v1/me': () =>
        jsonResponse(
          200,
          membership({
            isAdmin: false,
            permissions: { ...ALL_ADD_EDIT, firm_administration: 'view_only' },
          }),
        ),
      '/v1/firm/users': () => jsonResponse(200, { users: [BOB_RECORD] }),
    });

    expect(await screen.findByText(/Renaming the firm is an administrator’s job/)).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Save firm name' })).toBeNull();
  });

  it('gives the page exactly one level-1 heading', async () => {
    signedIn({
      '/v1/me': () => jsonResponse(200, membership()),
      '/v1/firm/users': () => jsonResponse(200, { users: [] }),
    });
    await screen.findByRole('heading', { name: 'Example & Partners' });

    const levelOnes = screen
      .getAllByRole('heading')
      .filter((node) => node.props['aria-level'] === 1);
    expect(levelOnes).toHaveLength(1);
  });
});
