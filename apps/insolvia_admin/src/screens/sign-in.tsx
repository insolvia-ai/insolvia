import { useEffect, useRef, useState } from 'react';
import { Card } from '@insolvia-ai/design-system';

import { config } from '../config/environment';
import { loadGoogleIdentity } from '../session/google-identity';
import { useSession } from '../session/session';

/**
 * Google's own button and nothing else — there is no password form here and
 * there must never be one (the same rule as the app's sign-in screen): staff
 * authenticate with their Insolvia Google Workspace account, in Google's own
 * UI. The button's callback hands the session an ID token directly; no
 * redirect leaves this page, so `RequireStaff` simply re-renders the guarded
 * screen the moment the session flips to signed-in.
 */
export function SignInScreen() {
  const session = useSession();
  const [error, setError] = useState<string | null>(null);
  const host = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    let cancelled = false;
    void loadGoogleIdentity()
      .then((identity) => {
        if (cancelled || host.current === null) return;
        identity.initialize({
          client_id: config.googleClientId,
          // A HINT to Google's account chooser; the ENFORCEMENT is the
          // client being Internal on Google's side and the service's hd
          // claim check on ours.
          hd: 'insolvia.ai',
          callback: (response) => {
            try {
              session.acceptCredential(response.credential);
            } catch {
              setError('Sign-in could not be completed. Try again.');
            }
          },
        });
        // replaceChildren: strict mode re-runs this effect in dev, and a
        // second renderButton into a non-empty host would stack two buttons.
        host.current.replaceChildren();
        identity.renderButton(host.current, { theme: 'outline', size: 'large' });
      })
      .catch(() => {
        if (!cancelled) {
          setError('Could not load Google sign-in. Check your connection and reload the page.');
        }
      });
    return () => {
      cancelled = true;
    };
  }, [session]);

  return (
    <div className="flex min-h-screen items-center justify-center px-6">
      <Card.Root className="w-full max-w-sm p-8 text-center">
        <h1 className="mb-2 font-serif text-xl font-bold">Insolvia Admin</h1>
        <p className="mb-6 text-sm text-muted">
          Firm provisioning and administration. Sign in with your Insolvia Google account.
        </p>
        <div ref={host} className="flex justify-center" />
        {error === null ? null : (
          <p role="alert" className="mt-4 text-sm text-danger">
            {error}
          </p>
        )}
      </Card.Root>
    </div>
  );
}
