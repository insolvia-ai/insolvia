import 'package:flutter/material.dart';

import '../tokens/spacing.dart';

/// Brand color tokens that don't map cleanly onto Material's [ColorScheme].
///
/// Read via `Theme.of(context).extension<InsolviaColors>()!`.
@immutable
class InsolviaColors extends ThemeExtension<InsolviaColors> {
  const InsolviaColors({
    required this.canvas,
    required this.brandInk,
    required this.brandAccent,
    required this.subtleText,
    required this.hairline,
    required this.success,
    required this.warning,
    required this.danger,
  });

  /// App background "paper".
  final Color canvas;

  /// The deep navy brand ink (headers, wordmark).
  final Color brandInk;

  /// Warm brass accent (highlights, focus, key CTAs).
  final Color brandAccent;

  /// De-emphasized text (captions, metadata).
  final Color subtleText;

  /// Thin divider/border color.
  final Color hairline;

  final Color success;
  final Color warning;
  final Color danger;

  @override
  InsolviaColors copyWith({
    Color? canvas,
    Color? brandInk,
    Color? brandAccent,
    Color? subtleText,
    Color? hairline,
    Color? success,
    Color? warning,
    Color? danger,
  }) {
    return InsolviaColors(
      canvas: canvas ?? this.canvas,
      brandInk: brandInk ?? this.brandInk,
      brandAccent: brandAccent ?? this.brandAccent,
      subtleText: subtleText ?? this.subtleText,
      hairline: hairline ?? this.hairline,
      success: success ?? this.success,
      warning: warning ?? this.warning,
      danger: danger ?? this.danger,
    );
  }

  @override
  InsolviaColors lerp(ThemeExtension<InsolviaColors>? other, double t) {
    if (other is! InsolviaColors) return this;
    return InsolviaColors(
      canvas: Color.lerp(canvas, other.canvas, t)!,
      brandInk: Color.lerp(brandInk, other.brandInk, t)!,
      brandAccent: Color.lerp(brandAccent, other.brandAccent, t)!,
      subtleText: Color.lerp(subtleText, other.subtleText, t)!,
      hairline: Color.lerp(hairline, other.hairline, t)!,
      success: Color.lerp(success, other.success, t)!,
      warning: Color.lerp(warning, other.warning, t)!,
      danger: Color.lerp(danger, other.danger, t)!,
    );
  }
}

/// Spacing scale exposed through the theme, so widgets can pull rhythm from
/// context (`Theme.of(context).extension<InsolviaSpacings>()!`) as well as from
/// the [InsolviaSpacing] constants.
@immutable
class InsolviaSpacings extends ThemeExtension<InsolviaSpacings> {
  const InsolviaSpacings({
    this.xs = InsolviaSpacing.xs,
    this.sm = InsolviaSpacing.sm,
    this.md = InsolviaSpacing.md,
    this.lg = InsolviaSpacing.lg,
    this.xl = InsolviaSpacing.xl,
    this.xxl = InsolviaSpacing.xxl,
  });

  final double xs;
  final double sm;
  final double md;
  final double lg;
  final double xl;
  final double xxl;

  @override
  InsolviaSpacings copyWith({
    double? xs,
    double? sm,
    double? md,
    double? lg,
    double? xl,
    double? xxl,
  }) {
    return InsolviaSpacings(
      xs: xs ?? this.xs,
      sm: sm ?? this.sm,
      md: md ?? this.md,
      lg: lg ?? this.lg,
      xl: xl ?? this.xl,
      xxl: xxl ?? this.xxl,
    );
  }

  @override
  InsolviaSpacings lerp(ThemeExtension<InsolviaSpacings>? other, double t) {
    if (other is! InsolviaSpacings) return this;
    return InsolviaSpacings(
      xs: lerpDouble(xs, other.xs, t),
      sm: lerpDouble(sm, other.sm, t),
      md: lerpDouble(md, other.md, t),
      lg: lerpDouble(lg, other.lg, t),
      xl: lerpDouble(xl, other.xl, t),
      xxl: lerpDouble(xxl, other.xxl, t),
    );
  }

  static double lerpDouble(double a, double b, double t) => a + (b - a) * t;
}

/// Convenience accessors so callers can write `context.insolviaColors`.
extension InsolviaThemeContext on BuildContext {
  InsolviaColors get insolviaColors =>
      Theme.of(this).extension<InsolviaColors>()!;
  InsolviaSpacings get insolviaSpacing =>
      Theme.of(this).extension<InsolviaSpacings>()!;
}
