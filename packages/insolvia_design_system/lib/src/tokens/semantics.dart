// DO NOT EDIT — generated from packages/insolvia_tokens/tokens.json
// Regenerate with: dart run packages/insolvia_tokens/tool/generate_tokens.dart

import 'package:flutter/material.dart';

import 'colors.dart';

/// The semantic layer: the only color vocabulary UI code and
/// themes should speak.
///
/// Raw [InsolviaPalette] entries map onto these roles, and a
/// re-brand swaps the mapping rather than every call site. This
/// mirrors the CSS custom properties generated into
/// `insolvia_design_system_react`, so both stacks stay one
/// vocabulary.
@immutable
class InsolviaSemanticColors {
  const InsolviaSemanticColors({
    required this.bg,
    required this.card,
    required this.surfaceAlt,
    required this.ink,
    required this.brand,
    required this.primary,
    required this.primaryText,
    required this.accent,
    required this.muted,
    required this.line,
    required this.success,
    required this.warning,
    required this.danger,
    required this.primaryHover,
    required this.primaryActive,
    required this.accentHover,
    required this.dangerHover,
  });

  /// App background "paper".
  final Color bg;

  /// Raised surface behind cards, sheets, and dialogs.
  final Color card;

  /// Inset/alternate surface — table stripes, wells, code blocks.
  final Color surfaceAlt;

  /// Body text on [bg].
  final Color ink;

  /// The brand ink used by headers and the wordmark.
  final Color brand;

  /// Primary action color (filled CTAs, focus, selection).
  final Color primary;

  /// Text/icon color that sits on top of [primary].
  final Color primaryText;

  /// Warm brass accent (highlights, focus, key CTAs).
  final Color accent;

  /// De-emphasized text (captions, metadata).
  final Color muted;

  /// Thin divider/border color.
  final Color line;

  /// Success/confirmed state.
  final Color success;

  /// Warning/attention state.
  final Color warning;

  /// Error/destructive state.
  final Color danger;

  /// Hovered [primary].
  final Color primaryHover;

  /// Pressed [primary].
  final Color primaryActive;

  /// Hovered [accent].
  final Color accentHover;

  /// Hovered [danger].
  final Color dangerHover;

  /// The light-mode mapping of palette onto semantics.
  static const InsolviaSemanticColors light = InsolviaSemanticColors(
    bg: Color(0xFFFAF9F6),
    card: Color(0xFFFFFFFF),
    surfaceAlt: Color(0xFFEFEDE6),
    ink: Color(0xFF141A1F),
    brand: Color(0xFF0B2A4A),
    primary: Color(0xFF0B2A4A),
    primaryText: Color(0xFFFFFFFF),
    accent: Color(0xFFB8863B),
    muted: Color(0xFF5A6672),
    line: Color(0xFFC9D0D8),
    success: Color(0xFF2E7D5B),
    warning: Color(0xFFB8863B),
    danger: Color(0xFFB3352E),
    primaryHover: Color(0xFF0A2541),
    primaryActive: Color(0xFF09223B),
    accentHover: Color(0xFFA27634),
    dangerHover: Color(0xFF9E2F28),
  );

  /// The dark-mode mapping of palette onto semantics.
  static const InsolviaSemanticColors dark = InsolviaSemanticColors(
    bg: Color(0xFF071B31),
    card: Color(0xFF141A1F),
    surfaceAlt: Color(0xFF0B2A4A),
    ink: Color(0xFFFAF9F6),
    brand: Color(0xFFFFFFFF),
    primary: Color(0xFFD2A857),
    primaryText: Color(0xFF071B31),
    accent: Color(0xFFD2A857),
    muted: Color(0xFFC9D0D8),
    line: Color(0x33FFFFFF),
    success: Color(0xFF2E7D5B),
    warning: Color(0xFFD2A857),
    danger: Color(0xFFB3352E),
    primaryHover: Color(0xFFD7B26B),
    primaryActive: Color(0xFFB08D49),
    accentHover: Color(0xFFD7B26B),
    dangerHover: Color(0xFFBC4D47),
  );
}
