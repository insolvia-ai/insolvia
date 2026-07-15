import 'package:flutter/material.dart';

import '../theme/theme_extensions.dart';

/// The Insolvia wordmark: "Insolvia" in brand ink with a brass accent dot.
///
/// A placeholder identity until the real logo/brand lands (O4), but themed so
/// it already responds to light/dark and to any brand-color changes.
class BrandWordmark extends StatelessWidget {
  const BrandWordmark({super.key, this.fontSize = 28});

  final double fontSize;

  @override
  Widget build(BuildContext context) {
    final colors = context.insolviaColors;
    return RichText(
      text: TextSpan(
        style: TextStyle(
          fontSize: fontSize,
          fontWeight: FontWeight.w700,
          letterSpacing: -0.5,
          color: colors.brandInk,
        ),
        children: [
          const TextSpan(text: 'Insolvia'),
          TextSpan(
            text: '.',
            style: TextStyle(color: colors.brandAccent),
          ),
        ],
      ),
    );
  }
}
