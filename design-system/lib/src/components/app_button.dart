import 'package:flutter/material.dart';

/// Visual emphasis for an [AppButton].
enum AppButtonVariant {
  /// Solid, high-emphasis primary action.
  primary,

  /// Outlined, medium-emphasis secondary action.
  secondary,
}

/// The Insolvia button. A thin, themed wrapper over Material's filled/outlined
/// buttons so product code never wires button styling by hand — it comes from
/// [InsolviaTheme].
class AppButton extends StatelessWidget {
  const AppButton({
    super.key,
    required this.label,
    required this.onPressed,
    this.variant = AppButtonVariant.primary,
    this.icon,
  });

  final String label;
  final VoidCallback? onPressed;
  final AppButtonVariant variant;
  final IconData? icon;

  @override
  Widget build(BuildContext context) {
    final child = icon == null
        ? Text(label)
        : Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              Icon(icon, size: 18),
              const SizedBox(width: 8),
              Text(label),
            ],
          );

    return switch (variant) {
      AppButtonVariant.primary =>
        FilledButton(onPressed: onPressed, child: child),
      AppButtonVariant.secondary =>
        OutlinedButton(onPressed: onPressed, child: child),
    };
  }
}
