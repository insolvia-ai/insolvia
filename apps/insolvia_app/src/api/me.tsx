import type { FirmMembership, Principal } from '@insolvia-ai/api-client';
import type { ReactNode } from 'react';
import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useRef,
  useState,
} from 'react';

import { useApi } from '@/api/use-api';
import { useSession } from '@/session';

export type MeState =
  | { readonly kind: 'loading' }
  | { readonly kind: 'ready'; readonly principal: Principal }
  | { readonly kind: 'error'; readonly message: string };

const MeContext = createContext<MeState>({ kind: 'loading' });

/**
 * The write half of this provider, deliberately a SECOND context.
 *
 * Folding `adopt` into {@link MeContext}'s value would give that value a new
 * identity on every state change, re-rendering every `useMe()` consumer — the
 * shell's navigation on every screen — for a function that never changes.
 */
interface MeActions {
  /**
   * Replace the cached principal with one the server just answered with.
   *
   * `PATCH /v1/me` returns the identical body `GET /v1/me` produces, so a
   * caller that renamed themselves already holds the fresh answer and this
   * costs no request. Without it the cache would stay stale for the whole
   * session — invisible while only `permissions` was read from it, and not
   * invisible at all now that a screen can gate on the name.
   */
  readonly adopt: (principal: Principal) => void;
}

const MeActionsContext = createContext<MeActions>({ adopt: () => {} });

/**
 * One `GET /v1/me` per signed-in session, held at the root for everything
 * that wants the caller's identity as CHROME rather than as a gate: the
 * shell's Firm nav entry needs `firm_administration` on every screen
 * (issue #218), and `MePanel` renders the same answer on the home screen.
 *
 * This is deliberately NOT what `RequireFirm` reads, and it keeps its own
 * per-mount fetch on purpose: a guard admitting somebody to a screen should
 * decide on a fresh answer, while a nav link is a courtesy — `permits` is
 * never a control — where a session-stale value costs nothing (the screen's
 * own fallback still answers for a demoted admin). One fetch per session
 * here versus one per visit there is the freshness each caller actually
 * needs.
 *
 * The 401 policy is {@link useApi}'s: a `call` that ends the session has
 * already navigated to sign-in or signed out, so this provider simply stays
 * `loading` for the moment it takes the router to leave. Anything else —
 * network, a 5xx — is the one error state, and the fetch runs once per
 * session (`started`), not once per render cause.
 */
export function MeProvider({ children }: { children: ReactNode }) {
  const { status } = useSession();
  const { call } = useApi();
  const [state, setState] = useState<MeState>({ kind: 'loading' });
  const started = useRef(false);

  useEffect(() => {
    if (status !== 'signed-in') {
      started.current = false;
      setState({ kind: 'loading' });
      return;
    }
    if (started.current) {
      return;
    }
    started.current = true;

    let cancelled = false;
    void (async () => {
      try {
        const result = await call((client) => client.me());
        if (!cancelled && result.ok) {
          setState({ kind: 'ready', principal: result.value });
        }
      } catch {
        if (!cancelled) {
          setState({ kind: 'error', message: 'Could not reach the Insolvia API.' });
        }
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [status, call]);

  const adopt = useCallback((principal: Principal) => {
    // `started` stays true: this is a fresher answer than a refetch would
    // give, not a reason to ask again.
    setState({ kind: 'ready', principal });
  }, []);
  const actions = useMemo<MeActions>(() => ({ adopt }), [adopt]);

  return (
    <MeActionsContext.Provider value={actions}>
      <MeContext.Provider value={state}>{children}</MeContext.Provider>
    </MeActionsContext.Provider>
  );
}

/** The session's `/v1/me` answer — see {@link MeProvider}. */
export function useMe(): MeState {
  return useContext(MeContext);
}

/**
 * The way to hand this provider a principal the server just answered with.
 * See {@link MeActions.adopt}.
 */
export function useMeActions(): MeActions {
  return useContext(MeActionsContext);
}

/**
 * The chrome's read of the firm membership. `undefined` — not known (signed
 * out, loading, or the fetch failed; render no link, the safe reading of
 * "don't know"); `null` — signed in and in no firm.
 */
export function useMembership(): FirmMembership | null | undefined {
  const state = useMe();
  return state.kind === 'ready' ? (state.principal.firm ?? null) : undefined;
}
