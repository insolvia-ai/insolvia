import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:insolvia_design_system/insolvia_design_system.dart';
import 'package:insolvia_app/app.dart';
import 'package:insolvia_app/config/environment.dart';
import 'package:insolvia_app/ui/home/widgets/env_badge.dart';

void main() {
  group('signed-in shell home', () {
    testWidgets('renders the branded chrome and the shell content',
        (tester) async {
      await tester.pumpWidget(const InsolviaApp());

      // The wordmark is the shell's identity — it comes from AppScaffold, so
      // this also asserts the screen is inside the design system's frame.
      expect(find.byType(AppScaffold), findsOneWidget);
      expect(find.byType(BrandWordmark), findsOneWidget);
      expect(find.text('Your case workspace'), findsOneWidget);
      expect(find.byType(AppButton), findsNWidgets(2));
    });

    testWidgets('reflects the resolved environment in the badge and the body',
        (tester) async {
      await tester.pumpWidget(const InsolviaApp());

      // Tests run without --dart-define, so this is the `local` fallback arm.
      final env = AppEnvironment.resolve();
      expect(env, AppEnvironment.local);

      expect(find.byType(EnvBadge), findsOneWidget);
      expect(find.text(env.label.toUpperCase()), findsOneWidget);
      expect(
        find.text('Serving ${env.label.toLowerCase()} · ${env.host}'),
        findsOneWidget,
      );
    });

    testWidgets(
        'content column is constrained, so it does not stretch across '
        'a desktop-width window', (tester) async {
      tester.view.physicalSize = const Size(2560, 1440);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.reset);

      await tester.pumpWidget(const InsolviaApp());

      final heading = tester.getSize(find.text('Your case workspace'));
      expect(heading.width, lessThan(1200));
    });

    testWidgets('primary CTA shows a snackbar', (tester) async {
      await tester.pumpWidget(const InsolviaApp());

      await tester.tap(find.widgetWithText(FilledButton, 'Start a case'));
      await tester.pump();

      expect(
        find.text('Case tools arrive in a later release.'),
        findsOneWidget,
      );
    });
  });

  group('theming', () {
    // Whether BOTH themes are wired at all is `app.dart`'s job, covered in
    // test/app_test.dart. This asserts the screen's own text defers to
    // whichever theme is active.
    testWidgets(
        'no screen text is painted with a literal color — everything '
        'resolves through the theme', (tester) async {
      await tester.pumpWidget(const InsolviaApp());

      final subtle = InsolviaSemanticColors.light.muted;
      final body = tester.widget<Text>(
        find.textContaining('This is the shell every Insolvia screen'),
      );
      expect(body.style?.color, subtle);
    });
  });
}
