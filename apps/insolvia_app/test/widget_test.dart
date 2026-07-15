import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:insolvia_design_system/insolvia_design_system.dart';
import 'package:insolvia_app/src/app.dart';
import 'package:insolvia_app/src/config/environment.dart';

void main() {
  testWidgets('renders the themed hello screen with brand chrome',
      (tester) async {
    await tester.pumpWidget(const InsolviaApp());

    expect(find.byType(BrandWordmark), findsOneWidget);
    expect(find.text('Hello, Insolvia'), findsOneWidget);
    expect(find.byType(AppButton), findsNWidgets(2));
  });

  testWidgets('shows the active environment badge', (tester) async {
    await tester.pumpWidget(const InsolviaApp());
    // Default build (no --dart-define) resolves to local.
    expect(AppEnvironment.resolve(), AppEnvironment.local);
    expect(find.text('LOCAL'), findsOneWidget);
  });

  testWidgets('primary CTA shows a snackbar', (tester) async {
    await tester.pumpWidget(const InsolviaApp());
    await tester.tap(find.widgetWithText(FilledButton, 'Get started'));
    await tester.pump();
    expect(find.text('Coming soon.'), findsOneWidget);
  });
}
