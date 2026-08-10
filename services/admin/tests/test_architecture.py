# Enforces the layered dependency direction so the layering cannot rot
# silently — same rule and mechanism as the sibling services' copies: core
# depends on nothing else, api depends only on core, adapters own the infra
# imports, and only entrypoints compose the two sides. `insolvia_core`'s
# domain modules count as core-direction imports; its ADAPTERS are adapters.
from __future__ import annotations

import ast
from pathlib import Path

PACKAGE = Path(__file__).parents[1] / "src" / "insolvia_admin"


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
            "insolvia_admin.api",
            "insolvia_admin.adapters",
            "insolvia_admin.entrypoints",
            "insolvia_core.adapters",
            "boto3",
            "flask",
        ),
        "api": (
            "insolvia_admin.adapters",
            "insolvia_admin.entrypoints",
            "insolvia_core.adapters",
            "boto3",
        ),
        "adapters": ("insolvia_admin.api", "insolvia_admin.entrypoints", "flask"),
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

    # The tenant API is out of bounds EVERYWHERE — importing it would undo the
    # insolvia_core extraction from the consuming side (ADR 0012).
    for path in sorted(PACKAGE.rglob("*.py")):
        for imported in sorted(_imports(path)):
            if imported == "insolvia_api" or imported.startswith("insolvia_api."):
                violations.append(f"{path.relative_to(PACKAGE)} imports {imported}")

    assert not violations, "invalid Insolvia admin dependencies:\n" + "\n".join(
        violations
    )
