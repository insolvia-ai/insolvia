import 'package:go_router/go_router.dart';

import '../features/home/presentation/home_screen.dart';

/// App route names, referenced instead of raw path strings.
abstract final class AppRoute {
  const AppRoute._();
  static const home = 'home';
}

/// The app's [GoRouter]. A single route today; new features add their routes
/// here (or contribute a typed sub-router) as the app grows.
final appRouter = GoRouter(
  initialLocation: '/',
  routes: [
    GoRoute(
      path: '/',
      name: AppRoute.home,
      builder: (context, state) => const HomeScreen(),
    ),
  ],
);
