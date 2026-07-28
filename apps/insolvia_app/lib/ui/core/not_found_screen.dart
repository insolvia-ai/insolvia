import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';
import 'package:insolvia_design_system/insolvia_design_system.dart';

import '../../routing/app_router.dart';

/// The router's fallback for an unrecognised location — branded chrome and a
/// way back, rather than go_router's default exception page.
class NotFoundScreen extends StatelessWidget {
  const NotFoundScreen({super.key, required this.location});

  /// The path that matched no route, echoed back so a mistyped or stale link
  /// is self-diagnosing.
  final String location;

  @override
  Widget build(BuildContext context) {
    final colors = context.insolviaColors;
    final textTheme = Theme.of(context).textTheme;

    return AppScaffold(
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text('Page not found', style: textTheme.displaySmall),
          const SizedBox(height: InsolviaSpacing.sm),
          Text(
            'Nothing lives at $location.',
            style: textTheme.bodyLarge?.copyWith(color: colors.subtleText),
          ),
          const SizedBox(height: InsolviaSpacing.xl),
          AppButton(
            label: 'Back to home',
            onPressed: () => context.goNamed(AppRoute.home),
          ),
        ],
      ),
    );
  }
}
