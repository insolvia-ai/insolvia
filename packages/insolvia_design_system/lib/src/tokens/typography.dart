import 'package:flutter/material.dart';

/// Insolvia's type scale.
///
/// Until the brand fonts land (O4), we use the platform default family with a
/// deliberate scale/weight ramp. When the variable fonts are bundled (see
/// `pubspec.yaml`), set [displayFamily]/[bodyFamily] and, if desired, use
/// [FontVariation] weight axes in place of [FontWeight].
abstract final class InsolviaTypography {
  const InsolviaTypography._();

  /// Serif display family — `null` falls back to the platform default for now.
  static const String? displayFamily = null;

  /// Sans body family — `null` falls back to the platform default for now.
  static const String? bodyFamily = null;

  /// Builds a [TextTheme] in the given [color], layered onto a base theme's
  /// text theme so platform defaults (locale fonts, etc.) are preserved.
  static TextTheme textTheme(TextTheme base, Color color) {
    return base
        .copyWith(
          displaySmall: base.displaySmall?.copyWith(
            fontFamily: displayFamily,
            fontWeight: FontWeight.w700,
            height: 1.1,
          ),
          headlineMedium: base.headlineMedium?.copyWith(
            fontFamily: displayFamily,
            fontWeight: FontWeight.w600,
            height: 1.15,
          ),
          titleLarge: base.titleLarge?.copyWith(
            fontFamily: displayFamily,
            fontWeight: FontWeight.w600,
          ),
          bodyLarge: base.bodyLarge?.copyWith(
            fontFamily: bodyFamily,
            height: 1.45,
          ),
          bodyMedium: base.bodyMedium?.copyWith(
            fontFamily: bodyFamily,
            height: 1.45,
          ),
          labelLarge: base.labelLarge?.copyWith(
            fontFamily: bodyFamily,
            fontWeight: FontWeight.w600,
            letterSpacing: 0.2,
          ),
        )
        .apply(bodyColor: color, displayColor: color);
  }
}
