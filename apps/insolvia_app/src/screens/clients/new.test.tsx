import { screen, userEvent, waitFor } from '@testing-library/react-native';
import { renderRouter } from 'expo-router/testing-library';

import type { AuthConfig } from '@/config/environment';
import { writeRefreshToken } from '@/session';
import { installFakeBrowser, jsonResponse, TEST_AUTH_CONFIG } from '@/session/testing';
import type { FakeBrowser } from '@/session/testing';

import { bodiesOf, clientFetch, firmClient, member } from './testing';
import type { Respond } from './testing';

let mockAuthConfig: AuthConfig | null = null;

jest.mock('@/config/environment', () => ({
  ...jest.requireActual('@/config/environment'),
  resolveAuthConfig: () => mockAuthConfig,
}));

const SAM = firmClient('00000000-0000-4000-8000-0000000c11a2', 'Sam', 'Sample');

/** `POST /v1/firm/clients` answers `created`; the record and the lists answer too. */
const adds =
  (created: () => Response = () => jsonResponse(201, SAM)): Respond =>
  (url, init) => {
    if (url.endsWith('/v1/firm/clients') && init?.method === 'POST') return created();
    if (url.endsWith('/v1/firm/clients')) return jsonResponse(200, { clients: [SAM] });
    if (url.endsWith('/cases')) return jsonResponse(200, { cases: [] });
    if (url.includes('/v1/firm/clients/')) return jsonResponse(200, SAM);
    if (url.includes('/v1/courts'))
      return jsonResponse(200, { releaseId: 'r', effectiveDate: '2026-09-24', districts: [] });
    return undefined;
  };

/**
 * `/clients/new` — "Add client", the front door (ADR 0022 / #354): the
 * person first, then optionally straight into their case.
 */
describe('adding a client', () => {
  let browser: FakeBrowser;
  const realFetch = globalThis.fetch;

  function signedIn(respond: Respond = adds(), me: unknown = member()) {
    const fetchMock = clientFetch(respond, me);
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    const router = renderRouter('src/app', { initialUrl: '/clients/new' });
    return { fetchMock, router };
  }

  const fill = async () => {
    const user = userEvent.setup();
    await user.type(await screen.findByLabelText('First name'), 'Sam');
    await user.type(screen.getByLabelText('Last name'), 'Sample');
    await user.type(screen.getByLabelText('City'), 'Orlando');
    return user;
  };

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

  it('adds the client, then goes straight to a new case for them', async () => {
    const { fetchMock, router } = signedIn();
    const user = await fill();

    await user.press(screen.getByRole('button', { name: 'Save and start a case' }));

    await waitFor(() => {
      expect(router.getPathname()).toBe('/cases/new');
    });
    expect(router.getSearchParams()).toMatchObject({ client: SAM.id });
    // Only what was typed: blank boxes are absent, never empty strings.
    expect(bodiesOf(fetchMock, 'POST', '/v1/firm/clients')).toEqual([
      { name: { given: 'Sam', surname: 'Sample' }, residence_address: { city: 'Orlando' } },
    ]);
  });

  it('adds the client and opens their record when no case is wanted yet', async () => {
    const { router } = signedIn();
    const user = await fill();

    await user.press(screen.getByRole('button', { name: 'Save client' }));

    await waitFor(() => {
      expect(router.getPathname()).toBe(`/clients/${SAM.id}`);
    });
  });

  it("puts the server's name rule under the last name, and stays on the form", async () => {
    const { router } = signedIn(
      adds(() =>
        jsonResponse(400, {
          error: 'ValidationError',
          fields: { name: 'Enter a surname or a first name.' },
        }),
      ),
    );
    await screen.findByLabelText('Last name');

    await userEvent.setup().press(screen.getByRole('button', { name: 'Save client' }));

    expect(await screen.findByText('Enter a surname or a first name.')).toBeTruthy();
    expect(router.getPathname()).toBe('/clients/new');
  });

  it('offers only "Save client" to a colleague who may not open cases', async () => {
    signedIn(adds(), member({ cases: 'view_only' }));
    await screen.findByLabelText('Last name');

    expect(screen.getByRole('button', { name: 'Save client' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Save and start a case' })).toBeNull();
  });

  it('tells a view-only colleague they cannot add clients, instead of a form the API would refuse', async () => {
    signedIn(adds(), member({ clients: 'view_only' }));

    expect(await screen.findByText(/not given you permission to add clients/)).toBeTruthy();
    expect(screen.queryByLabelText('Last name')).toBeNull();
  });
});
