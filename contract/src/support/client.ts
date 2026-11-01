// The shipped client, instrumented — never re-implemented.
//
// `contractClient` returns a real `InsolviaApiClient` (imported by package
// name, through its export map) wrapped twice:
//
// - its `fetch` records every exchange, so a test can assert on the WIRE —
//   status, raw JSON, field names and types — not just on what the client's
//   decoder made of it;
// - a Proxy notes every public method the suite calls, for the coverage gate
//   (src/coverage.ts), which fails the run if any method of the class
//   was never invoked.
import { appendFileSync } from 'node:fs';
import { join } from 'node:path';

import { expect } from '@jest/globals';
import { InsolviaApiClient } from '@insolvia-ai/api-client';

import { coverageDir } from '../coverage.ts';
import { env } from './env.ts';
import { expectShape, type Spec, withMessage } from './wire.ts';

export interface Exchange {
  readonly method: string;
  readonly path: string;
  readonly status: number;
  readonly contentType: string;
  /** Parsed JSON when the body was JSON, else the raw text ('' for none). */
  readonly body: unknown;
}

/** Every public method on the client's prototype — what the gate counts. */
export const CLIENT_METHODS: readonly string[] = Object.getOwnPropertyNames(
  InsolviaApiClient.prototype,
).filter(
  (name) =>
    name !== 'constructor' &&
    typeof Object.getOwnPropertyDescriptor(InsolviaApiClient.prototype, name)?.value === 'function',
);

const methodSet = new Set(CLIENT_METHODS);

function noteCall(method: string): void {
  const dir = coverageDir();
  appendFileSync(join(dir, `${process.pid}.log`), `${method}\n`);
}

export interface ContractClient {
  readonly api: InsolviaApiClient;
  /** Every exchange this client made, oldest first. */
  readonly exchanges: Exchange[];
  /** The most recent exchange. Throws when there is none. */
  last(): Exchange;
  /**
   * Asserts the most recent exchange answered `status` — and, given a spec,
   * that its raw JSON body has the declared shape. Returns the raw body.
   */
  wire(status: number, spec?: Spec): unknown;
}

/**
 * A client for the API under test. `token` is the bearer to send: a string,
 * or `undefined` for a client with no token at all (the client then refuses
 * protected calls itself, before the network).
 */
export function contractClient(token?: string | (() => string)): ContractClient {
  const exchanges: Exchange[] = [];
  const recordingFetch = async (input: string, init?: RequestInit): Promise<Response> => {
    const response = await globalThis.fetch(input, init);
    const text = await response.clone().text();
    const contentType = response.headers.get('content-type') ?? '';
    let body: unknown = text;
    if (contentType.includes('json') && text !== '') {
      body = JSON.parse(text);
    }
    const url = new URL(input);
    exchanges.push({
      method: init?.method ?? 'GET',
      path: url.pathname + url.search,
      status: response.status,
      contentType,
      body,
    });
    return response;
  };
  const real = new InsolviaApiClient(env.apiUrl, {
    fetch: recordingFetch,
    accessToken: token === undefined ? undefined : typeof token === 'string' ? () => token : token,
  });
  const api = new Proxy(real, {
    get(target, property, receiver) {
      const value: unknown = Reflect.get(target, property, receiver);
      if (typeof property === 'string' && methodSet.has(property) && typeof value === 'function') {
        return (...args: unknown[]) => {
          noteCall(property);
          // `target`, not the proxy: the class keeps its state in #private
          // fields, which only the real instance carries.
          return (value as (...a: unknown[]) => unknown).apply(target, args);
        };
      }
      return value;
    },
  });
  const last = (): Exchange => {
    const found = exchanges.at(-1);
    if (!found) throw new Error('no exchange recorded yet');
    return found;
  };
  return {
    api,
    exchanges,
    last,
    wire(status, spec) {
      const exchange = last();
      withMessage(`${exchange.method} ${exchange.path}`, () =>
        expect(exchange.status).toBe(status),
      );
      if (spec !== undefined)
        expectShape(exchange.body, spec, `${exchange.method} ${exchange.path}`);
      return exchange.body;
    },
  };
}
