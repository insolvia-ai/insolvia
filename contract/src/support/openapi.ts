// Holds the OpenAPI documents (docs/reference/openapi/) to the running hosts.
//
// Every exchange an instrumented client makes is noted (coverage.ts,
// `noteExchange`), and the teardown checks each one here:
//
// - its method and path match an operation in the service's document;
// - its status is one the operation documents (any 4xx answers the shared
//   error envelope);
// - the request body matches the operation's request schema, exactly — a key
//   the client sends that the document does not name is a failure;
// - the response body matches the schema for its status. A field the server
//   sends that the document does not name is allowed, as it is for the
//   clients' models (support/wire.ts), and counted in the summary.
//
// The hosts generate the documents from their typed routes (OpenApiDocumentTests
// keeps the committed copies current), so a failure here means a route's
// declared result disagrees with what it sends, or a client sends what the
// route does not take: fix the route or the client, then run
// backend/scripts/dev-openapi.sh.
//
// The validator covers the JSON Schema the generator emits — `$ref`, `type`,
// `properties`, `required`, `additionalProperties`, `items`, `enum`, `const`,
// `anyOf`, `oneOf` (checked as `anyOf`: the generator only uses it for
// `null` or a `$ref`) — and nothing else. `type` may be a list (OpenAPI 3.1).
import { readFileSync } from 'node:fs';
import { join } from 'node:path';

import type { WireExchange } from '../coverage.ts';

type Schema = {
  readonly $ref?: string;
  readonly type?: string | readonly string[];
  readonly properties?: Readonly<Record<string, Schema>>;
  readonly required?: readonly string[];
  readonly additionalProperties?: boolean | Schema;
  readonly items?: Schema;
  readonly enum?: readonly unknown[];
  readonly const?: unknown;
  readonly anyOf?: readonly Schema[];
  readonly oneOf?: readonly Schema[];
};

interface Operation {
  readonly requestBody?: { readonly content: Record<string, { readonly schema: Schema }> };
  readonly responses: Record<
    string,
    { readonly $ref?: string; readonly content?: Record<string, { readonly schema: Schema }> }
  >;
}

interface Document {
  readonly paths: Record<string, Record<string, Operation>>;
  readonly components: {
    readonly schemas: Record<string, Schema>;
    readonly responses: Record<
      string,
      { readonly content: Record<string, { readonly schema: Schema }> }
    >;
  };
}

const DOCUMENTS: Record<WireExchange['surface'], string> = {
  api: 'api.json',
  admin: 'admin.json',
};

function load(file: string): Document {
  // `__dirname`, not `import.meta`: Jest runs this transpiled to CommonJS (support/target.ts).
  const dir = join(__dirname, '..', '..', '..', 'docs', 'reference', 'openapi');
  return JSON.parse(readFileSync(join(dir, file), 'utf8')) as Document;
}

interface Template {
  readonly path: string;
  readonly pattern: RegExp;
  /** Literal segments first, so `/documents/{id}/url` beats `/{collection}/{id}/…`. */
  readonly rank: string;
}

function templates(document: Document): Template[] {
  return Object.keys(document.paths)
    .map((path) => ({
      path,
      pattern: new RegExp(`^${path.replace(/\{[^}]+\}/g, '[^/]+')}$`),
      rank: path
        .split('/')
        .map((s) => (s.startsWith('{') ? '0' : '1'))
        .join(''),
    }))
    .sort((a, b) => b.rank.localeCompare(a.rank));
}

class Validator {
  readonly #schemas: Record<string, Schema>;
  /** Response fields the server sent that the document does not name. */
  readonly undocumented = new Set<string>();

  constructor(document: Document) {
    this.#schemas = document.components.schemas;
  }

  check(value: unknown, schema: Schema, at: string, strict: boolean, problems: string[]): void {
    if (schema.$ref) {
      const name = schema.$ref.replace('#/components/schemas/', '');
      const target = this.#schemas[name];
      if (!target) {
        problems.push(`${at}: the document has no schema ${name}`);
        return;
      }
      this.check(value, target, at, strict, problems);
      return;
    }
    const alternatives = schema.anyOf ?? schema.oneOf;
    if (alternatives) {
      // Only the alternative that fits says what is undocumented: a variant
      // for another entityType would call the payload's fields unknown.
      const before = new Set(this.undocumented);
      const fits = alternatives.some((option) => {
        const own: string[] = [];
        this.undocumented.clear();
        for (const field of before) this.undocumented.add(field);
        this.check(value, option, at, strict, own);
        return own.length === 0;
      });
      if (!fits) {
        this.undocumented.clear();
        for (const field of before) this.undocumented.add(field);
      }
      if (!fits)
        problems.push(
          `${at}: matches none of its ${alternatives.length} alternatives (${preview(value)})`,
        );
      return;
    }
    if (schema.const !== undefined && value !== schema.const) {
      problems.push(`${at}: expected ${JSON.stringify(schema.const)}, got ${preview(value)}`);
      return;
    }
    if (schema.enum && !schema.enum.includes(value)) {
      problems.push(
        `${at}: ${preview(value)} is not one of ${schema.enum.map((v) => JSON.stringify(v)).join(', ')}`,
      );
      return;
    }
    const types = typeof schema.type === 'string' ? [schema.type] : schema.type;
    if (types && !types.some((type) => hasType(value, type))) {
      problems.push(`${at}: expected ${types.join(' or ')}, got ${preview(value)}`);
      return;
    }
    if (Array.isArray(value) && schema.items) {
      value.forEach((item, i) =>
        this.check(item, schema.items as Schema, `${at}[${i}]`, strict, problems),
      );
      return;
    }
    if (value === null || typeof value !== 'object' || Array.isArray(value)) return;
    const record = value as Record<string, unknown>;
    for (const name of schema.required ?? []) {
      if (!(name in record)) problems.push(`${at}.${name}: required, absent`);
    }
    for (const [name, member] of Object.entries(record)) {
      const declared = schema.properties?.[name];
      if (declared) {
        this.check(member, declared, `${at}.${name}`, strict, problems);
      } else if (typeof schema.additionalProperties === 'object') {
        this.check(member, schema.additionalProperties, `${at}.${name}`, strict, problems);
      } else if (schema.properties && schema.additionalProperties !== true) {
        if (strict) problems.push(`${at}.${name}: not in the document`);
        else this.undocumented.add(`${at.replace(/\[\d+\]/g, '[]')}.${name}`);
      }
    }
  }
}

function hasType(value: unknown, type: string): boolean {
  switch (type) {
    case 'null':
      return value === null;
    case 'array':
      return Array.isArray(value);
    case 'object':
      return value !== null && typeof value === 'object' && !Array.isArray(value);
    case 'integer':
      return typeof value === 'number' && Number.isInteger(value);
    default:
      return typeof value === type;
  }
}

function preview(value: unknown): string {
  const text = JSON.stringify(value) ?? String(value);
  return text.length > 60 ? `${text.slice(0, 57)}...` : text;
}

export interface OpenApiReport {
  readonly summary: string;
  readonly failures: readonly string[];
}

/** Checks every exchange the run made against its service's document. */
export function checkExchanges(exchanges: readonly WireExchange[]): OpenApiReport {
  const failures = new Set<string>();
  const exercised = new Set<string>();
  const undocumented = new Set<string>();
  for (const surface of Object.keys(DOCUMENTS) as WireExchange['surface'][]) {
    const document = load(DOCUMENTS[surface]);
    const paths = templates(document);
    const validator = new Validator(document);
    for (const exchange of exchanges.filter((e) => e.surface === surface)) {
      const method = exchange.method.toLowerCase();
      const template = paths.find(
        (t) => t.pattern.test(exchange.path) && document.paths[t.path]?.[method],
      );
      const label = `${surface} ${exchange.method} ${template?.path ?? exchange.path}`;
      if (!template) {
        // A host answers a route it does not have (an unknown collection, say)
        // with the error envelope, like any refusal; only that is checked.
        if (exchange.status >= 400 && exchange.status < 500) {
          const problems: string[] = [];
          const envelope = document.components.responses.Error!.content['application/json']!.schema;
          if (exchange.response !== undefined)
            validator.check(exchange.response, envelope, 'error', false, problems);
          for (const problem of problems) failures.add(`${label}: ${problem}`);
        } else failures.add(`${label}: no such operation in ${DOCUMENTS[surface]}`);
        continue;
      }
      const operation = document.paths[template.path]![method]!;
      exercised.add(label);
      const problems: string[] = [];

      // A refused request is often malformed on purpose (a route test's job,
      // but a happy-path spec may probe one): only an accepted one must fit.
      if (exchange.request !== undefined && exchange.status < 400) {
        const schema = operation.requestBody?.content['application/json']?.schema;
        if (schema) validator.check(exchange.request, schema, 'request', true, problems);
        else problems.push('request: sent a body the document does not take');
      }

      const status = String(exchange.status);
      const documented = operation.responses[status];
      if (documented) {
        const schema = documented.content?.['application/json']?.schema;
        if (schema && exchange.response !== undefined) {
          validator.check(exchange.response, schema, 'response', false, problems);
        }
      } else if (exchange.status >= 400 && exchange.status < 500) {
        const envelope = document.components.responses.Error!.content['application/json']!.schema;
        if (exchange.response !== undefined)
          validator.check(exchange.response, envelope, 'error', false, problems);
      } else {
        problems.push(`status ${status} is not documented`);
      }
      for (const problem of problems) failures.add(`${label}: ${problem}`);
      for (const field of validator.undocumented) undocumented.add(`${label} ${field}`);
      validator.undocumented.clear();
    }
  }
  return {
    summary:
      `openapi: ${exchanges.length} exchanges checked across ${exercised.size} operations, ` +
      `${failures.size} disagreements, ${undocumented.size} undocumented response fields`,
    failures: [...failures].sort(),
  };
}
