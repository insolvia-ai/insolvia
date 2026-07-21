import 'package:flutter/material.dart';

import '../tokens/radii.dart';
import '../tokens/semantics.dart';
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
///
/// Both themes are assembled purely from [InsolviaSemanticColors] — the same
/// vocabulary the generated Tailwind `theme.css` exposes to the web stack. No
/// raw palette entry is referenced here, so a re-brand is a change to
/// `packages/insolvia_tokens/tokens.json` alone.
abstract final class InsolviaTheme {
  const InsolviaTheme._();

  static ThemeData light() =>
      _build(InsolviaSemanticColors.light, Brightness.light);

  static ThemeData dark() =>
      _build(InsolviaSemanticColors.dark, Brightness.dark);

  static ThemeData _build(
    InsolviaSemanticColors colors,
    Brightness brightness,
  ) {
    final scheme = ColorScheme.fromSeed(
      seedColor: colors.brand,
      brightness: brightness,
    ).copyWith(
      primary: colors.primary,
      onPrimary: colors.primaryText,
      secondary: colors.accent,
      surface: colors.card,
      error: colors.danger,
    );

    final base = ThemeData(useMaterial3: true, colorScheme: scheme);
    return base.copyWith(
      scaffoldBackgroundColor: colors.bg,
      textTheme: InsolviaTypography.textTheme(base.textTheme, colors.ink),
      extensions: <ThemeExtension<dynamic>>[
        InsolviaColors(
          canvas: colors.bg,
          brandInk: colors.brand,
          brandAccent: colors.accent,
          subtleText: colors.muted,
          hairline: colors.line,
          success: colors.success,
          warning: colors.warning,
          danger: colors.danger,
        ),
        const InsolviaSpacings(),
      ],
      filledButtonTheme: FilledButtonThemeData(
        style: FilledButton.styleFrom(
          shape: const RoundedRectangleBorder(
            borderRadius: InsolviaRadii.mdAll,
          ),
          padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 14),
        ),
      ),
      outlinedButtonTheme: OutlinedButtonThemeData(
        style: OutlinedButton.styleFrom(
          shape: const RoundedRectangleBorder(
            borderRadius: InsolviaRadii.mdAll,
          ),
          padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 14),
        ),
      ),
      cardTheme: const CardThemeData(
        shape: RoundedRectangleBorder(borderRadius: InsolviaRadii.lgAll),
      ),
    );
  }
}
