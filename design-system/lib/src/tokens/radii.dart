import 'package:flutter/widgets.dart';

/// Corner-radius scale for Insolvia surfaces and controls.
abstract final class InsolviaRadii {
  const InsolviaRadii._();

  static const double sm = 6;
  static const double md = 10;
  static const double lg = 16;
  static const double pill = 999;

  static const BorderRadius smAll = BorderRadius.all(Radius.circular(sm));
  static const BorderRadius mdAll = BorderRadius.all(Radius.circular(md));
  static const BorderRadius lgAll = BorderRadius.all(Radius.circular(lg));
}
