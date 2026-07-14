import 'package:flutter/material.dart';

/// Raw brand color values — the single source of truth for Insolvia's palette.
///
/// These are *primitives*. UI code should not read them directly; it should go
/// through the theme ([ColorScheme]) or the [InsolviaColors] theme extension so
/// that light/dark and future re-branding stay centralized.
///
/// Placeholder professional-legal palette (brand not yet finalized — see O4):
/// deep navy "ink" for trust, warm brass for accent, paper neutrals.
abstract final class InsolviaPalette {
  const InsolviaPalette._();

  // Primary — deep navy "ink".
  static const Color ink = Color(0xFF0B2A4A);
  static const Color inkBright = Color(0xFF13396A);
  static const Color inkDeep = Color(0xFF071B31);

  // Accent — warm brass/gold.
  static const Color brass = Color(0xFFB8863B);
  static const Color brassBright = Color(0xFFD2A857);

  // Neutrals — paper & graphite.
  static const Color paper = Color(0xFFFAF9F6);
  static const Color linen = Color(0xFFEFEDE6);
  static const Color graphite = Color(0xFF141A1F);
  static const Color slate = Color(0xFF5A6672);
  static const Color mist = Color(0xFFC9D0D8);

  // Semantic.
  static const Color success = Color(0xFF2E7D5B);
  static const Color warning = Color(0xFFB8863B);
  static const Color danger = Color(0xFFB3352E);

  // Pure.
  static const Color white = Color(0xFFFFFFFF);
  static const Color black = Color(0xFF000000);
}
