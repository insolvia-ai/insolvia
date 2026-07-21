/// Insolvia's shared design system.
///
/// Import this single barrel to get tokens, themes, and components:
/// ```dart
/// import 'package:insolvia_design_system/insolvia_design_system.dart';
/// ```
library;

// Tokens
// (colors/spacing/radii/semantics are generated from
// packages/insolvia_tokens/tokens.json — see that package's README.)
export 'src/tokens/colors.dart';
export 'src/tokens/semantics.dart';
export 'src/tokens/spacing.dart';
export 'src/tokens/radii.dart';
export 'src/tokens/typography.dart';

// Theme
export 'src/theme/app_theme.dart';
export 'src/theme/theme_extensions.dart';

// Components
export 'src/components/app_button.dart';
export 'src/components/app_scaffold.dart';
export 'src/components/brand_wordmark.dart';
