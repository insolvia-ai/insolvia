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

/** Bundles an environment's label, host and API base URL together. */
export function environmentInfo(env: AppEnvironment = appEnvironment): EnvironmentInfo {
  return {
    name: env,
    label: environmentLabels[env],
    host: environmentHosts[env],
    apiBaseUrl: environmentApiBaseUrls[env],
  };
}
