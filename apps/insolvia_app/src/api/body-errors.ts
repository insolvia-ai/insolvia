/**
 * A case collection save's field errors keyed by the body's own paths, the
 * way a screen names its boxes: the API keys them under the request's
 * `body` (`body.address.postalCode`, ADR 0032); `provenance.…` keys stay.
 */
export function bodyErrors(fields: Readonly<Record<string, string>>): Record<string, string> {
  return Object.fromEntries(
    Object.entries(fields).map(([key, message]) => [
      key.startsWith('body.') ? key.slice('body.'.length) : key,
      message,
    ]),
  );
}
