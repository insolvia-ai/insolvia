/**
 * A stable id for a repeating row the user just added — an alias on the 8-year
 * lookback, and later a creditor, an asset, a SOFA entry.
 *
 * The CLIENT has to mint these. A row's provenance is written in the same
 * request that creates it, so the caller must be able to name the row it is
 * describing: `other_names_used[<id>].surname`. A server-assigned id would need
 * a round trip between "add a row" and "say where it came from", and
 * progressive autosave has no such moment. The API refuses a row without one.
 *
 * The id must match the API's `ADDRESSABLE_ID_RE` — `[A-Za-z0-9_-]+` — because
 * anything else produces a provenance path the server rejects as malformed. A
 * UUID qualifies.
 *
 * UNLIKE `session/pkce.ts`, A FALLBACK IS LEGITIMATE HERE, and the contrast is
 * worth stating because that module bans one in capital letters. There, the
 * random value IS the security parameter and a predictable one silently defeats
 * the exchange. Here it is a row key: it must not collide within a case, and it
 * is never a secret. So a runtime without `crypto.randomUUID` gets a
 * counter-and-clock id rather than a form that cannot add a row.
 */
let counter = 0;

export function newRowId(): string {
  const cryptoLike = (globalThis as { crypto?: { randomUUID?: () => string } }).crypto;
  if (typeof cryptoLike?.randomUUID === 'function') {
    return cryptoLike.randomUUID();
  }
  counter += 1;
  // Hyphens only — the same character class a UUID uses, so both forms satisfy
  // the API's id grammar.
  return `row-${Date.now().toString(36)}-${counter}`;
}
