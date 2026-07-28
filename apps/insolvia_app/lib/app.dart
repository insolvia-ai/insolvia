import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';
import 'package:insolvia_design_system/insolvia_design_system.dart';

import 'routing/app_router.dart';

/// Root widget: wires the Insolvia themes and the app router.
///
/// Both themes are supplied and [ThemeMode.system] is left as the default, so
/// the shell follows the OS appearance on desktop and the browser's
/// `prefers-color-scheme` on web.
class InsolviaApp extends StatelessWidget {
  const InsolviaApp({super.key, this.router});

  /// The router to run on. Defaults to the app-wide [appRouter]; tests (and
  /// any future deep-link entry point) can inject one that starts elsewhere.
  final GoRouter? router;

  @override
  Widget build(BuildContext context) {
    return MaterialApp.router(
      title: 'Insolvia',
      debugShowCheckedModeBanner: false,
      theme: InsolviaTheme.light(),
      darkTheme: InsolviaTheme.dark(),
      routerConfig: router ?? appRouter,
    );
  }
}
