import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:insolvia_design_system/insolvia_design_system.dart';

void main() {
  group('InsolviaTheme', () {
    test('light theme carries Insolvia extensions', () {
      final theme = InsolviaTheme.light();
      expect(theme.extension<InsolviaColors>(), isNotNull);
      expect(theme.extension<InsolviaSpacings>(), isNotNull);
      expect(theme.colorScheme.brightness, Brightness.light);
      expect(theme.extension<InsolviaColors>()!.canvas, InsolviaPalette.paper);
    });

    test('dark theme carries Insolvia extensions', () {
      final theme = InsolviaTheme.dark();
      expect(theme.extension<InsolviaColors>(), isNotNull);
      expect(theme.colorScheme.brightness, Brightness.dark);
    });

    testWidgets('context extensions resolve from the theme', (tester) async {
      late InsolviaColors colors;
      late InsolviaSpacings spacing;
      await tester.pumpWidget(
        MaterialApp(
          theme: InsolviaTheme.light(),
          home: Builder(
            builder: (context) {
              colors = context.insolviaColors;
              spacing = context.insolviaSpacing;
              return const SizedBox.shrink();
            },
          ),
        ),
      );
      expect(colors.brandAccent, InsolviaPalette.brass);
      expect(spacing.md, InsolviaSpacing.md);
    });
  });
}
