import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:insolvia_design_system/insolvia_design_system.dart';

Widget _host(Widget child) =>
    MaterialApp(theme: InsolviaTheme.light(), home: child);

void main() {
  testWidgets('AppButton primary renders label and fires onPressed',
      (tester) async {
    var tapped = 0;
    await tester.pumpWidget(_host(
      Scaffold(
        body: AppButton(label: 'File case', onPressed: () => tapped++),
      ),
    ));

    expect(find.text('File case'), findsOneWidget);
    expect(find.byType(FilledButton), findsOneWidget);

    await tester.tap(find.byType(FilledButton));
    expect(tapped, 1);
  });

  testWidgets('AppButton secondary renders as an outlined button',
      (tester) async {
    await tester.pumpWidget(_host(
      Scaffold(
        body: AppButton(
          label: 'Cancel',
          variant: AppButtonVariant.secondary,
          onPressed: () {},
        ),
      ),
    ));
    expect(find.byType(OutlinedButton), findsOneWidget);
  });

  testWidgets('BrandWordmark shows the Insolvia wordmark', (tester) async {
    await tester.pumpWidget(_host(const Scaffold(body: BrandWordmark())));
    // RichText composes "Insolvia" + "."; assert the composed text.
    final richText = tester.widget<RichText>(find.byType(RichText).first);
    expect(richText.text.toPlainText(), 'Insolvia.');
  });

  testWidgets('AppScaffold frames content with the wordmark header',
      (tester) async {
    await tester.pumpWidget(_host(
      const AppScaffold(child: Text('Hello')),
    ));
    expect(find.byType(BrandWordmark), findsOneWidget);
    expect(find.text('Hello'), findsOneWidget);
  });
}
