// DO NOT EDIT — generated from packages/insolvia_tokens/tokens.json
// Regenerate with: dart run packages/insolvia_tokens/tool/generate_tokens.dart

import 'package:flutter/material.dart';

/// Raw brand color values — the primitive layer of the palette.
///
/// UI code should not read these directly; go through
/// [InsolviaSemanticColors] or the theme so that light/dark and
/// future re-branding stay a single-file change.
abstract final class InsolviaPalette {
  const InsolviaPalette._();

  /// Primary — deep navy "ink".
  static const Color ink = Color(0xFF0B2A4A);

  /// Deep navy, one step lighter — hover/pressed on ink surfaces.
  static const Color inkBright = Color(0xFF13396A);

  /// Deep navy, near-black — the dark-mode canvas.
  static const Color inkDeep = Color(0xFF071B31);

  /// Accent — warm brass/gold.
  static const Color brass = Color(0xFFB8863B);

  /// Warm brass, lifted for legibility on dark surfaces.
  static const Color brassBright = Color(0xFFD2A857);

  /// Neutral — warm off-white "paper"; the light-mode canvas.
  static const Color paper = Color(0xFFFAF9F6);

  /// Neutral — one step down from paper, for inset/alt surfaces.
  static const Color linen = Color(0xFFEFEDE6);

  /// Neutral — near-black body text on light surfaces.
  static const Color graphite = Color(0xFF141A1F);

  /// Neutral — de-emphasized text (captions, metadata).
  static const Color slate = Color(0xFF5A6672);

  /// Neutral — hairline rules and dividers.
  static const Color mist = Color(0xFFC9D0D8);

  /// Semantic — success/confirmed state.
  static const Color success = Color(0xFF2E7D5B);

  /// Semantic — warning/attention state.
  static const Color warning = Color(0xFFB8863B);

  /// Semantic — error/destructive state.
  static const Color danger = Color(0xFFB3352E);

  /// Pure white.
  static const Color white = Color(0xFFFFFFFF);

  /// Pure black.
  static const Color black = Color(0xFF000000);

  /// White at 20% — hairline rules on dark surfaces.
  static const Color whiteAlpha20 = Color(0x33FFFFFF);
}
