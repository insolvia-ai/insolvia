import 'package:flutter/material.dart';

import '../tokens/spacing.dart';
import '../theme/theme_extensions.dart';
import 'brand_wordmark.dart';

/// A branded page scaffold: the Insolvia wordmark header over a centered,
/// max-width content column on the brand canvas. Gives every Insolvia screen a
/// consistent frame without each screen re-implementing chrome.
class AppScaffold extends StatelessWidget {
  const AppScaffold({
    super.key,
    required this.child,
    this.maxContentWidth = 720,
    this.actions,
  });

  final Widget child;
  final double maxContentWidth;

  /// Optional trailing header widgets (e.g. an environment badge).
  final List<Widget>? actions;

  @override
  Widget build(BuildContext context) {
    final colors = context.insolviaColors;
    return Scaffold(
      backgroundColor: colors.canvas,
      body: SafeArea(
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Padding(
              padding: const EdgeInsets.all(InsolviaSpacing.lg),
              child: Row(
                children: [
                  const BrandWordmark(fontSize: 22),
                  const Spacer(),
                  ...?actions,
                ],
              ),
            ),
            Divider(height: 1, color: colors.hairline),
            Expanded(
              child: Center(
                child: ConstrainedBox(
                  constraints: BoxConstraints(maxWidth: maxContentWidth),
                  child: Padding(
                    padding: const EdgeInsets.all(InsolviaSpacing.xl),
                    child: child,
                  ),
                ),
              ),
            ),
          ],
        ),
      ),
    );
  }
}
