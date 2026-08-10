# Enforces this package's own dependency direction, mirroring the services'
# test_architecture.py: the domain modules depend on nothing but each other and
# the stdlib; adapters own the infra imports; nothing anywhere imports a web
# framework or reaches back into a service.
from __future__ import annotations

import ast
from pathlib import Path

PACKAGE = Path(__file__).parents[1] / "src" / "insolvia_core"

# The domain: everything in the package root. Adapters are the one subpackage.
DOMAIN_FORBIDDEN = ("insolvia_core.adapters", "boto3", "botocore", "flask")
# `insolvia_api` / `insolvia_admin` in either layer would invert the extraction
# this package exists for (issue #208): services import the core, never the
# other way.
EVERYWHERE_FORBIDDEN = ("insolvia_api", "insolvia_admin", "flask")


def _imports(path: Path) -> set[str]:
    tree = ast.parse(path.read_text(), filename=str(path))
    result: set[str] = set()
    for node in ast.walk(tree):
        if isinstance(node, ast.Import):
            result.update(alias.name for alias in node.names)
        elif isinstance(node, ast.ImportFrom) and node.module:
            result.add(node.module)
    return result


def _violations(paths: list[Path], forbidden: tuple[str, ...]) -> list[str]:
    found: list[str] = []
    for path in paths:
        for imported in sorted(_imports(path)):
            if any(
                imported == prefix or imported.startswith(f"{prefix}.")
                for prefix in forbidden
            ):
                found.append(f"{path.relative_to(PACKAGE)} imports {imported}")
    return found


def test_domain_depends_on_nothing() -> None:
    domain = sorted(PACKAGE.glob("*.py"))
    violations = _violations(domain, DOMAIN_FORBIDDEN + EVERYWHERE_FORBIDDEN)
    assert not violations, "invalid insolvia_core dependencies:\n" + "\n".join(
        violations
    )


def test_nothing_reaches_back_into_a_service() -> None:
    everything = sorted(PACKAGE.rglob("*.py"))
    violations = _violations(everything, EVERYWHERE_FORBIDDEN)
    assert not violations, "invalid insolvia_core dependencies:\n" + "\n".join(
        violations
    )
