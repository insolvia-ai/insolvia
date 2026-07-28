import 'package:flutter/material.dart';
import 'package:insolvia_design_system/insolvia_design_system.dart';

import '../../../config/environment.dart';
import 'widgets/env_badge.dart';

/// The signed-in shell's home screen.
///
/// Deliberately thin: this milestone proves the delivery pipeline, not the
/// product (see `docs/MVP_PLAN.md`, Milestone 4). Everything visual comes from
/// the design system — the [AppScaffold] frame (which carries the
/// [BrandWordmark] and the centered max-width column), [AppButton], the
/// spacing scale, and the [InsolviaColors] extension — so no color, spacing,
/// or font is spelled out here. It also surfaces the active [AppEnvironment],
/// which is what makes a staging build unmistakable at a glance.
class HomeScreen extends StatelessWidget {
  const HomeScreen({super.key});

  @override
  Widget build(BuildContext context) {
    final env = AppEnvironment.resolve();
    final colors = context.insolviaColors;
    final textTheme = Theme.of(context).textTheme;

    return AppScaffold(
      actions: [EnvBadge(env: env)],
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text('Your case workspace', style: textTheme.displaySmall),
          const SizedBox(height: InsolviaSpacing.sm),
          Text(
            'This is the shell every Insolvia screen sits inside. Case intake, '
            'the forms engine, and e-filing each arrive in their own ticket.',
            style: textTheme.bodyLarge?.copyWith(color: colors.subtleText),
          ),
          const SizedBox(height: InsolviaSpacing.xl),
          Wrap(
            spacing: InsolviaSpacing.md,
            runSpacing: InsolviaSpacing.md,
            children: [
              AppButton(
                label: 'Start a case',
                icon: Icons.arrow_forward,
                onPressed: () => _showSoon(context),
              ),
              AppButton(
                label: 'Open a case',
                variant: AppButtonVariant.secondary,
                onPressed: () => _showSoon(context),
              ),
            ],
          ),
          const SizedBox(height: InsolviaSpacing.xl),
          Text(
            'Serving ${env.label.toLowerCase()} · ${env.host}',
            style: textTheme.bodySmall?.copyWith(color: colors.subtleText),
          ),
        ],
      ),
    );
  }

  void _showSoon(BuildContext context) {
    ScaffoldMessenger.of(context).showSnackBar(
      const SnackBar(content: Text('Case tools arrive in a later release.')),
    );
  }
}
