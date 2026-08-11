/**
 * The sign-in screen and Google's button, with the script loader mocked —
 * the assertions that are checkable nowhere else: the button is initialized
 * for THIS environment's client id with the Workspace hint, its credential
 * reaches the session, and a script that will not load is reported rather
 * than leaving a silent dead card.
 */

import { cleanup, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { CredentialResponse, GoogleIdentityApi } from '../session/google-identity';
import { loadGoogleIdentity } from '../session/google-identity';
import { SessionProvider, useSession } from '../session/session';
import { SignInScreen } from './sign-in';

vi.mock('../session/google-identity', () => ({
  loadGoogleIdentity: vi.fn(),
}));

const loadMock = vi.mocked(loadGoogleIdentity);

function fakeJwt(claims: Record<string, unknown>): string {
  const encode = (value: unknown) =>
    btoa(JSON.stringify(value)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
  return `${encode({ alg: 'RS256' })}.${encode(claims)}.sig`;
}

function fakeIdentity() {
  let credentialCallback: ((response: CredentialResponse) => void) | null = null;
  const api: GoogleIdentityApi = {
    initialize: vi.fn((config) => {
      credentialCallback = config.callback;
    }),
    renderButton: vi.fn((parent: HTMLElement) => {
      const marker = document.createElement('div');
      marker.textContent = 'Google button';
      parent.appendChild(marker);
    }),
  };
  return {
    api,
    signInAs(claims: Record<string, unknown>) {
      if (credentialCallback === null) throw new Error('initialize never ran');
      credentialCallback({ credential: fakeJwt(claims) });
    },
  };
}

function SignedInProbe() {
  const session = useSession();
  if (!session.signedIn) return <SignInScreen />;
  return <p>signed in as {session.email}</p>;
}

beforeEach(() => {
  vi.clearAllMocks();
});

// Without vitest `globals`, testing-library cannot register its automatic
// cleanup — two renders would share one DOM and the second test finds the
// first test's alert.
afterEach(cleanup);

describe('the sign-in screen', () => {
  it("initializes Google's button for this environment's client, hd-hinted", async () => {
    const identity = fakeIdentity();
    loadMock.mockResolvedValue(identity.api);

    render(
      <SessionProvider>
        <SignInScreen />
      </SessionProvider>,
    );

    await screen.findByText('Google button');
    expect(identity.api.initialize).toHaveBeenCalledWith(
      expect.objectContaining({
        client_id: expect.stringContaining('.apps.googleusercontent.com'),
        hd: 'insolvia.ai',
      }),
    );
    // auto_select is deliberately never set: silent re-sign-in is a
    // decision, not a default, for a high-privilege internal tool.
    const config = vi.mocked(identity.api.initialize).mock.calls[0]?.[0];
    expect(config).not.toHaveProperty('auto_select');
  });

  it("hands the button's credential to the session, and the guard re-renders", async () => {
    const identity = fakeIdentity();
    loadMock.mockResolvedValue(identity.api);

    render(
      <SessionProvider>
        <SignedInProbe />
      </SessionProvider>,
    );
    await screen.findByText('Google button');

    identity.signInAs({
      email: 'operator@insolvia.ai',
      exp: Math.floor(Date.now() / 1000) + 3600,
    });

    expect(await screen.findByText('signed in as operator@insolvia.ai')).toBeInTheDocument();
  });

  it('reports a credential the session refuses, rather than dying silently', async () => {
    const identity = fakeIdentity();
    loadMock.mockResolvedValue(identity.api);

    render(
      <SessionProvider>
        <SignInScreen />
      </SessionProvider>,
    );
    await screen.findByText('Google button');

    identity.signInAs({ email: 'operator@insolvia.ai' }); // no exp

    expect(await screen.findByRole('alert')).toHaveTextContent(/could not be completed/i);
  });

  it('reports a script that will not load', async () => {
    loadMock.mockRejectedValue(new Error('blocked'));

    render(
      <SessionProvider>
        <SignInScreen />
      </SessionProvider>,
    );

    await waitFor(() => {
      expect(screen.getByRole('alert')).toHaveTextContent(/could not load/i);
    });
  });
});
