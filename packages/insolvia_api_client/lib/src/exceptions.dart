/// Typed failures for Insolvia API calls.
///
/// Only *API-level* failures are modelled here. Transport failures (DNS,
/// refused connection, timeout) propagate untouched as whatever
/// `package:http` throws (`http.ClientException`, `SocketException`, …) —
/// the caller can distinguish "the API rejected this" from "the network is
/// down" by exception type alone.
library;

/// The API answered with an unexpected status or an undecodable body.
///
/// Carries the raw [statusCode] and [body] so callers (and logs) can see
/// exactly what came back. Subclassed by [ApiValidationException] for the
/// one failure shape callers are expected to handle field-by-field.
class ApiException implements Exception {
  ApiException({
    required this.statusCode,
    required this.body,
    String? message,
  }) : message = message ?? 'API request failed with status $statusCode';

  /// The HTTP status code the API responded with.
  final int statusCode;

  /// The raw (undecoded) response body, for diagnostics.
  final String body;

  /// A human-readable summary of the failure.
  final String message;

  @override
  String toString() => 'ApiException($statusCode): $message';
}

/// A 400 `{"error": "ValidationError", "fields": {...}}` response —
/// per-field messages keyed by the request's JSON field names, exactly as
/// the API sent them, so a form can surface each message next to its input.
class ApiValidationException extends ApiException {
  ApiValidationException({
    required super.statusCode,
    required super.body,
    required this.fields,
  }) : super(
          message:
              'validation failed: ${(fields.keys.toList()..sort()).join(', ')}',
        );

  /// Per-field validation messages, keyed by JSON field name
  /// (e.g. `name`, `firm`, `email`).
  final Map<String, String> fields;

  @override
  String toString() => 'ApiValidationException($statusCode): $message';
}
