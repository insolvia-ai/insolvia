import {
  APP_ENVIRONMENTS,
  appEnvironment,
  environmentApiBaseUrls,
  environmentHosts,
  environmentInfo,
  isProduction,
  resolveAuthConfig,
  resolveEnvironment,
} from '@/config/environment';

describe('environment API base URLs', () => {
  it('maps every environment to its own API base URL', () => {
    expect(environmentApiBaseUrls.local).toBe('http://localhost:8080');
    expect(environmentApiBaseUrls.staging).toBe('https://staging-api.insolvia.ai');
    expect(environmentApiBaseUrls.production).toBe('https://api.insolvia.ai');
  });

  it('declares a host and an API for every environment', () => {
    // The compile-time guarantee is the `satisfies Record<AppEnvironment, …>` +
    // `never` assertions in environment.ts; this is the runtime half, and it
    // iterates APP_ENVIRONMENTS so a future environment is covered the moment
    // it exists.
    for (const env of APP_ENVIRONMENTS) {
      expect(environmentHosts[env]).toBeTruthy();
      expect(environmentApiBaseUrls[env]).toBeTruthy();
    }
  });

  it('never lets a non-production environment resolve to the production API', () => {
    // Issue #64: a staging build silently pointing at production is the failure
    // mode being designed out.
    for (const env of APP_ENVIRONMENTS) {
      if (isProduction(env)) continue;
      expect(new URL(environmentApiBaseUrls[env]).hostname).not.toBe('api.insolvia.ai');
    }
  });
});

describe('resolveEnvironment', () => {
  it('resolves the values a build actually passes', () => {
    expect(resolveEnvironment('staging')).toBe('staging');
    expect(resolveEnvironment('production')).toBe('production');
    expect(resolveEnvironment('prod')).toBe('production');
    expect(resolveEnvironment('local')).toBe('local');
  });

  it.each([undefined, '', 'Production', 'PRODUCTION', 'prd', 'stagingg', 'nonsense'])(
    'falls back to local — never production — for %p',
    (raw) => {
      const env = resolveEnvironment(raw);
      expect(env).toBe('local');
      expect(new URL(environmentApiBaseUrls[env]).hostname).toBe('localhost');
    },
  );

  it('falls back to local when EXPO_PUBLIC_INSOLVIA_ENV is unset', () => {
    // Tests run without the variable, so this exercises the same arm the
    // module-level `appEnvironment` takes in an unconfigured build.
    expect(resolveEnvironment()).toBe('local');
    expect(appEnvironment).toBe('local');
  });
});

describe('environmentInfo', () => {
  it('bundles the label, host and API base URL together', () => {
    expect(environmentInfo('staging')).toEqual({
      name: 'staging',
      label: 'Staging',
      host: 'staging-app.insolvia.ai',
      apiBaseUrl: 'https://staging-api.insolvia.ai',
    });
  });

  it('defaults to the environment this bundle was built for', () => {
    expect(environmentInfo().name).toBe(appEnvironment);
  });
});

describe('resolveAuthConfig', () => {
  // Obviously-fake values throughout: this repository is public, and a real
  // hosted domain or app client id must never appear in it. `.test` is reserved
  // by RFC 6761 and can never be registered.
  const domain = 'insolvia-test.auth.example.test';
  const clientId = 'test-client-id-0000000000';

  it('accepts a bare hostname and settles on an https origin', () => {
    // Terraform's `aws_cognito_user_pool_domain` output is a hostname with no
    // scheme, so this is the shape the deploy workflow actually passes.
    expect(resolveAuthConfig(domain, clientId)).toEqual({
      domain: `https://${domain}`,
      clientId,
    });
  });

  it('accepts an https URL unchanged, trailing slash and all', () => {
    // A stray trailing slash would otherwise produce `//oauth2/token`.
    expect(resolveAuthConfig(`https://${domain}/`, clientId)?.domain).toBe(`https://${domain}`);
    expect(resolveAuthConfig(`https://${domain}`, clientId)?.domain).toBe(`https://${domain}`);
  });

  it('rejects a non-https scheme rather than repairing it', () => {
    // An OAuth code exchange over plaintext puts a refresh token on the wire.
    expect(resolveAuthConfig(`http://${domain}`, clientId)).toBeNull();
  });

  it.each([
    ['both absent', undefined, undefined],
    ['no domain', undefined, clientId],
    ['no client id', domain, undefined],
    ['a blank domain', '   ', clientId],
    ['a blank client id', domain, '  '],
  ])('reports %s as unconfigured rather than half-configured', (_label, rawDomain, rawClientId) => {
    // `null` is a supported state: the sign-in screen says so, instead of
    // building a redirect to `https://undefined/oauth2/authorize`.
    expect(resolveAuthConfig(rawDomain, rawClientId)).toBeNull();
  });

  it('is unconfigured in this test run, which is the local default', () => {
    // Tests run without the EXPO_PUBLIC_COGNITO_* variables, so this exercises
    // the same arm an unconfigured `local` build takes.
    expect(resolveAuthConfig()).toBeNull();
  });
});
