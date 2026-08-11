/**
 * Per-environment configuration, as a committed map rather than env files.
 *
 * Everything here is PUBLIC by construction — the Google client id appears in
 * every sign-in redirect, and the API hosts are DNS — so the values live in
 * code where a diff reviews them, exactly as the app's
 * `environmentApiBaseUrls` map does. The build injects ONE value,
 * `VITE_INSOLVIA_ENV`, and this module refuses anything it does not
 * recognise: a typo'd environment must fail the build's smoke test, not
 * quietly verify dev tokens in production.
 *
 * The dev client id is the SAME for every developer machine (a single
 * Internal OAuth client redirecting to localhost:3100 — see
 * infra/envs/dev/variables.tf, which commits the same value for the
 * service side).
 */

export type InsolviaEnvironment = "local" | "staging" | "production";

export interface AdminConfig {
  readonly environment: InsolviaEnvironment;
  /** The Google Workspace OAuth client this build signs in against. */
  readonly googleClientId: string;
  /** The admin service base URL, no trailing slash. */
  readonly apiBaseUrl: string;
}

const CONFIGS: Record<InsolviaEnvironment, AdminConfig> = {
  local: {
    environment: "local",
    googleClientId:
      "925851246989-tttlgepq8mjfhmtlkmgikrtv4kskv0pk.apps.googleusercontent.com",
    apiBaseUrl: "http://127.0.0.1:8090",
  },
  staging: {
    environment: "staging",
    googleClientId:
      "925851246989-a4prtrjp0p5j1q71g8pbv4irqu7ibsce.apps.googleusercontent.com",
    apiBaseUrl: "https://staging-admin-api.insolvia.ai",
  },
  production: {
    environment: "production",
    googleClientId:
      "925851246989-115l1fsln1ntv52uv3bhg29k819fram7.apps.googleusercontent.com",
    apiBaseUrl: "https://admin-api.insolvia.ai",
  },
};

/** Resolve the build's configuration, throwing on an unknown environment. */
export function resolveConfig(raw: string | undefined): AdminConfig {
  const name = raw === undefined || raw === "" ? "local" : raw;
  const config = CONFIGS[name as InsolviaEnvironment];
  if (config === undefined) {
    throw new Error(
      `VITE_INSOLVIA_ENV is "${name}" but must be local, staging, or production`,
    );
  }
  return config;
}

export const config: AdminConfig = resolveConfig(
  import.meta.env.VITE_INSOLVIA_ENV as string | undefined,
);
