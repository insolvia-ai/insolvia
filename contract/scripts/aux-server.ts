// The .NET contract stack's auxiliary server: the per-run JWKS and a
// stand-in for S3, as its own process because the .NET hosts are three.
//
//   CONTRACT_AUX_PORT=18060 CONTRACT_JWKS=<run>/jwks.json \
//   CONTRACT_BLOB_DIR=<run>/blobs node contract/scripts/aux-server.ts
//
// The developer compose stack (backend/docker-compose.yml) runs the same file
// as its blob twin, with no CONTRACT_JWKS — blobs only, the dev pool's real
// JWKS being Cognito's — and AUX_CORS_ORIGIN set to the app's origin, because
// there the PUT comes from a BROWSER on another port, as it does to S3 behind
// the bucket's CORS rule. The contract run sets neither: Node's fetch needs no
// CORS, and a header nobody reads changes nothing.
//
// - GET <issuerPath>/.well-known/jwks.json — what HttpJwksProvider.ForIssuer
//   derives for the Cognito-shaped issuer the API and MCP hosts trust;
// - GET /google/certs — the admin host's GOOGLE_JWKS_URL (local only);
// - PUT/GET /blobs/<ref, escaped whole>?method=… — the capabilities the
//   API's DirectoryDocumentBlobStore (backend/src/Insolvia.Domain/Local) mints
//   over the SAME directory: an object at <dir>/<ref>, its content type at
//   <dir>/<ref>.content-type. A PUT is refused, as S3 would refuse it, unless
//   its Content-Type and byte count are the ones bound into the URL.
//
// It changes no route's behaviour: it is where keys and bytes live locally.
import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { createServer, type IncomingMessage, type ServerResponse } from 'node:http';
import { dirname, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

function required(name: string): string {
  const value = process.env[name]?.trim();
  if (!value) {
    console.error(`${name} is required`);
    process.exit(2);
  }
  return value;
}

const port = Number(required('CONTRACT_AUX_PORT'));
const blobRoot = resolve(required('CONTRACT_BLOB_DIR'));
// Unset: blobs only (the compose stack). Set: the run's keys as well.
const jwksFile = process.env.CONTRACT_JWKS?.trim();
const jwks = jwksFile ? readFileSync(jwksFile) : undefined;
const jwksPaths = new Set<string>();
if (jwks) {
  const stack = JSON.parse(
    readFileSync(fileURLToPath(new URL('../stack/stack.json', import.meta.url)), 'utf8'),
  ) as { issuerPath: string };
  jwksPaths.add(`/${stack.issuerPath}/.well-known/jwks.json`).add('/google/certs');
}
// The one origin a browser may PUT from, as the bucket's CORS rule names the
// app's. Exact match, never `*`.
const corsOrigin = process.env.AUX_CORS_ORIGIN?.trim() || undefined;
const CONTENT_TYPE_SUFFIX = '.content-type';

mkdirSync(blobRoot, { recursive: true });

function send(res: ServerResponse, status: number, body: Buffer | string = '', type = ''): void {
  const bytes = typeof body === 'string' ? Buffer.from(body) : body;
  const headers: Record<string, string | number> = { 'Content-Length': bytes.length };
  if (type) headers['Content-Type'] = type;
  res.writeHead(status, headers);
  res.end(bytes);
}

/** The object a /blobs/ URL names, or undefined when it names none (or escapes the directory). */
function blob(url: URL): { path: string; terms: URLSearchParams } | undefined {
  if (!url.pathname.startsWith('/blobs/')) return undefined;
  const ref = decodeURIComponent(url.pathname.slice('/blobs/'.length));
  if (!ref || ref.endsWith(CONTENT_TYPE_SUFFIX)) return undefined;
  const path = resolve(blobRoot, ref);
  if (!path.startsWith(blobRoot + sep)) return undefined;
  return { path, terms: url.searchParams };
}

function body(req: IncomingMessage): Promise<Buffer> {
  return new Promise((done, fail) => {
    const chunks: Buffer[] = [];
    req.on('data', (chunk: Buffer) => chunks.push(chunk));
    req.on('end', () => done(Buffer.concat(chunks)));
    req.on('error', fail);
  });
}

const server = createServer((req, res) => {
  const url = new URL(req.url ?? '/', `http://127.0.0.1:${port}`);
  if (corsOrigin && req.headers.origin === corsOrigin) {
    res.setHeader('Access-Control-Allow-Origin', corsOrigin);
    res.setHeader('Access-Control-Expose-Headers', 'etag');
    res.setHeader('Vary', 'Origin');
  }
  void (async () => {
    if (req.method === 'OPTIONS' && corsOrigin && req.headers.origin === corsOrigin) {
      // The preflight a browser sends before the PUT. The allowed headers are
      // the case-documents bucket's own CORS rule
      // (infra/modules/case_documents/main.tf), so a header the app starts
      // sending fails here before it fails on staging. The PUT itself is
      // still checked below.
      res.setHeader('Access-Control-Allow-Methods', 'PUT, GET');
      res.setHeader(
        'Access-Control-Allow-Headers',
        'content-type, content-length, x-amz-server-side-encryption, x-amz-tagging',
      );
      send(res, 204);
      return;
    }
    if (req.method === 'GET' && url.pathname === '/health') {
      send(res, 200, '{"status":"ok"}', 'application/json');
      return;
    }
    if (req.method === 'GET' && jwks && jwksPaths.has(url.pathname)) {
      send(res, 200, jwks, 'application/json');
      return;
    }
    const found = blob(url);
    if (req.method === 'GET') {
      if (!found || found.terms.get('method') !== 'GET' || !existsSync(found.path)) {
        send(res, 404);
        return;
      }
      const typePath = found.path + CONTENT_TYPE_SUFFIX;
      const type = existsSync(typePath)
        ? readFileSync(typePath, 'utf8')
        : 'application/octet-stream';
      send(res, 200, readFileSync(found.path), type);
      return;
    }
    if (req.method === 'PUT') {
      const bytes = await body(req);
      // What S3 binds into the signature, refused the way S3 would.
      if (
        !found ||
        found.terms.get('method') !== 'PUT' ||
        String(bytes.length) !== found.terms.get('content_length') ||
        req.headers['content-type'] !== found.terms.get('content_type')
      ) {
        send(res, 403);
        return;
      }
      mkdirSync(dirname(found.path), { recursive: true });
      writeFileSync(found.path, bytes);
      writeFileSync(found.path + CONTENT_TYPE_SUFFIX, found.terms.get('content_type') ?? '');
      res.setHeader('ETag', `"${createHash('md5').update(bytes).digest('hex')}"`);
      send(res, 200);
      return;
    }
    send(res, 405);
  })().catch((error: unknown) => {
    console.error(error);
    if (!res.headersSent) send(res, 500);
  });
});

// 127.0.0.1 unless told otherwise: inside a container the published port
// reaches it only on 0.0.0.0, and the container's own port mapping is what
// keeps it on this machine (backend/docker-compose.yml binds 127.0.0.1).
const host = process.env.AUX_LISTEN_HOST?.trim() || '127.0.0.1';
server.listen(port, host, () => {
  console.log(`contract aux server :${port} (${jwks ? 'jwks, ' : ''}blobs in ${blobRoot})`);
});
