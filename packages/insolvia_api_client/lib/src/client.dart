import 'dart:convert';

import 'package:http/http.dart' as http;

import 'exceptions.dart';
import 'models.dart';

/// A typed client for the Insolvia API.
///
/// ```dart
/// final client = InsolviaApiClient('https://staging-api.insolvia.ai');
/// final status = await client.health();
/// ```
///
/// Error model:
/// - 400 with per-field messages → [ApiValidationException];
/// - any other unexpected status, or an undecodable success body →
///   [ApiException];
/// - transport failures (DNS, refused connection, …) propagate untouched
///   as `package:http` exceptions.
class InsolviaApiClient {
  /// [baseUrl] is the API origin, with or without a trailing slash —
  /// e.g. `http://localhost:8080` or `https://api.insolvia.ai`.
  ///
  /// Pass [httpClient] to control transport (and in tests, to inject a
  /// `MockClient`); the caller then owns its lifecycle. When omitted, the
  /// client creates its own and [close] disposes it.
  InsolviaApiClient(String baseUrl, {http.Client? httpClient})
      : _baseUrl = baseUrl.endsWith('/')
            ? baseUrl.substring(0, baseUrl.length - 1)
            : baseUrl,
        _http = httpClient ?? http.Client(),
        _ownsHttpClient = httpClient == null;

  final String _baseUrl;
  final http.Client _http;
  final bool _ownsHttpClient;

  static const _jsonHeaders = {
    'Accept': 'application/json',
    'Content-Type': 'application/json',
  };

  /// `GET /health` — the API's liveness/identity endpoint.
  Future<HealthStatus> health() async {
    final response = await _http.get(
      Uri.parse('$_baseUrl/health'),
      headers: const {'Accept': 'application/json'},
    );
    return HealthStatus.fromJson(_decodeExpected(response, 200));
  }

  /// `POST /v1/waitlist` — submit a waitlist entry.
  ///
  /// Returns the server's confirmation on 201. Throws
  /// [ApiValidationException] on a 400 with per-field messages.
  Future<WaitlistConfirmation> joinWaitlist(
    WaitlistSubmission submission,
  ) async {
    final response = await _http.post(
      Uri.parse('$_baseUrl/v1/waitlist'),
      headers: _jsonHeaders,
      body: jsonEncode(submission.toJson()),
    );
    return WaitlistConfirmation.fromJson(_decodeExpected(response, 201));
  }

  /// Releases the underlying HTTP client — but only if this instance
  /// created it. An injected `httpClient` stays the caller's to close.
  void close() {
    if (_ownsHttpClient) {
      _http.close();
    }
  }

  /// Decodes [response] as a JSON object when its status is
  /// [expectedStatus]; otherwise maps the failure to a typed exception.
  Map<String, dynamic> _decodeExpected(
    http.Response response,
    int expectedStatus,
  ) {
    if (response.statusCode != expectedStatus) {
      throw _errorFor(response);
    }
    final Object? decoded;
    try {
      decoded = jsonDecode(response.body);
    } on FormatException {
      throw ApiException(
        statusCode: response.statusCode,
        body: response.body,
        message: 'response body was not valid JSON',
      );
    }
    if (decoded is! Map<String, dynamic>) {
      throw ApiException(
        statusCode: response.statusCode,
        body: response.body,
        message: 'response body was not a JSON object',
      );
    }
    return decoded;
  }

  /// Maps a non-success response to the most specific exception available:
  /// `{"error": ..., "fields": {...}}` → [ApiValidationException], anything
  /// else (including unparseable bodies) → [ApiException].
  ApiException _errorFor(http.Response response) {
    Object? decoded;
    try {
      decoded = jsonDecode(response.body);
    } on FormatException {
      decoded = null;
    }
    if (decoded is Map<String, dynamic>) {
      final fields = decoded['fields'];
      if (fields is Map<String, dynamic>) {
        return ApiValidationException(
          statusCode: response.statusCode,
          body: response.body,
          fields: fields.map((key, value) => MapEntry(key, value.toString())),
        );
      }
      final error = decoded['error'];
      final message = decoded['message'];
      if (error is String) {
        return ApiException(
          statusCode: response.statusCode,
          body: response.body,
          message: message is String ? '$error: $message' : error,
        );
      }
    }
    return ApiException(statusCode: response.statusCode, body: response.body);
  }
}
