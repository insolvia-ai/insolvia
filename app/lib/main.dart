import 'package:flutter/material.dart';
import 'package:insolvia_design_system/insolvia_design_system.dart';

import 'config/environment.dart';

void main() {
  runApp(const InsolviaApp());
}

class InsolviaApp extends StatelessWidget {
  const InsolviaApp({super.key});

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      title: 'Insolvia',
      debugShowCheckedModeBanner: false,
      theme: InsolviaTheme.light(),
      darkTheme: InsolviaTheme.dark(),
      home: const HomeScreen(),
    );
  }
}

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
      actions: [_EnvBadge(env: env)],
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

/// A small pill showing the current environment, tinted by the brand accent.
class _EnvBadge extends StatelessWidget {
  const _EnvBadge({required this.env});

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
