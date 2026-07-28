import 'package:go_router/go_router.dart';

import '../ui/auth/auth_callback_screen.dart';
import '../ui/home/home_screen.dart';
import '../ui/core/not_found_screen.dart';

/// App route names, referenced instead of raw path strings.
abstract final class AppRoute {
  const AppRoute._();
  static const home = 'home';
  static const authCallback = 'authCallback';
}

/// Route paths, kept next to the names so a deep link is declared once.
abstract final class AppRoutePath {
  const AppRoutePath._();
  static const home = '/';

  /// Must stay in step with `infra/modules/auth/main.tf`, whose web client
  /// registers `<origin>/auth/callback` as its only OAuth callback URL. If the
  /// two drift, a returning sign-in lands on a route the app does not know.
  static const authCallback = '/auth/callback';
}

/// Builds the app's [GoRouter].
///
/// A factory rather than a bare global so tests (and, later, deep-link entry
/// points) can start at a specific location without mutating shared state;
/// [appRouter] is the single instance the running app uses.
GoRouter createAppRouter({String initialLocation = AppRoutePath.home}) {
  return GoRouter(
    initialLocation: initialLocation,
    routes: [
      GoRoute(
        path: AppRoutePath.home,
        name: AppRoute.home,
        builder: (context, state) => const HomeScreen(),
      ),
      GoRoute(
        path: AppRoutePath.authCallback,
        name: AppRoute.authCallback,
        builder: (context, state) => const AuthCallbackScreen(),
      ),
    ],
    // Without this an unknown deep link renders go_router's raw exception
    // page — a stack trace on the brand's own domain. Once CloudFront rewrites
    // 403/404 to `/index.html` (issue 4.3) every mistyped URL reaches the
    // router, so the fallback has to be a real screen.
    errorBuilder: (context, state) => NotFoundScreen(location: state.uri.path),
  );
}

/// The [GoRouter] the app runs on.
final appRouter = createAppRouter();
