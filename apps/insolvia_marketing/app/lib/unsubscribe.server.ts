/**
 * Unsubscribe forwarding (issue #80 / 6.8).
 *
 * The third link in the chain: an email footer link (or a mail client's own
 * one-click control) lands on `/unsubscribe`, whose action calls this, which
 * POSTs the token to the Insolvia API, which verifies its HMAC and asks the
 * mailer to suppress the address.
 *
 * Same posture as waitlist.server.ts and for the same reason (ADR 0001,
 * docs/adr/0001-client-stays-dumb-trust-boundary.md): this Lambda holds no AWS
 * credentials and talks to no AWS service. It cannot suppress anything itself
 * — it forwards an opaque token and reports what the API said.
 *
 * The token is deliberately opaque HERE. This layer cannot read the address
 * out of it, cannot validate it, and must not try: the signature is the only
 * thing that decides whether it is real, and the key for that lives with the
 * API. So there is no client-side pre-validation to mirror the way
 * parseWaitlistForm mirrors the API's field rules — a token either verifies
 * server-side or it does not.
 */

/** A token is ~120 characters. Anything wildly longer is not one, and there
 *  is no reason to spend a network round-trip finding that out. */
const MAX_TOKEN_LENGTH = 1024;

export type UnsubscribeResult =
  | { ok: true }
  /** The token was missing, malformed, or did not verify. */
  | { ok: false; reason: "invalid-token" }
  /** The API or the network failed — a retry might work. */
  | { ok: false; reason: "unavailable" };

/** Pull the token from the form body, falling back to the query string.
 *
 * Both matter. A person clicking the footer link posts our own form, which
 * carries the token in a hidden field. A mail client honouring RFC 8058 POSTs
 * `List-Unsubscribe=One-Click` to the URL verbatim — its body contains no
 * token at all, only the query string does. Reading just one of the two would
 * silently break one of the two entry points.
 */
export function tokenFrom(form: FormData, url: URL): string | null {
  const fromBody = form.get("token");
  const candidate =
    (typeof fromBody === "string" ? fromBody : "").trim() ||
    (url.searchParams.get("token") ?? "").trim();
  if (!candidate || candidate.length > MAX_TOKEN_LENGTH) return null;
  return candidate;
}

/**
 * Forward one unsubscribe to the API.
 *
 * `INSOLVIA_API_BASE_URL` names the API origin (set on the SSR Lambda by
 * infra). Unlike the waitlist — where an unset base URL logs and pretends
 * success so a local form still works — an unset base URL here is reported as
 * `unavailable`. Telling someone their unsubscribe went through when nothing
 * was recorded is the one outcome this page must never produce.
 *
 * Responses: 202 → ok; 400 → invalid-token; anything else → unavailable.
 */
export async function submitUnsubscribe(token: string): Promise<UnsubscribeResult> {
  const baseUrl = process.env.INSOLVIA_API_BASE_URL;
  if (!baseUrl) {
    console.error(
      "[unsubscribe] INSOLVIA_API_BASE_URL is not set — cannot honour the request",
    );
    return { ok: false, reason: "unavailable" };
  }

  let response: Response;
  try {
    response = await fetch(`${baseUrl.replace(/\/$/, "")}/v1/unsubscribe`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ token }),
    });
  } catch (error) {
    console.error("[unsubscribe] failed to reach the API:", error);
    return { ok: false, reason: "unavailable" };
  }

  if (response.status === 202) return { ok: true };
  if (response.status === 400) return { ok: false, reason: "invalid-token" };

  console.error(`[unsubscribe] API responded ${response.status}`);
  return { ok: false, reason: "unavailable" };
}
