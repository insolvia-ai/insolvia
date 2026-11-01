// The admin portal's client, instrumented — never re-implemented (ADR 0029
// rule 2). `adminClient` returns the real `AdminClient` from
// `@insolvia-ai/admin-client`, imported by package name — the same code the
// portal ships — wrapped twice, as support/client.ts wraps the API's:
//
// - every exchange it makes is recorded, so a spec can assert the WIRE (the
//   status, the raw JSON and its error envelope) as well as what the client
//   made of it. The client calls the platform `fetch` and takes no fetch of
//   its own, so the recorder wraps the global one, once per test file, and
//   keeps only the exchanges aimed at the admin service;
// - a Proxy notes every public method a spec calls, for the admin-client
//   coverage gate (src/coverage.ts).
import { expect } from '@jest/globals';
import { AdminApiError, AdminClient, AdminUnauthorizedError } from '@insolvia-ai/admin-client';

import { adminClientMethods, noteCall } from '../coverage.ts';
import { env } from './env.ts';
import { expectShape, type Spec, withMessage } from './wire.ts';

export interface AdminExchange {
  readonly method: string;
  readonly path: string;
  readonly status: number;
  /** Parsed JSON when there was a body, else ''. */
  readonly body: unknown;
}

const exchanges: AdminExchange[] = [];
let recording = false;

function urlOf(input: Parameters<typeof fetch>[0]): string {
  if (typeof input === 'string') return input;
  return input instanceof URL ? input.href : input.url;
}

function record(): void {
  if (recording) return;
  recording = true;
  const platform = globalThis.fetch;
  globalThis.fetch = async (input, init) => {
    const response = await platform(input, init);
    const url = urlOf(input);
    if (url.startsWith(env.adminUrl)) {
      const text = await response.clone().text();
      exchanges.push({
        method: init?.method ?? 'GET',
        path: new URL(url).pathname,
        status: response.status,
        body: text === '' ? '' : (JSON.parse(text) as unknown),
      });
    }
    return response;
  };
}

const methods = new Set(adminClientMethods());

/** The portal's client against the admin service under test, sending `token`. */
export function adminClient(token: () => string): AdminClient {
  record();
  const real = new AdminClient(env.adminUrl, token);
  return new Proxy(real, {
    get(target, property, receiver) {
      const value: unknown = Reflect.get(target, property, receiver);
      if (typeof property === 'string' && methods.has(property) && typeof value === 'function') {
        return (...args: unknown[]) => {
          noteCall('admin', property);
          return (value as (...a: unknown[]) => unknown).apply(target, args);
        };
      }
      return value;
    },
  });
}

/** The most recent exchange with the admin service. Throws when there is none. */
export function lastAdminExchange(): AdminExchange {
  const found = exchanges.at(-1);
  if (!found) throw new Error('no admin exchange recorded yet');
  return found;
}

/** Asserts the last exchange answered `status` and, given a spec, its raw JSON's shape. */
export function adminWire(status: number, spec?: Spec): unknown {
  const exchange = lastAdminExchange();
  const label = `${exchange.method} ${exchange.path}`;
  withMessage(label, () => expect(exchange.status).toBe(status));
  if (spec !== undefined) expectShape(exchange.body, spec, label);
  return exchange.body;
}

const ENVELOPE: Spec = { error: 'string', 'message?': 'string', 'fields?': 'object' };

/**
 * Awaits a call the admin service must refuse with `status`: the client's
 * exception class and status, the raw `{"error", "message"|"fields"}`
 * envelope, and that the client carried its message or fields across.
 */
export async function expectAdminError(
  call: Promise<unknown>,
  status: number,
  error: string,
): Promise<{ readonly error: string; readonly message?: string; readonly fields?: object }> {
  let caught: unknown;
  try {
    await call;
  } catch (thrown) {
    caught = thrown;
  }
  withMessage(`expected a ${status} refusal, but the call succeeded`, () =>
    expect(caught).toBeInstanceOf(AdminApiError),
  );
  const failure = caught as AdminApiError;
  const body = adminWire(status, ENVELOPE) as { error: string; message?: string; fields?: object };
  withMessage(JSON.stringify(body), () => expect(body.error).toBe(error));
  expect(failure.statusCode).toBe(status);
  if (status === 401) {
    expect(failure).toBeInstanceOf(AdminUnauthorizedError);
  } else if (body.fields !== undefined) {
    expect(failure.fields).toEqual(body.fields);
  } else {
    expect(failure.message).toBe(body.message);
  }
  return body;
}
