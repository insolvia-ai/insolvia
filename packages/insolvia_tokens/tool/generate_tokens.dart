// Renders `packages/insolvia_tokens/tokens.json` into every consuming stack.
//
//   dart run packages/insolvia_tokens/tool/generate_tokens.dart
//   dart run packages/insolvia_tokens/tool/generate_tokens.dart --check
//
// `--check` regenerates in memory and exits non-zero if any committed output
// has drifted from the JSON, so CI can gate hand-edits to generated files.
//
// Deliberately dependency-free (`dart:io` + `dart:convert` only): at this token
// count a ~400-line script is easier to read, debug, and vendor than Style
// Dictionary plus a Node toolchain.

import 'dart:convert';
import 'dart:io';

const String _tokensPath = 'packages/insolvia_tokens/tokens.json';
const String _regenCommand =
    'dart run packages/insolvia_tokens/tool/generate_tokens.dart';

const String _dartBanner = '// DO NOT EDIT — generated from $_tokensPath\n'
    '// Regenerate with: $_regenCommand\n';

const String _cssBanner = '/* DO NOT EDIT — generated from $_tokensPath\n'
    ' * Regenerate with: $_regenCommand\n'
    ' */\n';

const String _dartOutDir = 'packages/insolvia_design_system/lib/src/tokens';
const String _cssOut =
    'packages/insolvia_design_system_react/src/styles/theme.css';

Future<void> main(List<String> args) async {
  final check = args.contains('--check');
  final root = _repoRoot();

  final tokens = jsonDecode(
    File('$root/$_tokensPath').readAsStringSync(),
  ) as Map<String, dynamic>;

  final outputs = <String, String>{
    '$_dartOutDir/colors.dart': _renderColors(tokens),
    '$_dartOutDir/spacing.dart': _renderSpacing(tokens),
    '$_dartOutDir/radii.dart': _renderRadii(tokens),
    '$_dartOutDir/semantics.dart': _renderSemantics(tokens),
    _cssOut: _renderCss(tokens),
  };

  final drifted = <String>[];
  for (final entry in outputs.entries) {
    final file = File('$root/${entry.key}');
    final current = file.existsSync() ? file.readAsStringSync() : null;
    if (current == entry.value) continue;
    if (check) {
      drifted.add(entry.key);
    } else {
      file.parent.createSync(recursive: true);
      file.writeAsStringSync(entry.value);
      stdout.writeln('wrote ${entry.key}');
    }
  }

  if (!check) {
    stdout.writeln('Generated ${outputs.length} file(s) from $_tokensPath.');
    return;
  }

  if (drifted.isEmpty) {
    stdout.writeln('Tokens are in sync (${outputs.length} file(s) checked).');
    return;
  }

  stderr.writeln('Generated token output has drifted from $_tokensPath:');
  for (final path in drifted) {
    stderr.writeln('  - $path');
  }
  stderr.writeln('\nEdit $_tokensPath (never the generated file), then run:');
  stderr.writeln('  $_regenCommand');
  exitCode = 1;
}

/// The script lives at `<root>/packages/insolvia_tokens/tool/`, so the repo
/// root is three directories up — resolved from the script URI rather than the
/// cwd so the generator behaves identically from anywhere.
String _repoRoot() {
  final toolDir = Directory.fromUri(Platform.script.resolve('.'));
  return toolDir.parent.parent.parent.path;
}

// ─────────────────────────── token model ───────────────────────────

/// Entries whose key starts with `$` are documentation for humans reading the
/// JSON, not tokens.
Map<String, Map<String, dynamic>> _group(
    Map<String, dynamic> tokens, String name) {
  final raw = tokens[name] as Map<String, dynamic>;
  return {
    for (final e in raw.entries)
      if (!e.key.startsWith(r'$')) e.key: e.value as Map<String, dynamic>,
  };
}

/// Resolves a `{palette.foo}` alias to its raw hex value.
String _resolve(Map<String, dynamic> tokens, String ref) {
  if (!ref.startsWith('{') || !ref.endsWith('}')) return ref;
  final parts = ref.substring(1, ref.length - 1).split('.');
  var node = tokens as dynamic;
  for (final part in parts) {
    node = (node as Map<String, dynamic>)[part];
    if (node == null) {
      throw StateError('Unresolvable token alias: $ref');
    }
  }
  return (node as Map<String, dynamic>)['value'] as String;
}

/// `#RRGGBB` or `#RRGGBBAA` → `[r, g, b, a]`, each 0-255.
List<int> _parseHex(String hex) {
  final body = hex.replaceFirst('#', '');
  if (body.length != 6 && body.length != 8) {
    throw StateError('Expected #RRGGBB or #RRGGBBAA, got "$hex"');
  }
  return [
    int.parse(body.substring(0, 2), radix: 16),
    int.parse(body.substring(2, 4), radix: 16),
    int.parse(body.substring(4, 6), radix: 16),
    body.length == 8 ? int.parse(body.substring(6, 8), radix: 16) : 255,
  ];
}

/// The sRGB blend CSS `color-mix(in srgb, <base> <100-amount>%, <mix> <amount>%)`
/// produces, pre-computed so the Dart side matches the browser exactly.
String _mix(String baseHex, String mixName, int amount) {
  final base = _parseHex(baseHex);
  final other = mixName == 'white' ? [255, 255, 255, 255] : [0, 0, 0, 255];
  final t = amount / 100.0;
  final out = [
    for (var i = 0; i < 4; i++) (base[i] * (1 - t) + other[i] * t).round(),
  ];
  final hex = out.map((c) => c.toRadixString(16).padLeft(2, '0')).join();
  return '#${hex.substring(0, 6).toUpperCase()}'
      '${out[3] == 255 ? '' : hex.substring(6).toUpperCase()}';
}

// ─────────────────────────── Dart output ───────────────────────────

String _dartColor(String hex) {
  final c = _parseHex(hex);
  final argb = (c[3] << 24) | (c[0] << 16) | (c[1] << 8) | c[2];
  return 'Color(0x${argb.toRadixString(16).toUpperCase().padLeft(8, '0')})';
}

String _doc(String description, {String indent = '  '}) =>
    '$indent/// $description\n';

String _renderColors(Map<String, dynamic> tokens) {
  final palette = _group(tokens, 'palette');
  final buffer = StringBuffer()
    ..write(_dartBanner)
    ..writeln()
    ..writeln("import 'package:flutter/material.dart';")
    ..writeln()
    ..writeln(
        '/// Raw brand color values — the primitive layer of the palette.')
    ..writeln('///')
    ..writeln('/// UI code should not read these directly; go through')
    ..writeln(
        '/// [InsolviaSemanticColors] or the theme so that light/dark and')
    ..writeln('/// future re-branding stay a single-file change.')
    ..writeln('abstract final class InsolviaPalette {')
    ..writeln('  const InsolviaPalette._();');

  for (final entry in palette.entries) {
    buffer
      ..writeln()
      ..write(_doc(entry.value['description'] as String))
      ..writeln('  static const Color ${entry.key} = '
          '${_dartColor(entry.value['value'] as String)};');
  }

  return (buffer..writeln('}')).toString();
}

String _renderSpacing(Map<String, dynamic> tokens) {
  final spacing = _group(tokens, 'spacing');
  final buffer = StringBuffer()
    ..write(_dartBanner)
    ..writeln()
    ..writeln('/// Spacing scale — a 4pt base grid. Use these instead of magic')
    ..writeln('/// numbers so layout rhythm stays consistent across every')
    ..writeln('/// Insolvia surface.')
    ..writeln('abstract final class InsolviaSpacing {')
    ..writeln('  const InsolviaSpacing._();');

  for (final entry in spacing.entries) {
    buffer
      ..writeln()
      ..write(_doc(entry.value['description'] as String))
      ..writeln('  static const double ${entry.key} = '
          '${entry.value['value']};');
  }

  return (buffer..writeln('}')).toString();
}

String _renderRadii(Map<String, dynamic> tokens) {
  final radii = _group(tokens, 'radii');
  final buffer = StringBuffer()
    ..write(_dartBanner)
    ..writeln()
    ..writeln("import 'package:flutter/widgets.dart';")
    ..writeln()
    ..writeln('/// Corner-radius scale for Insolvia surfaces and controls.')
    ..writeln('abstract final class InsolviaRadii {')
    ..writeln('  const InsolviaRadii._();');

  for (final entry in radii.entries) {
    buffer
      ..writeln()
      ..write(_doc(entry.value['description'] as String))
      ..writeln('  static const double ${entry.key} = '
          '${entry.value['value']};');
  }

  for (final entry in radii.entries) {
    if (entry.value['borderRadius'] != true) continue;
    buffer
      ..writeln()
      ..write(_doc('[${entry.key}] as a [BorderRadius] on all four corners.'))
      ..writeln('  static const BorderRadius ${entry.key}All = '
          'BorderRadius.all(Radius.circular(${entry.key}));');
  }

  return (buffer..writeln('}')).toString();
}

String _renderSemantics(Map<String, dynamic> tokens) {
  final semantic = _group(tokens, 'semantic');
  final derived = _group(tokens, 'semanticDerived');

  /// Resolves every semantic + derived token for one brightness.
  Map<String, String> valuesFor(String mode) {
    final resolved = <String, String>{
      for (final e in semantic.entries)
        e.key: _resolve(tokens, e.value[mode] as String),
    };
    for (final e in derived.entries) {
      final spec = e.value[mode] as Map<String, dynamic>;
      resolved[e.key] = _mix(
        resolved[e.value['from'] as String]!,
        spec['mix'] as String,
        spec['amount'] as int,
      );
    }
    return resolved;
  }

  final names = [...semantic.keys, ...derived.keys];
  final descriptions = {
    for (final e in semantic.entries) e.key: e.value['description'] as String,
    for (final e in derived.entries) e.key: e.value['description'] as String,
  };

  final buffer = StringBuffer()
    ..write(_dartBanner)
    ..writeln()
    ..writeln("import 'package:flutter/material.dart';")
    ..writeln()
    ..writeln("import 'colors.dart';")
    ..writeln()
    ..writeln('/// The semantic layer: the only color vocabulary UI code and')
    ..writeln('/// themes should speak.')
    ..writeln('///')
    ..writeln('/// Raw [InsolviaPalette] entries map onto these roles, and a')
    ..writeln(
        '/// re-brand swaps the mapping rather than every call site. This')
    ..writeln('/// mirrors the CSS custom properties generated into')
    ..writeln('/// `insolvia_design_system_react`, so both stacks stay one')
    ..writeln('/// vocabulary.')
    ..writeln('@immutable')
    ..writeln('class InsolviaSemanticColors {')
    ..writeln('  const InsolviaSemanticColors({');
  for (final name in names) {
    buffer.writeln('    required this.$name,');
  }
  buffer.writeln('  });');

  for (final name in names) {
    buffer
      ..writeln()
      ..write(_doc(descriptions[name]!))
      ..writeln('  final Color $name;');
  }

  for (final mode in ['light', 'dark']) {
    final values = valuesFor(mode);
    buffer
      ..writeln()
      ..write(_doc('The $mode-mode mapping of palette onto semantics.'))
      ..writeln('  static const InsolviaSemanticColors $mode = '
          'InsolviaSemanticColors(');
    for (final name in names) {
      buffer.writeln('    $name: ${_dartColor(values[name]!)},');
    }
    buffer.writeln('  );');
  }

  return (buffer..writeln('}')).toString();
}

// ─────────────────────────── CSS output ───────────────────────────

String _kebab(String name) => name
    .replaceAllMapped(RegExp('[A-Z]'), (m) => '-${m[0]!.toLowerCase()}')
    .replaceAll(RegExp('-+'), '-');

/// Opaque colors stay hex; translucent ones become `rgb(r g b / a)`, which is
/// how the reference design system authors alpha.
String _cssColor(String hex) {
  final c = _parseHex(hex);
  final rgb = hex.replaceFirst('#', '').substring(0, 6).toLowerCase();
  if (c[3] == 255) {
    return '#$rgb';
  }
  return 'rgb(${c[0]} ${c[1]} ${c[2]} / ${_trim(c[3] / 255)})';
}

String _renderCss(Map<String, dynamic> tokens) {
  final semantic = _group(tokens, 'semantic');
  final derived = _group(tokens, 'semanticDerived');
  final buffer = StringBuffer()
    ..write(_cssBanner)
    ..writeln('''
/*
 * Tailwind v4 design tokens for Insolvia's React/web surfaces.
 *
 * Consuming apps import this from their Tailwind v4 CSS entrypoint:
 *
 *   @import "tailwindcss";
 *   @import "@insolvia/design-system/theme.css";
 *
 * Dark mode is toggled by setting `data-theme="dark"` on a root element
 * (e.g. <html data-theme="dark">) — every component reads the SEMANTIC
 * tokens below, so nothing in component source changes per theme.
 *
 * Brand yourself by overriding the semantic variables after importing this
 * file. Never depend on raw palette names (ink/brass/paper): they are an
 * implementation detail of the default mapping and are not emitted here.
 */''')
    ..writeln()
    ..writeln('@theme {');

  void section(String comment) => buffer
    ..writeln()
    ..writeln('  /* $comment */');

  section('Typography. Apps can override these to apply their own brand.');
  for (final e in _group(tokens, 'fonts').entries) {
    buffer.writeln('  --font-${_kebab(e.key)}: ${e.value['value']};');
  }

  section('Semantic colors — the light-mode default mapping.');
  for (final e in semantic.entries) {
    buffer.writeln('  --color-${_kebab(e.key)}: '
        '${_cssColor(_resolve(tokens, e.value['light'] as String))};');
  }

  section('Derived states. Overriding the base semantic moves these too.');
  for (final e in derived.entries) {
    final spec = e.value['light'] as Map<String, dynamic>;
    buffer.writeln('  --color-${_kebab(e.key)}: '
        '${_colorMix(e.value['from'] as String, spec)};');
  }

  section('Spacing — a 4pt base grid.');
  for (final e in _group(tokens, 'spacing').entries) {
    final rem = (e.value['value'] as num) / 16;
    buffer.writeln('  --spacing-${_kebab(e.key)}: ${_trim(rem)}rem;');
  }

  section('Corner radii.');
  for (final e in _group(tokens, 'radii').entries) {
    buffer.writeln('  --radius-${_kebab(e.key)}: ${e.value['css']};');
  }

  section('Elevation.');
  for (final e in _group(tokens, 'shadows').entries) {
    buffer.writeln('  --shadow-${_kebab(e.key)}: ${e.value['value']};');
  }

  buffer
    ..writeln('}')
    ..writeln()
    ..writeln("[data-theme='dark'] {");

  for (final e in semantic.entries) {
    buffer.writeln('  --color-${_kebab(e.key)}: '
        '${_cssColor(_resolve(tokens, e.value['dark'] as String))};');
  }
  buffer.writeln();
  for (final e in derived.entries) {
    final spec = e.value['dark'] as Map<String, dynamic>;
    buffer.writeln('  --color-${_kebab(e.key)}: '
        '${_colorMix(e.value['from'] as String, spec)};');
  }

  return (buffer..writeln('}')).toString();
}

String _colorMix(String from, Map<String, dynamic> spec) {
  final amount = spec['amount'] as int;
  return 'color-mix(in srgb, var(--color-${_kebab(from)}) ${100 - amount}%, '
      '${spec['mix']} $amount%)';
}

String _trim(num value) {
  final text = value.toStringAsFixed(4);
  return text.contains('.')
      ? text.replaceFirst(RegExp(r'0+$'), '').replaceFirst(RegExp(r'\.$'), '')
      : text;
}
