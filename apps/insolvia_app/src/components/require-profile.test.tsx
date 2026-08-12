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

const ALICE = '00000000-0000-4000-8000-00000000a11c';

/**
 * A `/v1/me` body. `firstName`/`lastName` default to a complete name; pass
 * `''` for either to produce the state this guard exists for — which is
 * exactly what a row derived from a single-token legacy display name looks
 * like coming back from the server.
 */
function principal(firstName = 'Alice', lastName = 'Attorney', firm = true) {
  const body: Record<string, unknown> = {
    subject: ALICE,
    username: null,
    clientId: 'exampleappclientid000000',
    scopes: [],
    expiresAt: null,
  };
  if (firm) {
    body.firm = {
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
    };
  }
  return body;
}

/**
 * The first-run name gate.
 *
 * WHAT MAKES THIS WORTH TESTING AT ALL: it is the only component in the app
 * that can refuse to render a screen the user asked for, on every protected
 * route at once. Both directions matter — a gate that never lifts locks
 * everybody out of the product, and a gate that never appears silently drops
 * the requirement it exists to enforce.
 *
 * It is mounted inside `RequireSession`, so these render real routes through
 * the real router rather than the component in isolation: what is being
 * asserted is that the wiring covers the routes, not just that the branch
 * returns the right element.
 */
describe('the first-run name gate', () => {
  let browser: FakeBrowser;
  const realFetch = globalThis.fetch;

  function signedIn(
    handlers: Readonly<Record<string, () => Response>>,
    patchMe?: () => Response,
    initialUrl = '/',
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
    writeRefreshToken('refresh-token');
  });

  afterEach(() => {
    globalThis.fetch = realFetch;
    browser.restore();
    jest.clearAllMocks();
  });

  it('asks for a name when the surname is missing', async () => {
    // The migration's shape: "Cher" splits to a first name and nothing else,
    // because we genuinely do not know that person's surname.
    signedIn({ '/v1/me': () => jsonResponse(200, principal('Cher', '')) });

    expect(await screen.findByRole('heading', { name: 'Tell us your name' })).toBeTruthy();
    // And the screen it was covering is not rendered behind it.
    expect(screen.queryByRole('heading', { name: 'Your case workspace' })).toBeNull();
  });

  it('asks for a name when the first name is missing', async () => {
    signedIn({ '/v1/me': () => jsonResponse(200, principal('', 'Attorney')) });

    expect(await screen.findByRole('heading', { name: 'Tell us your name' })).toBeTruthy();
  });

  it('prefills the half that is already known', async () => {
    // Making somebody retype a name the screen is already showing them would
    // be rude, and would invite them to change a value that was right.
    signedIn({ '/v1/me': () => jsonResponse(200, principal('Cher', '')) });
    await screen.findByRole('heading', { name: 'Tell us your name' });

    expect(screen.getByDisplayValue('Cher')).toBeTruthy();
  });

  it('lets a complete name straight through', async () => {
    signedIn({ '/v1/me': () => jsonResponse(200, principal()) });

    expect(await screen.findByRole('heading', { name: 'Your case workspace' })).toBeTruthy();
    expect(screen.queryByRole('heading', { name: 'Tell us your name' })).toBeNull();
  });

  it('covers a deep route, not just the home screen', async () => {
    // The wiring assertion. Every protected route composes RequireSession, so
    // the gate rides along on all of them — including one nobody thought about
    // when the gate was written.
    signedIn({ '/v1/me': () => jsonResponse(200, principal('Cher', '')) }, undefined, '/cases');

    expect(await screen.findByRole('heading', { name: 'Tell us your name' })).toBeTruthy();
  });

  it('does not gate a signed-in caller who is in no firm', async () => {
    // There is no row to write a name to, and RequireFirm already owns the
    // explanation for that state — two screens competing to explain it would
    // be worse than either alone.
    signedIn({ '/v1/me': () => jsonResponse(200, principal('', '', false)) });

    expect(await screen.findByRole('heading', { name: 'Your case workspace' })).toBeTruthy();
    expect(screen.queryByRole('heading', { name: 'Tell us your name' })).toBeNull();
  });

  it('does not gate when /v1/me could not be read', async () => {
    // Nothing is proven by a failed request, and this is not a security
    // control — the server enforces nothing on a name. Refusing to render the
    // app because a request failed would be the worse answer.
    signedIn({ '/v1/me': () => jsonResponse(500, { error: 'ServerError' }) });

    expect(await screen.findByRole('heading', { name: 'Your case workspace' })).toBeTruthy();
  });

  it('saves both halves and lifts the gate without a reload', async () => {
    // THE ONE THAT MATTERS MOST. PATCH /v1/me answers with the same body GET
    // does, and the screen hands it to MeProvider — so the guard re-reads a
    // complete name and falls through to the route originally asked for. If
    // the provider's cache were not updated, the gate would re-render itself
    // immediately after a successful save and the user would be stuck.
    const fetchMock = signedIn({ '/v1/me': () => jsonResponse(200, principal('Cher', '')) }, () =>
      jsonResponse(200, principal('Cher', 'Bono')),
    );
    await screen.findByRole('heading', { name: 'Tell us your name' });

    const user = userEvent.setup();
    await user.type(screen.getByLabelText('Last name'), 'Bono');
    await user.press(screen.getByRole('button', { name: 'Continue' }));

    expect(await screen.findByRole('heading', { name: 'Your case workspace' })).toBeTruthy();

    const patch = fetchMock.mock.calls.find(
      ([url, init]) => url.includes('/v1/me') && init?.method === 'PATCH',
    );
    expect(JSON.parse(String(patch?.[1]?.body))).toEqual({
      firstName: 'Cher',
      lastName: 'Bono',
    });
  });

  it('renders the server’s own message on a rejected half', async () => {
    signedIn({ '/v1/me': () => jsonResponse(200, principal('Cher', '')) }, () =>
      jsonResponse(400, {
        error: 'ValidationError',
        fields: { lastName: 'A name is required.' },
      }),
    );
    await screen.findByRole('heading', { name: 'Tell us your name' });

    await userEvent.setup().press(screen.getByRole('button', { name: 'Continue' }));

    expect(await screen.findByText('A name is required.')).toBeTruthy();
  });

  it('keeps sign-out reachable, so the gate is not a trap', async () => {
    // It renders inside AppShell for exactly this reason: somebody who cannot
    // or will not answer needs a way out that is not closing the tab.
    signedIn({ '/v1/me': () => jsonResponse(200, principal('Cher', '')) });
    await screen.findByRole('heading', { name: 'Tell us your name' });

    await userEvent.setup().press(screen.getByRole('button', { name: 'Account menu' }));
    expect(screen.getByRole('menuitem', { name: 'Sign out' })).toBeTruthy();
  });

  it('gives the gate screen exactly one level-1 heading', async () => {
    // `page-has-heading-one` is a required axe check, and this screen replaces
    // whichever h1 the guarded screen would have rendered.
    signedIn({ '/v1/me': () => jsonResponse(200, principal('Cher', '')) });
    await screen.findByRole('heading', { name: 'Tell us your name' });

    const levelOnes = screen
      .getAllByRole('heading')
      .filter((node) => node.props['aria-level'] === 1);
    expect(levelOnes).toHaveLength(1);
  });
});
