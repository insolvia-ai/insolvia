// The requests a BROWSER makes to the admin service that the portal's client
// never does — and the only hand-built HTTP left in this suite.
//
// The admin client (support/admin.ts) carries every call the portal makes.
// Three things around it are the browser's, not the client's:
//
// - the CORS preflight the browser sends before the client's cross-origin
//   request, whose answer decides whether that request is made at all;
// - a request with no credential at all — someone opening a route's URL. The
//   client refuses to send one (it raises before the network when it has no
//   token), so no client call can show the service refusing it;
// - `/health`, which the portal never calls and a monitor or a person does.
//
// Nothing here calls a route the client has a method for with a credential;
// a spec that wants a route's answer goes through the client.
import { env } from './env.ts';

export interface BrowserAnswer {
  readonly status: number;
  readonly headers: Headers;
  /** Parsed JSON when there was a body, else ''. */
  readonly body: unknown;
}

async function answer(response: Response): Promise<BrowserAnswer> {
  const text = await response.text();
  return {
    status: response.status,
    headers: response.headers,
    body: text === '' ? '' : (JSON.parse(text) as unknown),
  };
}

/** A request with no Authorization header, as opening the URL makes it. */
export async function signedOut(
  method: string,
  path: string,
  origin?: string,
): Promise<BrowserAnswer> {
  return answer(
    await fetch(`${env.adminUrl}${path}`, {
      method,
      headers: { Accept: 'application/json', ...(origin === undefined ? {} : { Origin: origin }) },
    }),
  );
}

/** The browser's CORS preflight for a `method` request carrying the client's headers. */
export async function preflight(path: string, origin: string, method: string): Promise<Headers> {
  const response = await fetch(`${env.adminUrl}${path}`, {
    method: 'OPTIONS',
    headers: {
      Origin: origin,
      'Access-Control-Request-Method': method,
      'Access-Control-Request-Headers': 'authorization, content-type',
    },
  });
  await response.body?.cancel();
  return response.headers;
}
