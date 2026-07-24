from __future__ import annotations

import ast
from pathlib import Path

PACKAGE = Path(__file__).parents[1] / "src" / "insolvia_mailer"


def _imports(path: Path) -> set[str]:
    tree = ast.parse(path.read_text(), filename=str(path))
    result: set[str] = set()
    for node in ast.walk(tree):
        if isinstance(node, ast.Import):
            result.update(alias.name for alias in node.names)
        elif isinstance(node, ast.ImportFrom) and node.module:
            result.add(node.module)
    return result


def test_environment_dependency_boundaries() -> None:
    violations: list[str] = []
    forbidden = {
        "core": (
            "insolvia_mailer.api",
            "insolvia_mailer.adapters",
            "insolvia_mailer.entrypoints",
            "boto3",
            "flask",
        ),
        "api": ("insolvia_mailer.adapters", "insolvia_mailer.entrypoints", "boto3"),
        "adapters": ("insolvia_mailer.api", "insolvia_mailer.entrypoints", "flask"),
        "entrypoints": ("flask",),
    }

    for layer, prefixes in forbidden.items():
        for path in sorted((PACKAGE / layer).rglob("*.py")):
            for imported in sorted(_imports(path)):
                crosses_boundary = any(
                    imported == prefix or imported.startswith(f"{prefix}.")
                    for prefix in prefixes
                )
                if crosses_boundary:
                    violations.append(f"{path.relative_to(PACKAGE)} imports {imported}")

    assert not violations, "invalid Mailer dependencies:\n" + "\n".join(violations)
