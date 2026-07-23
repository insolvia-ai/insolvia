// These tests ARE the contract pin.
//
// There is no OpenAPI spec and no codegen (see README.md): the JSON shapes
// asserted here — request paths, methods, field names, status codes, and
// error bodies — are the authoritative record of what `services/api`
// actually speaks. If the API's contract changes, these tests must fail;
// keep every literal in sync with:
//   services/api/src/insolvia_api/api/routes/{health,waitlist}.py
//   services/api/src/insolvia_api/api/app_factory.py (error handlers)
//   services/api/src/insolvia_api/core/waitlist.py
import 'dart:convert';

import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:insolvia_api_client/insolvia_api_client.dart';
import 'package:test/test.dart';

void main() {
  group('health', () {
    test('GETs /health and maps the four contract fields', () async {
      late http.Request seen;
      final client = InsolviaApiClient(
        'http://localhost:8080',
        httpClient: MockClient((request) async {
          seen = request;
          return http.Response(
            jsonEncode({
              'status': 'ok',
              'service': 'insolvia-api',
              'version': '0.1.0',
              'environment': 'staging',
            }),
            200,
            headers: {'content-type': 'application/json'},
          );
        }),
      );

      final status = await client.health();

      expect(seen.method, 'GET');
      expect(seen.url.toString(), 'http://localhost:8080/health');
      expect(status.status, 'ok');
      expect(status.service, 'insolvia-api');
      expect(status.version, '0.1.0');
      expect(status.environment, 'staging');
    });

    test('a trailing slash on baseUrl does not double the path', () async {
      late http.Request seen;
      final client = InsolviaApiClient(
        'http://localhost:8080/',
        httpClient: MockClient((request) async {
          seen = request;
          return http.Response(
            jsonEncode({
              'status': 'ok',
              'service': 'insolvia-api',
              'version': '0.1.0',
              'environment': 'local',
            }),
            200,
          );
        }),
      );

      await client.health();
      expect(seen.url.path, '/health');
    });
  });

  group('joinWaitlist', () {
    const submission = WaitlistSubmission(
      name: 'Ada Lovelace',
      firm: 'Lovelace Law LLC',
      email: 'ada@lovelace.law',
    );

    test('POSTs /v1/waitlist as JSON and maps the 201 confirmation', () async {
      late http.Request seen;
      final client = InsolviaApiClient(
        'https://staging-api.insolvia.ai',
        httpClient: MockClient((request) async {
          seen = request;
          return http.Response(
            jsonEncode({
              'id': '0b1e9a4e-8c1f-4a7e-9c39-b1c5b7d9f2a1',
              'submittedAt': '2026-07-23T09:15:00.123Z',
            }),
            201,
            headers: {'content-type': 'application/json'},
          );
        }),
      );

      final confirmation = await client.joinWaitlist(submission);

      expect(seen.method, 'POST');
      expect(
        seen.url.toString(),
        'https://staging-api.insolvia.ai/v1/waitlist',
      );
      expect(seen.headers['Content-Type'], startsWith('application/json'));
      final body = jsonDecode(seen.body) as Map<String, dynamic>;
      expect(body, {
        'name': 'Ada Lovelace',
        'firm': 'Lovelace Law LLC',
        'email': 'ada@lovelace.law',
      });
      // Optional fields are omitted when null, not sent as null/"".
      expect(body.containsKey('currentSoftware'), isFalse);
      expect(body.containsKey('message'), isFalse);
      expect(body.containsKey('host'), isFalse);

      expect(confirmation.id, '0b1e9a4e-8c1f-4a7e-9c39-b1c5b7d9f2a1');
      expect(confirmation.submittedAt, '2026-07-23T09:15:00.123Z');
      expect(
        confirmation.submittedAtUtc,
        DateTime.utc(2026, 7, 23, 9, 15, 0, 123),
      );
    });

    test('sends optional fields under their exact wire names when set',
        () async {
      late Map<String, dynamic> sentBody;
      final client = InsolviaApiClient(
        'http://localhost:8080',
        httpClient: MockClient((request) async {
          sentBody = jsonDecode(request.body) as Map<String, dynamic>;
          return http.Response(
            jsonEncode({'id': 'x', 'submittedAt': '2026-07-23T00:00:00.000Z'}),
            201,
          );
        }),
      );

      await client.joinWaitlist(
        const WaitlistSubmission(
          name: 'Ada Lovelace',
          firm: 'Lovelace Law LLC',
          email: 'ada@lovelace.law',
          currentSoftware: 'Best Case',
          message: 'Interested in the desktop app.',
          host: 'www.insolvia.ai',
        ),
      );

      expect(sentBody['currentSoftware'], 'Best Case');
      expect(sentBody['message'], 'Interested in the desktop app.');
      expect(sentBody['host'], 'www.insolvia.ai');
    });

    test(
        'maps a 400 {"error","fields"} body to ApiValidationException '
        'carrying the per-field messages verbatim', () async {
      // Exact shape produced by app_factory's FieldValidationError handler.
      final client = InsolviaApiClient(
        'http://localhost:8080',
        httpClient: MockClient(
          (request) async => http.Response(
            jsonEncode({
              'error': 'ValidationError',
              'fields': {
                'name': 'Please tell us your name.',
                'email': "That doesn't look like a valid email address.",
              },
            }),
            400,
          ),
        ),
      );

      await expectLater(
        client.joinWaitlist(submission),
        throwsA(
          isA<ApiValidationException>()
              .having((e) => e.statusCode, 'statusCode', 400)
              .having((e) => e.fields, 'fields', {
            'name': 'Please tell us your name.',
            'email': "That doesn't look like a valid email address.",
          }),
        ),
      );
    });

    test(
        'a 400 without "fields" is a plain ApiException, not a validation '
        'one', () async {
      // Shape produced by the non-field ValidationError handler.
      final client = InsolviaApiClient(
        'http://localhost:8080',
        httpClient: MockClient(
          (request) async => http.Response(
            jsonEncode({
              'error': 'ValidationError',
              'message': 'request body must be a JSON object',
            }),
            400,
          ),
        ),
      );

      await expectLater(
        client.joinWaitlist(submission),
        throwsA(
          isA<ApiException>()
              .having((e) => e.statusCode, 'statusCode', 400)
              .having(
                (e) => e,
                'runtimeType',
                isNot(isA<ApiValidationException>()),
              ),
        ),
      );
    });

    test('maps a 500 to ApiException with the status and raw body', () async {
      // Exact shape produced by the catch-all Exception handler.
      final body = jsonEncode(
        {'error': 'InternalError', 'message': 'request failed'},
      );
      final client = InsolviaApiClient(
        'http://localhost:8080',
        httpClient: MockClient((request) async => http.Response(body, 500)),
      );

      await expectLater(
        client.joinWaitlist(submission),
        throwsA(
          isA<ApiException>()
              .having((e) => e.statusCode, 'statusCode', 500)
              .having((e) => e.body, 'body', body)
              .having((e) => e.message, 'message', contains('InternalError')),
        ),
      );
    });

    test('a success status with a malformed JSON body throws ApiException',
        () async {
      final client = InsolviaApiClient(
        'http://localhost:8080',
        httpClient: MockClient(
          (request) async => http.Response('<html>gateway timeout</html>', 201),
        ),
      );

      await expectLater(
        client.joinWaitlist(submission),
        throwsA(
          isA<ApiException>()
              .having((e) => e.message, 'message', contains('not valid JSON')),
        ),
      );
    });

    test('a non-2xx with an unparseable body still throws ApiException',
        () async {
      final client = InsolviaApiClient(
        'http://localhost:8080',
        httpClient: MockClient(
          (request) async => http.Response('Bad Gateway', 502),
        ),
      );

      await expectLater(
        client.joinWaitlist(submission),
        throwsA(
          isA<ApiException>()
              .having((e) => e.statusCode, 'statusCode', 502)
              .having((e) => e.body, 'body', 'Bad Gateway'),
        ),
      );
    });

    test('transport failures propagate untouched (no ApiException wrapping)',
        () async {
      final client = InsolviaApiClient(
        'http://localhost:8080',
        httpClient: MockClient(
          (request) async =>
              throw http.ClientException('Connection refused', request.url),
        ),
      );

      await expectLater(
        client.joinWaitlist(submission),
        throwsA(isA<http.ClientException>()),
      );
    });
  });
}
