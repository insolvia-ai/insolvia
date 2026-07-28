import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:insolvia_design_system/insolvia_design_system.dart';
import 'package:insolvia_app/app.dart';

void main() {
  group('theme wiring', () {
    // `InsolviaApp` supplies both themes and leaves ThemeMode.system as the
    // default, so the shell follows the OS on desktop and the browser's
    // `prefers-color-scheme` on web. A dark theme that is declared but never
    // reachable looks identical to a correct one until someone runs the app in
    // dark mode, so both arms are asserted here rather than in a screen test.
    testWidgets('light mode paints the light canvas', (tester) async {
      await tester.pumpWidget(const InsolviaApp());

      final scaffold = tester.widget<Scaffold>(find.byType(Scaffold));
      expect(scaffold.backgroundColor, InsolviaSemanticColors.light.bg);
    });

    testWidgets('dark mode paints the dark canvas', (tester) async {
      tester.platformDispatcher.platformBrightnessTestValue = Brightness.dark;
      addTearDown(tester.platformDispatcher.clearPlatformBrightnessTestValue);

      await tester.pumpWidget(const InsolviaApp());

      final scaffold = tester.widget<Scaffold>(find.byType(Scaffold));
      expect(scaffold.backgroundColor, InsolviaSemanticColors.dark.bg);
    });
  });
}
