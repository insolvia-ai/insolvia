import type { FirmMembership } from '@insolvia-ai/api-client';
import { useEffect, useRef, useState } from 'react';
import type { ReactNode } from 'react';

import { useApi } from '@/api/use-api';
import { StatusScreen } from '@/components/status-screen';

export interface RequireFirmProps {
  /**
   * Rendered with the resolved membership. A render prop rather than plain
   * children because the membership is the whole point: a screen that had to
   * fetch it again would make two requests for one fact, and the second could
   * disagree with the first.
   */
  children: (membership: FirmMembership) => ReactNode;
}

type State =
  | { readonly kind: 'loading' }
  | { readonly kind: 'ready'; readonly membership: FirmMembership }
  | { readonly kind: 'no-firm' }
  | { readonly kind: 'error' };

/**
 * The second guard: renders `children` only for somebody an actual firm has
 * added.
 *
 * ## Why this is not part of RequireSession
 *
 * "Signed in" and "in a firm" are genuinely different states, and the gap
 * between them is a place a real user sits. Self-signup is disabled on the
 * pool, so a Cognito account is created for somebody BEFORE — and, if the
 * membership write fails, without — a firm-user record. Every case endpoint
 * answers 403 for that person. `GET /v1/me` is the one that answers instead,
 * reporting an absent `firm`, and this component is what turns that into a
 * page rather than an error.
 *
 * Folding it into `RequireSession` would send them to sign-in, where signing in
 * again would work perfectly and change nothing — the loop a 401 for this state
 * would produce, and the reason the server answers 403.
 *
 * ## What it deliberately does not do
 *
 * It does not redirect. There is nowhere useful to send somebody with no firm:
 * every protected route is in the same position, and a redirect would either
 * loop or land them on a page equally unable to help. It renders the
 * explanation in place.
 *
 * It is also NOT a security control. The server refuses these callers on every
 * endpoint; this component exists so the refusal reads as an explanation rather
 * than an error, and a bug here costs clarity, not access.
 */
export function RequireFirm({ children }: RequireFirmProps) {
  const { call } = useApi();
  const [state, setState] = useState<State>({ kind: 'loading' });

  // One request per mount. Without this it refires on every state change it
  // causes — the same guard MePanel documents.
  const started = useRef(false);

  useEffect(() => {
    if (started.current) {
      return;
    }
    started.current = true;

    let cancelled = false;
    const settle = (next: State) => {
      if (!cancelled) {
        setState(next);
      }
    };

    const load = async () => {
      try {
        const result = await call((client) => client.me());
        if (!result.ok) {
          // The session ended; useApi already navigated. Staying in `loading`
          // is correct — this is about to unmount.
          return;
        }
        const { firm } = result.value;
        settle(firm === undefined ? { kind: 'no-firm' } : { kind: 'ready', membership: firm });
      } catch {
        settle({ kind: 'error' });
      }
    };

    void load();
    return () => {
      cancelled = true;
    };
  }, [call]);

  if (state.kind === 'ready') {
    return <>{children(state.membership)}</>;
  }
  if (state.kind === 'no-firm') {
    return (
      <StatusScreen
        title="You are not in a firm yet"
        message="Your account is signed in, but nobody has added you to a firm. Ask your firm’s administrator to add you — they can do it from the firm settings page."
      />
    );
  }
  if (state.kind === 'error') {
    return (
      <StatusScreen
        title="Could not load your firm"
        message="We could not reach the Insolvia API. Please try again in a moment."
      />
    );
  }
  return <StatusScreen title="Loading your firm" message="One moment." />;
}
