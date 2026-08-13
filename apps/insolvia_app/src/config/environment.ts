/**
 * Which deployment environment this build targets.
 *
 * Selected at build time via `EXPO_PUBLIC_INSOLVIA_ENV`:
 * ```bash
 * EXPO_PUBLIC_INSOLVIA_ENV=staging npm run build --workspace @insolvia-ai/app
 * ```
 *
 * The `EXPO_PUBLIC_` prefix is not decoration: Expo inlines **only** variables
 * with that prefix into the client bundle. An unprefixed name would read as
 * `undefined` at runtime and — by design, see `resolveEnvironment` — fall back
 * to `local`.
 *
 * A union of string literals rather than an `enum`: `erasableSyntaxOnly` is on
 * (see tsconfig.json), because Metro strips types instead of compiling them and
 * cannot execute an `enum`.
 */
export const APP_ENVIRONMENTS = ['local', 'staging', 'production'] as const;

/** One of the environments this app can be built for. */
export type AppEnvironment = (typeof APP_ENVIRONMENTS)[number];

/** Human-readable label shown in the UI so staging vs prod is unmistakable. */
export const environmentLabels = {
  local: 'Local',
  staging: 'Staging',
  production: 'Production',
} as const satisfies Record<AppEnvironment, string>;

/** The public host this environment serves from (informational for now). */
export const environmentHosts = {
  local: 'localhost',
  staging: 'staging-app.insolvia.ai',
  production: 'app.insolvia.ai',
} as const satisfies Record<AppEnvironment, string>;

/**
 * The base URL of the Insolvia API this build talks to.
 *
 * Local is the `services/api` docker-compose port (see
 * services/api/docker-compose.yml — `127.0.0.1:8080`).
 */
export const environmentApiBaseUrls = {
  local: 'http://localhost:8080',
  staging: 'https://staging-api.insolvia.ai',
  production: 'https://api.insolvia.ai',
} as const satisfies Record<AppEnvironment, string>;

/**
 * Where the marketing site lives for this environment — the footer's Privacy
 * and help links leave the app for it.
 *
 * Local points at the DEPLOYED staging site rather than a `localhost` port,
 * and that is deliberate: marketing is a separate dev server nobody runs
 * alongside the app, so a localhost link here would be a dead link on every
 * developer machine. The pages it reaches are public and identical.
 */
export const environmentMarketingUrls = {
  local: 'https://staging.insolvia.ai',
  staging: 'https://staging.insolvia.ai',
  production: 'https://insolvia.ai',
} as const satisfies Record<AppEnvironment, string>;

/**
 * Exhaustiveness guards.
 *
 * `satisfies Record<AppEnvironment, …>` already rejects a map that omits an
 * environment; these assertions state the intent where a reader will see it and
 * survive a future refactor to a wider map type. Adding a fourth environment to
 * `APP_ENVIRONMENTS` stops compiling until it declares its host and its API —
 * which, together with `resolveEnvironment` falling back to `local` for any
 * unrecognised value, is the whole of issue #64: no build can *silently* end up
 * pointing at the production API. Only an explicit
 * `EXPO_PUBLIC_INSOLVIA_ENV=production` reaches it.
 */
type Undeclared<M> = Exclude<AppEnvironment, keyof M>;
type Exhaustive<M> = [Undeclared<M>] extends [never] ? true : never;

const _hostsAreExhaustive: Exhaustive<typeof environmentHosts> = true;
const _apiBaseUrlsAreExhaustive: Exhaustive<typeof environmentApiBaseUrls> = true;
const _labelsAreExhaustive: Exhaustive<typeof environmentLabels> = true;
const _marketingUrlsAreExhaustive: Exhaustive<typeof environmentMarketingUrls> = true;

/** The marketing site for an environment. See {@link environmentMarketingUrls}. */
export function marketingUrl(env: AppEnvironment = appEnvironment): string {
  return environmentMarketingUrls[env];
}

/**
 * Which bundle this is — the last segment of the footer's build stamp.
 *
 * A COMMIT SHA rather than the `version` in `app.config.ts`, because that
 * version has not moved since the app was created and never distinguishes two
 * builds. This is what a customer reads back over a support call.
 *
 * `dev` when unset, which is every local build: `EXPO_PUBLIC_BUILD_SHA` is
 * supplied by the deploy workflows from `github.sha`. Like every
 * `EXPO_PUBLIC_*` value it is inlined at bundle time and is not a secret — a
 * commit sha is public the moment it is pushed.
 */
export const buildStamp: string = process.env.EXPO_PUBLIC_BUILD_SHA?.trim().slice(0, 7) || 'dev';

/**
 * Resolves the environment from the compile-time `EXPO_PUBLIC_INSOLVIA_ENV`
 * value, defaulting to `local` for `npm run web` during development.
 *
 * `raw` is a parameter with a default rather than a direct read, so tests can
 * exercise every arm — including the unknown-value one — without a rebuild.
 * Unknown, empty and absent all resolve to `local`; **never** to production.
 */
export function resolveEnvironment(
  raw: string | undefined = process.env.EXPO_PUBLIC_INSOLVIA_ENV,
): AppEnvironment {
  switch (raw) {
    case 'production':
    case 'prod':
      return 'production';
    case 'staging':
      return 'staging';
    default:
      return 'local';
  }
}

/** True only for the production build. */
export function isProduction(env: AppEnvironment): boolean {
  return env === 'production';
}

/** The environment this bundle was built for. Resolved once, at module load. */
export const appEnvironment: AppEnvironment = resolveEnvironment();

/** Everything the UI needs to describe the running build. */
export interface EnvironmentInfo {
  readonly name: AppEnvironment;
  readonly label: string;
  readonly host: string;
  readonly apiBaseUrl: string;
}

/**
 * The Cognito hosted UI this build signs in against.
 *
 * `domain` is an **origin**, scheme included and no trailing slash
 * (`https://…amazoncognito.com`); `/oauth2/authorize`, `/oauth2/token` and
 * `/logout` hang off it.
 */
export interface AuthConfig {
  readonly domain: string;
  readonly clientId: string;
}

/**
 * Resolves the hosted-UI configuration, or `null` when this build has none.
 *
 * **This is the one deliberate deviation from the hardcoded-map convention
 * above,** and it is worth stating why. `environmentHosts` and
 * `environmentApiBaseUrls` are exhaustive maps because their values are names
 * *we* chose and can write down. The Cognito hosted domain and app client id
 * are neither: Terraform generates them (`infra/modules/auth`), the client id
 * is a random identifier, and both change whenever a pool is recreated. There
 * is no correct value to hardcode, so they arrive as build-time environment
 * variables that the deploy workflow fills in from `terraform output`.
 *
 * **Neither is a secret**, which is what makes `EXPO_PUBLIC_` legitimate here:
 * `docs/reference/architecture.md` warns that an `EXPO_PUBLIC_` name is inlined
 * into a bundle any visitor can read, and both of these values are already
 * published on every sign-in redirect — the client id rides in the query string
 * of `/oauth2/authorize` and the domain *is* the URL. The app client is a
 * public OAuth client with `generate_secret = false` precisely so that nothing
 * secret is needed in the browser (ADR 0007).
 *
 * **`null` is a supported state, not a failure.** The `local` default carries
 * no pool, so the app renders an explicit "sign-in is not configured for this
 * environment" screen rather than building a redirect to `undefined`.
 *
 * Both parameters have defaults rather than being read directly, for the same
 * reason {@link resolveEnvironment} does: tests exercise every arm without a
 * rebuild. Expo inlines `EXPO_PUBLIC_*` at transform time, so a direct read
 * would be frozen into the bundle and untestable.
 */
export function resolveAuthConfig(
  rawDomain: string | undefined = process.env.EXPO_PUBLIC_COGNITO_DOMAIN,
  rawClientId: string | undefined = process.env.EXPO_PUBLIC_COGNITO_CLIENT_ID,
): AuthConfig | null {
  const domain = normalizeAuthDomain(rawDomain);
  const clientId = rawClientId?.trim() ?? '';
  if (domain === null || clientId === '') {
    return null;
  }
  return { domain, clientId };
}

/**
 * Normalizes a hosted-UI domain to a bare origin, or `null` when there isn't
 * one.
 *
 * Terraform's `aws_cognito_user_pool_domain` output is a **hostname** with no
 * scheme, while a custom-domain setup or a hand-set variable may well include
 * one. Accepting both and settling on `https://<host>` here means no call site
 * has to guess, and a stray trailing slash cannot produce `//oauth2/token`.
 * Anything not `https:` is rejected — an OAuth code exchange over plaintext
 * would put a refresh token on the wire.
 */
function normalizeAuthDomain(raw: string | undefined): string | null {
  const trimmed = raw?.trim().replace(/\/+$/, '') ?? '';
  if (trimmed === '') {
    return null;
  }
  if (trimmed.startsWith('https://')) {
    return trimmed.length > 'https://'.length ? trimmed : null;
  }
  // A scheme we did not just accept is a misconfiguration, not something to
  // repair by prefixing `https://` in front of it.
  if (trimmed.includes('://')) {
    return null;
  }
  return `https://${trimmed}`;
}

/** Bundles an environment's label, host and API base URL together. */
export function environmentInfo(env: AppEnvironment = appEnvironment): EnvironmentInfo {
  return {
    name: env,
    label: environmentLabels[env],
    host: environmentHosts[env],
    apiBaseUrl: environmentApiBaseUrls[env],
  };
}
