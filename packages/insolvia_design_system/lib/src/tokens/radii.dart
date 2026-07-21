// DO NOT EDIT — generated from packages/insolvia_tokens/tokens.json
// Regenerate with: dart run packages/insolvia_tokens/tool/generate_tokens.dart

import 'package:flutter/widgets.dart';

/// Corner-radius scale for Insolvia surfaces and controls.
abstract final class InsolviaRadii {
  const InsolviaRadii._();

  /// 6 — chips, inputs, small controls.
  static const double sm = 6;

  /// 10 — buttons and the default control radius.
  static const double md = 10;

  /// 16 — cards and raised surfaces.
  static const double lg = 16;

  /// 999 — fully rounded (pills, avatars).
  static const double pill = 999;

  /// [sm] as a [BorderRadius] on all four corners.
  static const BorderRadius smAll = BorderRadius.all(Radius.circular(sm));

  /// [md] as a [BorderRadius] on all four corners.
  static const BorderRadius mdAll = BorderRadius.all(Radius.circular(md));

  /// [lg] as a [BorderRadius] on all four corners.
  static const BorderRadius lgAll = BorderRadius.all(Radius.circular(lg));
}
