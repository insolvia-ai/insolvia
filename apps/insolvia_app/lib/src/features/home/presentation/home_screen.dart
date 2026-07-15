import 'package:flutter/material.dart';
import 'package:insolvia_design_system/insolvia_design_system.dart';

import '../../../config/environment.dart';
import 'widgets/env_badge.dart';

/// The hello-world home screen. Everything visual comes from the design system:
/// the [AppScaffold] frame, the [BrandWordmark], themed typography, and the
/// [InsolviaColors] extension. It also surfaces the active [AppEnvironment].
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
          Text('Hello, Insolvia', style: textTheme.displaySmall),
          const SizedBox(height: InsolviaSpacing.sm),
          Text(
            'Modern, cross-platform bankruptcy case prep — running on '
            '${env.label.toLowerCase()} (${env.host}).',
            style: textTheme.bodyLarge?.copyWith(color: colors.subtleText),
          ),
          const SizedBox(height: InsolviaSpacing.xl),
          Wrap(
            spacing: InsolviaSpacing.md,
            runSpacing: InsolviaSpacing.md,
            children: [
              AppButton(
                label: 'Get started',
                icon: Icons.arrow_forward,
                onPressed: () => _showSoon(context),
              ),
              AppButton(
                label: 'Learn more',
                variant: AppButtonVariant.secondary,
                onPressed: () => _showSoon(context),
              ),
            ],
          ),
        ],
      ),
    );
  }

  void _showSoon(BuildContext context) {
    ScaffoldMessenger.of(context).showSnackBar(
      const SnackBar(content: Text('Coming soon.')),
    );
  }
}
