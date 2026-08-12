import { screen, userEvent } from '@testing-library/react-native';
import { renderRouter } from 'expo-router/testing-library';

import type { AuthConfig } from '@/config/environment';
import { writeRefreshToken } from '@/session';
import {
  installFakeBrowser,
  jsonResponse,
  routeFetch,
  TEST_AUTH_CONFIG,
  TEST_EMAIL,
  tokenEndpointResponse,
} from '@/session/testing';
import type { FakeBrowser } from '@/session/testing';

let mockAuthConfig: AuthConfig | null = null;

jest.mock('@/config/environment', () => ({
  ...jest.requireActual('@/config/environment'),
  resolveAuthConfig: () => mockAuthConfig,
}));

const ALICE = '00000000-0000-4000-8000-00000000a11c';

function membership(firstName = 'Alice', lastName = 'Attorney') {
  return {
    subject: ALICE,
    username: null,
    clientId: 'exampleappclientid000000',
    scopes: [],
    expiresAt: null,
    firm: {
      id: '00000000-0000-4000-8000-00000000f18a',
      name: 'Example & Partners',
      role: 'attorney',
      firstName,
      lastName,
      displayName: [firstName, lastName].filter(Boolean).join(' '),
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
}

/**
 * `/account` — your own name, and the email you sign in with.
 *
 * The membership above deliberately holds NO administration permission: the
 * point of the endpoint this screen calls is that renaming yourself needs
 * none, and a fixture with admin rights would let a permission gate sneak in
 * unnoticed.
 */
describe('the account screen', () => {
  let browser: FakeBrowser;
  const realFetch = globalThis.fetch;

  function signedIn(
    handlers: Readonly<Record<string, () => Response>>,
    patchMe?: () => Response,
    initialUrl = '/account',
  ) {
    const route = routeFetch({ '/oauth2/token': tokenEndpointResponse, ...handlers });
    const fetchMock = jest.fn((url: string, init?: RequestInit) =>
      patchMe !== undefined && init?.method === 'PATCH' && url.includes('/v1/me')
        ? patchMe()
        : route(url),
    );
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    renderRouter('src/app', { initialUrl });
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

  it('prefills the name from the membership and shows the sign-in email read-only', async () => {
    signedIn({ '/v1/me': () => jsonResponse(200, membership()) });

    expect(await screen.findByDisplayValue('Alice')).toBeTruthy();
    expect(screen.getByDisplayValue('Attorney')).toBeTruthy();
    // The address comes from the ID token, not /v1/me (ADR 0007), and there
    // is no input for it — only the statement that it is the sign-in name.
    // Twice: once in the shell's AccountBar, once in the read-only section.
    expect(screen.getAllByText(TEST_EMAIL)).toHaveLength(2);
    expect(screen.getByText(/can’t be changed/i)).toBeTruthy();
  });

  it('saves exactly the two name halves, and re-renders from the server’s echo', async () => {
    const fetchMock = signedIn({ '/v1/me': () => jsonResponse(200, membership()) }, () =>
      jsonResponse(200, membership('Alice', 'Corrected')),
    );
    await screen.findByDisplayValue('Attorney');

    const user = userEvent.setup();
    const last = screen.getByLabelText('Last name');
    await user.clear(last);
    await user.type(last, 'Corrected');
    await user.press(screen.getByRole('button', { name: 'Save name' }));

    expect(await screen.findByText('Your name is saved.')).toBeTruthy();
    // The rendered value is the SERVER's echo, not trust in local state.
    expect(screen.getByDisplayValue('Corrected')).toBeTruthy();

    const patch = fetchMock.mock.calls.find(
      ([url, init]) => url.includes('/v1/me') && init?.method === 'PATCH',
    );
    expect(patch).toBeTruthy();
    // The whole writable surface. A `displayName` key here would be the client
    // sending a field the server derives and never accepts.
    expect(JSON.parse(String(patch?.[1]?.body))).toEqual({
      firstName: 'Alice',
      lastName: 'Corrected',
    });
  });

  it('renders the server’s own message on a rejected name', async () => {
    // The server owns validation (ADR 0001) — the message is its literal
    // FieldValidationError body, shown as-is rather than restated.
    signedIn({ '/v1/me': () => jsonResponse(200, membership()) }, () =>
      jsonResponse(400, {
        error: 'ValidationError',
        fields: { firstName: 'A name is required.' },
      }),
    );
    await screen.findByDisplayValue('Alice');

    const user = userEvent.setup();
    await user.clear(screen.getByLabelText('First name'));
    await user.press(screen.getByRole('button', { name: 'Save name' }));

    expect(await screen.findByText('A name is required.')).toBeTruthy();
  });

  it('is reachable from the shell’s Account link', async () => {
    signedIn({ '/v1/me': () => jsonResponse(200, membership()) }, undefined, '/');
    await screen.findByText(TEST_EMAIL);

    await userEvent.setup().press(screen.getByRole('link', { name: 'Account' }));

    expect(await screen.findByRole('heading', { name: 'Your account' })).toBeTruthy();
  });

  it('gives the page exactly one level-1 heading', async () => {
    signedIn({ '/v1/me': () => jsonResponse(200, membership()) });
    await screen.findByRole('heading', { name: 'Your account' });

    const levelOnes = screen
      .getAllByRole('heading')
      .filter((node) => node.props['aria-level'] === 1);
    expect(levelOnes).toHaveLength(1);
  });
});
