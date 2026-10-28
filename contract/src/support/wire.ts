// Assertions about the wire, in the API's own vocabulary.
//
// `expectShape` checks a raw JSON body against the field names and JSON types
// models.ts declares. It is deliberately NOT exact: a field the client does
// not read may come and go, but a declared field that is missing, renamed or
// retyped is a contract break — and an absent optional field must be ABSENT,
// never `null` (the API omits; packages/insolvia_api_client/CLAUDE.md).
//
// `expectApiError` checks a refusal: the status, the client's exception type,
// and the envelope every error carries — `{"error": <code>, "message": …}`, or
// `{"error": "ValidationError", "fields": {<name>: <message>}}`.
import {
  ApiException,
  ApiUnauthorizedException,
  ApiValidationException,
} from '@insolvia-ai/api-client';
import { expect } from 'vitest';

type Leaf = 'string' | 'number' | 'integer' | 'boolean' | 'object' | 'array' | 'any' | 'null';
/**
 * A leaf type (`'string'`), a union (`'string|null'`), an object spec whose
 * keys end in `?` when optional, or `[spec]` for an array of `spec`.
 */
export type Spec = Leaf | `${Leaf}|${Leaf}` | { readonly [key: string]: Spec } | readonly [Spec];

function typeOf(value: unknown): Leaf {
  if (value === null) return 'null';
  if (Array.isArray(value)) return 'array';
  if (typeof value === 'number') return Number.isInteger(value) ? 'integer' : 'number';
  return typeof value as Leaf;
}

function check(value: unknown, spec: Spec, path: string, problems: string[]): void {
  if (typeof spec === 'string') {
    const allowed = spec.split('|') as Leaf[];
    if (allowed.includes('any')) return;
    const actual = typeOf(value);
    const ok = allowed.includes(actual) || (actual === 'integer' && allowed.includes('number'));
    if (!ok) problems.push(`${path}: expected ${spec}, got ${actual} (${JSON.stringify(value)})`);
    return;
  }
  if (Array.isArray(spec)) {
    if (!Array.isArray(value)) {
      problems.push(`${path}: expected an array, got ${typeOf(value)}`);
      return;
    }
    value.forEach((item, index) => check(item, spec[0] as Spec, `${path}[${index}]`, problems));
    return;
  }
  if (typeOf(value) !== 'object') {
    problems.push(`${path}: expected an object, got ${typeOf(value)}`);
    return;
  }
  const record = value as Record<string, unknown>;
  for (const [rawKey, child] of Object.entries(spec as Record<string, Spec>)) {
    const optional = rawKey.endsWith('?');
    const key = optional ? rawKey.slice(0, -1) : rawKey;
    if (!(key in record)) {
      if (!optional) problems.push(`${path}.${key}: missing`);
      continue;
    }
    if (optional && record[key] === null && !String(child).includes('null')) {
      problems.push(`${path}.${key}: optional fields are omitted, never null`);
      continue;
    }
    check(record[key], child, `${path}.${key}`, problems);
  }
}

/** Fails with every mismatch at once, each naming its JSON path. */
export function expectShape(value: unknown, spec: Spec, label = '$'): void {
  const problems: string[] = [];
  check(value, spec, label, problems);
  expect(problems, `wire shape of ${label}`).toEqual([]);
}

export interface ErrorEnvelope {
  readonly error: string;
  readonly message?: string;
  readonly fields?: Record<string, string>;
}

const ENVELOPE: Spec = { error: 'string', 'message?': 'string', 'fields?': 'object' };

/**
 * Awaits a call that must be refused with `status`, and returns the parsed
 * envelope. `error` pins the envelope's code when given.
 */
export async function expectApiError(
  call: Promise<unknown>,
  status: number,
  error?: string,
): Promise<ErrorEnvelope> {
  let caught: unknown;
  try {
    await call;
  } catch (thrown) {
    caught = thrown;
  }
  expect(caught, `expected a ${status} refusal, but the call succeeded`).toBeInstanceOf(
    ApiException,
  );
  const failure = caught as ApiException;
  expect(failure.statusCode, failure.body).toBe(status);
  if (status === 401) {
    expect(failure).toBeInstanceOf(ApiUnauthorizedException);
    expect((failure as ApiUnauthorizedException).source).toBe('server');
  }
  const body = JSON.parse(failure.body) as ErrorEnvelope;
  expectShape(body, ENVELOPE, 'error');
  expect(body.message !== undefined || body.fields !== undefined, failure.body).toBe(true);
  if (body.fields !== undefined) {
    expect(failure).toBeInstanceOf(ApiValidationException);
    for (const [name, message] of Object.entries(body.fields)) {
      expect(typeof message, `fields.${name}`).toBe('string');
    }
  }
  if (error !== undefined) expect(body.error, failure.body).toBe(error);
  return body;
}
