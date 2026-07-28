import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:insolvia_design_system/insolvia_design_system.dart';
import 'package:insolvia_app/app.dart';
import 'package:insolvia_app/ui/auth/auth_callback_screen.dart';
import 'package:insolvia_app/ui/home/home_screen.dart';
import 'package:insolvia_app/routing/app_router.dart';
import 'package:insolvia_app/ui/core/not_found_screen.dart';

void main() {
  group('deep links', () {
    testWidgets('/ lands on the home shell', (tester) async {
      await tester.pumpWidget(
        InsolviaApp(router: createAppRouter(initialLocation: '/')),
      );

      expect(find.byType(HomeScreen), findsOneWidget);
    });

    testWidgets(
        'the Cognito callback path resolves to a screen, not a router error',
        (tester) async {
      // infra/modules/auth/main.tf registers `<origin>/auth/callback` as the
      // web client's callback URL. Sign-in is not implemented, but the path
      // must not fall through to go_router's exception page.
      await tester.pumpWidget(
        InsolviaApp(
          router: createAppRouter(initialLocation: AppRoutePath.authCallback),
        ),
      );

      expect(find.byType(AuthCallbackScreen), findsOneWidget);
      expect(find.byType(NotFoundScreen), findsNothing);
      expect(find.byType(BrandWordmark), findsOneWidget);
      expect(find.text('Sign-in is not enabled yet'), findsOneWidget);
    });

    testWidgets('the callback screen leads back to home', (tester) async {
      await tester.pumpWidget(
        InsolviaApp(
          router: createAppRouter(initialLocation: AppRoutePath.authCallback),
        ),
      );

      await tester.tap(
        find.widgetWithText(FilledButton, 'Continue to Insolvia'),
      );
      await tester.pumpAndSettle();

      expect(find.byType(HomeScreen), findsOneWidget);
    });

    testWidgets('an unknown path renders branded not-found chrome',
        (tester) async {
      await tester.pumpWidget(
        InsolviaApp(router: createAppRouter(initialLocation: '/nope')),
      );

      expect(find.byType(NotFoundScreen), findsOneWidget);
      expect(find.byType(BrandWordmark), findsOneWidget);
      expect(find.textContaining('/nope'), findsOneWidget);
    });

    testWidgets('not-found leads back to home', (tester) async {
      await tester.pumpWidget(
        InsolviaApp(router: createAppRouter(initialLocation: '/nope')),
      );

      await tester.tap(find.widgetWithText(FilledButton, 'Back to home'));
      await tester.pumpAndSettle();

      expect(find.byType(HomeScreen), findsOneWidget);
    });
  });

  group('route declarations', () {
    test('the callback path matches the infra-registered URL exactly', () {
      // A drift guard: `web_callback_urls` in infra/modules/auth/main.tf is
      // `"${o}/auth/callback"`. Changing one side without the other silently
      // breaks the return leg of sign-in.
      expect(AppRoutePath.authCallback, '/auth/callback');
    });
  });
}
