// jsdom, plus the web platform globals Jest 29's jsdom (20) predates.
//
// jest-environment-jsdom@29 — the major jest-expo pins, ADR 0029 rule 3 —
// ships jsdom 20, whose window has no `fetch`/`Request`/`Response`/`Headers`
// and no `TextEncoder`/`TextDecoder`. Every browser this portal targets has
// them, and so does Node, so the test window takes Node's: client.ts spies
// on `fetch`, and session.tsx decodes the credential with `TextDecoder`.
import JSDOMEnvironment from "jest-environment-jsdom";

const FROM_NODE = [
  "fetch",
  "Request",
  "Response",
  "Headers",
  "TextEncoder",
  "TextDecoder",
  "structuredClone",
];

export default class Environment extends JSDOMEnvironment {
  constructor(...args) {
    super(...args);
    for (const name of FROM_NODE) {
      if (this.global[name] === undefined) this.global[name] = globalThis[name];
    }
  }
}
