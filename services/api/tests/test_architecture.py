# Enforces the layered dependency direction so the layering cannot rot silently:
# core depends on nothing, api depends only on core, adapters own the infra
# imports, and only entrypoints compose the two sides together.
from __future__ import annotations

import ast
from pathlib import Path

PACKAGE = Path(__file__).parents[1] / "src" / "insolvia_api"


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
    # `insolvia_core`'s domain modules count as core-direction imports and are
    # allowed everywhere; its ADAPTERS are adapters, and the same layering
    # applies to them as to our own (issue #208).
    forbidden = {
        "core": (
            "insolvia_api.api",
            "insolvia_api.adapters",
            "insolvia_api.entrypoints",
            "insolvia_core.adapters",
            "boto3",
            "flask",
        ),
        "api": (
            "insolvia_api.adapters",
            "insolvia_api.entrypoints",
            "insolvia_core.adapters",
            "boto3",
        ),
        "adapters": ("insolvia_api.api", "insolvia_api.entrypoints", "flask"),
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

    assert not violations, "invalid Insolvia API dependencies:\n" + "\n".join(
        violations
    )
