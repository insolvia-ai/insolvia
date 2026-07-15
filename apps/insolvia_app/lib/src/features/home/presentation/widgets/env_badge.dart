import 'package:flutter/material.dart';
import 'package:insolvia_design_system/insolvia_design_system.dart';

import '../../../../config/environment.dart';

/// A small pill showing the current environment, tinted by the brand accent.
class EnvBadge extends StatelessWidget {
  const EnvBadge({super.key, required this.env});

  final AppEnvironment env;

  @override
  Widget build(BuildContext context) {
    final colors = context.insolviaColors;
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 6),
      decoration: BoxDecoration(
        color: colors.brandAccent.withValues(alpha: 0.14),
        borderRadius: InsolviaRadii.smAll,
        border: Border.all(color: colors.brandAccent.withValues(alpha: 0.5)),
      ),
      child: Text(
        env.label.toUpperCase(),
        style: Theme.of(context).textTheme.labelLarge?.copyWith(
              color: colors.brandAccent,
              letterSpacing: 1,
            ),
      ),
    );
  }
}
