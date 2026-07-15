import 'package:flutter/material.dart';

import '../tokens/colors.dart';
import '../tokens/radii.dart';
import '../tokens/typography.dart';
import 'theme_extensions.dart';

/// Insolvia's Material themes.
///
/// ```dart
/// MaterialApp(
///   theme: InsolviaTheme.light(),
///   darkTheme: InsolviaTheme.dark(),
/// );
/// ```
abstract final class InsolviaTheme {
  const InsolviaTheme._();

  static ThemeData light() {
    final scheme = ColorScheme.fromSeed(
      seedColor: InsolviaPalette.ink,
      brightness: Brightness.light,
    ).copyWith(
      primary: InsolviaPalette.ink,
      secondary: InsolviaPalette.brass,
      surface: InsolviaPalette.white,
      error: InsolviaPalette.danger,
    );

    const extensions = <ThemeExtension<dynamic>>[
      InsolviaColors(
        canvas: InsolviaPalette.paper,
        brandInk: InsolviaPalette.ink,
        brandAccent: InsolviaPalette.brass,
        subtleText: InsolviaPalette.slate,
        hairline: InsolviaPalette.mist,
        success: InsolviaPalette.success,
        warning: InsolviaPalette.warning,
        danger: InsolviaPalette.danger,
      ),
      InsolviaSpacings(),
    ];

    return _base(
        scheme, InsolviaPalette.paper, InsolviaPalette.graphite, extensions);
  }

  static ThemeData dark() {
    final scheme = ColorScheme.fromSeed(
      seedColor: InsolviaPalette.ink,
      brightness: Brightness.dark,
    ).copyWith(
      primary: InsolviaPalette.brassBright,
      secondary: InsolviaPalette.brass,
      error: InsolviaPalette.danger,
    );

    const extensions = <ThemeExtension<dynamic>>[
      InsolviaColors(
        canvas: InsolviaPalette.inkDeep,
        brandInk: InsolviaPalette.white,
        brandAccent: InsolviaPalette.brassBright,
        subtleText: InsolviaPalette.mist,
        hairline: Color(0x33FFFFFF),
        success: InsolviaPalette.success,
        warning: InsolviaPalette.brassBright,
        danger: InsolviaPalette.danger,
      ),
      InsolviaSpacings(),
    ];

    return _base(
        scheme, InsolviaPalette.inkDeep, InsolviaPalette.paper, extensions);
  }

  static ThemeData _base(
    ColorScheme scheme,
    Color scaffoldBackground,
    Color onBackground,
    List<ThemeExtension<dynamic>> extensions,
  ) {
    final base = ThemeData(useMaterial3: true, colorScheme: scheme);
    return base.copyWith(
      scaffoldBackgroundColor: scaffoldBackground,
      textTheme: InsolviaTypography.textTheme(base.textTheme, onBackground),
      extensions: extensions,
      filledButtonTheme: FilledButtonThemeData(
        style: FilledButton.styleFrom(
          shape:
              const RoundedRectangleBorder(borderRadius: InsolviaRadii.mdAll),
          padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 14),
        ),
      ),
      outlinedButtonTheme: OutlinedButtonThemeData(
        style: OutlinedButton.styleFrom(
          shape:
              const RoundedRectangleBorder(borderRadius: InsolviaRadii.mdAll),
          padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 14),
        ),
      ),
      cardTheme: const CardThemeData(
        shape: RoundedRectangleBorder(borderRadius: InsolviaRadii.lgAll),
      ),
    );
  }
}
