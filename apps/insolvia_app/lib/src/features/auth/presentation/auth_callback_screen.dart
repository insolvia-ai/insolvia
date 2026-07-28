import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';
import 'package:insolvia_design_system/insolvia_design_system.dart';

import '../../../routing/app_router.dart';

/// Placeholder landing screen for the OAuth redirect.
///
/// The Cognito user pool in `infra/modules/auth` registers
/// `<origin>/auth/callback` as the web client's callback URL, but this build
/// carries **no OIDC client** — sign-in is a separate ticket. This screen
/// exists so that a redirect (or a stale bookmark) lands on branded chrome
/// with a way forward instead of the router's exception page. It deliberately
/// does not read, validate, or exchange the `code`/`state` query parameters:
/// half an auth flow is worse than none.
class AuthCallbackScreen extends StatelessWidget {
  const AuthCallbackScreen({super.key});

  @override
  Widget build(BuildContext context) {
    final colors = context.insolviaColors;
    final textTheme = Theme.of(context).textTheme;

    return AppScaffold(
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text('Sign-in is not enabled yet', style: textTheme.displaySmall),
          const SizedBox(height: InsolviaSpacing.sm),
          Text(
            'This build has no sign-in flow — accounts land in a later '
            'release. Nothing was signed in, and nothing was stored.',
            style: textTheme.bodyLarge?.copyWith(color: colors.subtleText),
          ),
          const SizedBox(height: InsolviaSpacing.xl),
          AppButton(
            label: 'Continue to Insolvia',
            icon: Icons.arrow_forward,
            onPressed: () => context.goNamed(AppRoute.home),
          ),
        ],
      ),
    );
  }
}
