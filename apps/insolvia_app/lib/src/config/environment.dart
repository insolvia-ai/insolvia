/// Which deployment environment this build targets.
///
/// Selected at build time via `--dart-define=INSOLVIA_ENV=...`:
/// ```bash
/// flutter build web --dart-define=INSOLVIA_ENV=staging
/// ```
enum AppEnvironment {
  local,
  staging,
  production;

  /// Resolves the environment from the compile-time `INSOLVIA_ENV` define,
  /// defaulting to [AppEnvironment.local] for `flutter run` during development.
  static AppEnvironment resolve() {
    const raw = String.fromEnvironment('INSOLVIA_ENV', defaultValue: 'local');
    return switch (raw) {
      'production' || 'prod' => AppEnvironment.production,
      'staging' => AppEnvironment.staging,
      _ => AppEnvironment.local,
    };
  }

  bool get isProduction => this == AppEnvironment.production;

  /// Human-readable label shown in the UI so staging vs prod is unmistakable.
  String get label => switch (this) {
        AppEnvironment.local => 'Local',
        AppEnvironment.staging => 'Staging',
        AppEnvironment.production => 'Production',
      };

  /// The public host this environment serves from (informational for now).
  String get host => switch (this) {
        AppEnvironment.local => 'localhost',
        AppEnvironment.staging => 'staging-app.insolvia.ai',
        AppEnvironment.production => 'app.insolvia.ai',
      };

  /// The base URL of the Insolvia API this build talks to.
  ///
  /// Exhaustive switch with no default arm, like [host] — the compiler
  /// forces a new environment to declare its API explicitly. Combined with
  /// [resolve] falling back to [local] for any unrecognised `INSOLVIA_ENV`,
  /// no build can *silently* end up pointing at the production API: only
  /// an explicit `INSOLVIA_ENV=production` define reaches it (issue #64).
  ///
  /// Local is the `services/api` docker-compose port (see
  /// services/api/docker-compose.yml — `127.0.0.1:8080`).
  String get apiBaseUrl => switch (this) {
        AppEnvironment.local => 'http://localhost:8080',
        AppEnvironment.staging => 'https://staging-api.insolvia.ai',
        AppEnvironment.production => 'https://api.insolvia.ai',
      };
}
