/**
 * The one piece of Google's Identity Services this portal uses: the script
 * loader and the typed sliver of `google.accounts.id` behind the sign-in
 * button.
 *
 * WHY THIS AND NOT AUTHORIZATION-CODE + PKCE. Google "Web application" OAuth
 * clients require the client secret at the token endpoint even when PKCE is
 * sent — the exchange answers `invalid_request: client_secret is missing`,
 * unlike Cognito, which genuinely supports secretless SPA clients. The
 * original redirect flow (#214) was built on that false assumption and could
 * never complete in any environment. GIS sidesteps the token endpoint
 * entirely: the button hands the page an ID token directly, no secret exists
 * anywhere, and the admin service's verification (issuer, audience, `hd`,
 * `email_verified`) is untouched. The alternative — exchanging the code
 * server-side with a secret held by the admin service — was considered and
 * declined: it adds a per-environment secret to provision and rotate, for a
 * flow GIS provides without one.
 *
 * The script comes from Google's origin at runtime; each environment's
 * portal origin must be registered under the OAuth client's "Authorized
 * JavaScript origins" (localhost:3100 / staging-admin / admin), the same
 * console screen that held the now-unused redirect URIs.
 */

/** What the button's callback receives; `credential` is the Google ID token. */
export interface CredentialResponse {
  readonly credential: string;
}

/** The subset of `google.accounts.id` this portal calls. */
export interface GoogleIdentityApi {
  initialize(config: {
    readonly client_id: string;
    readonly callback: (response: CredentialResponse) => void;
    /** Workspace-domain HINT — enforcement stays with the service's hd check. */
    readonly hd?: string;
    /** Deliberately never set true: silent re-sign-in is a decision, not a default. */
    readonly auto_select?: boolean;
  }): void;
  renderButton(
    parent: HTMLElement,
    options: {
      readonly theme?: 'outline' | 'filled_blue' | 'filled_black';
      readonly size?: 'large' | 'medium' | 'small';
      readonly text?: 'signin_with' | 'signup_with' | 'continue_with' | 'signin';
      readonly width?: number;
    },
  ): void;
}

declare global {
  interface Window {
    google?: { accounts?: { id?: GoogleIdentityApi } };
  }
}

export const IDENTITY_SCRIPT_URL = 'https://accounts.google.com/gsi/client';

let loading: Promise<GoogleIdentityApi> | null = null;

/**
 * Loads Google's script once and resolves its API. Idempotent across callers;
 * a failed load clears the memo so a reload-and-retry is possible rather than
 * the first failure being cached forever.
 */
export function loadGoogleIdentity(): Promise<GoogleIdentityApi> {
  if (loading !== null) {
    return loading;
  }
  const held = window.google?.accounts?.id;
  if (held !== undefined) {
    loading = Promise.resolve(held);
    return loading;
  }
  loading = new Promise((resolve, reject) => {
    const script = document.createElement('script');
    script.src = IDENTITY_SCRIPT_URL;
    script.async = true;
    script.onload = () => {
      const api = window.google?.accounts?.id;
      if (api === undefined) {
        loading = null;
        reject(new Error("Google's sign-in script loaded without its API"));
      } else {
        resolve(api);
      }
    };
    script.onerror = () => {
      loading = null;
      reject(new Error("Google's sign-in script failed to load"));
    };
    document.head.appendChild(script);
  });
  return loading;
}
