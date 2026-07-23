/// Request/response models mirroring the Insolvia API's exact JSON contract.
///
/// Field names match the wire format (camelCase, e.g. `currentSoftware`,
/// `submittedAt`) as produced by `services/api` — see
/// `services/api/src/insolvia_api/core/waitlist.py` and
/// `api/routes/{health,waitlist}.py`. The tests in this package pin that
/// contract; change these models only together with the API.
library;

/// The `GET /health` response:
/// `{"status", "service", "version", "environment"}`.
class HealthStatus {
  const HealthStatus({
    required this.status,
    required this.service,
    required this.version,
    required this.environment,
  });

  factory HealthStatus.fromJson(Map<String, dynamic> json) => HealthStatus(
        status: json['status'] as String,
        service: json['service'] as String,
        version: json['version'] as String,
        environment: json['environment'] as String,
      );

  /// `"ok"` when the service is healthy.
  final String status;

  /// The service name (e.g. `insolvia-api`).
  final String service;

  /// The deployed `insolvia_api` package version.
  final String version;

  /// The environment the API believes it is running in
  /// (`local` / `staging` / `production`).
  final String environment;

  Map<String, dynamic> toJson() => {
        'status': status,
        'service': service,
        'version': version,
        'environment': environment,
      };
}

/// The `POST /v1/waitlist` request body.
///
/// [name], [firm], and [email] are required by the API; the rest are
/// optional and omitted from the JSON entirely when `null` (the API treats
/// absent and empty identically, but omitting keeps requests minimal and
/// mirrors what the marketing form sends).
class WaitlistSubmission {
  const WaitlistSubmission({
    required this.name,
    required this.firm,
    required this.email,
    this.currentSoftware,
    this.message,
    this.host,
  });

  factory WaitlistSubmission.fromJson(Map<String, dynamic> json) =>
      WaitlistSubmission(
        name: json['name'] as String,
        firm: json['firm'] as String,
        email: json['email'] as String,
        currentSoftware: json['currentSoftware'] as String?,
        message: json['message'] as String?,
        host: json['host'] as String?,
      );

  /// The submitter's name. Required; max 200 characters.
  final String name;

  /// The submitter's firm. Required; max 200 characters.
  final String firm;

  /// A work email address. Required; max 320 characters.
  final String email;

  /// The bankruptcy software the firm uses today. Optional; max 100.
  final String? currentSoftware;

  /// A free-text message. Optional; max 2000 characters.
  final String? message;

  /// The serving host the submission came from (set server-to-server by the
  /// marketing SSR action, not visitor input). Optional; max 253.
  final String? host;

  Map<String, dynamic> toJson() => {
        'name': name,
        'firm': firm,
        'email': email,
        if (currentSoftware != null) 'currentSoftware': currentSoftware,
        if (message != null) 'message': message,
        if (host != null) 'host': host,
      };
}

/// The `POST /v1/waitlist` 201 response: `{"id", "submittedAt"}`.
class WaitlistConfirmation {
  const WaitlistConfirmation({required this.id, required this.submittedAt});

  factory WaitlistConfirmation.fromJson(Map<String, dynamic> json) =>
      WaitlistConfirmation(
        id: json['id'] as String,
        submittedAt: json['submittedAt'] as String,
      );

  /// The server-generated submission id (a UUID).
  final String id;

  /// The server's UTC submission timestamp, kept verbatim as the wire's
  /// millisecond-precision ISO-8601 `Z` string (it doubles as a sort key
  /// server-side). Use [submittedAtUtc] for a parsed value.
  final String submittedAt;

  /// [submittedAt] parsed as a UTC [DateTime].
  DateTime get submittedAtUtc => DateTime.parse(submittedAt);

  Map<String, dynamic> toJson() => {'id': id, 'submittedAt': submittedAt};
}
