import 'package:flutter_test/flutter_test.dart';
import 'package:insolvia_app/src/config/environment.dart';

void main() {
  group('AppEnvironment.apiBaseUrl', () {
    test('maps every environment to its own API base URL', () {
      expect(AppEnvironment.local.apiBaseUrl, 'http://localhost:8080');
      expect(
        AppEnvironment.staging.apiBaseUrl,
        'https://staging-api.insolvia.ai',
      );
      expect(
        AppEnvironment.production.apiBaseUrl,
        'https://api.insolvia.ai',
      );
    });

    test('no non-production environment ever resolves to the production API',
        () {
      // Issue #64: a staging desktop build silently pointing at production
      // is the failure mode to design out. Iterate `values` so a future
      // environment is covered the moment it exists.
      for (final env in AppEnvironment.values) {
        if (env.isProduction) continue;
        expect(
          Uri.parse(env.apiBaseUrl).host,
          isNot('api.insolvia.ai'),
          reason: '$env must not point at the production API',
        );
      }
    });

    test(
        'an unset/unknown INSOLVIA_ENV falls back to local, whose API is '
        'localhost — never production', () {
      // Tests run without --dart-define, so this exercises the fallback arm.
      final env = AppEnvironment.resolve();
      expect(env, AppEnvironment.local);
      expect(Uri.parse(env.apiBaseUrl).host, 'localhost');
    });
  });
}
