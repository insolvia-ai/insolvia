import 'package:flutter/material.dart';
import 'package:insolvia_design_system/insolvia_design_system.dart';

import 'routing/app_router.dart';

/// Root widget: wires the Insolvia themes and the app router.
class InsolviaApp extends StatelessWidget {
  const InsolviaApp({super.key});

  @override
  Widget build(BuildContext context) {
    return MaterialApp.router(
      title: 'Insolvia',
      debugShowCheckedModeBanner: false,
      theme: InsolviaTheme.light(),
      darkTheme: InsolviaTheme.dark(),
      routerConfig: appRouter,
    );
  }
}
