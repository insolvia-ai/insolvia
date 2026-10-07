"""The layering, and the two boundaries this service exists to keep:

- the API's code is reached for exactly what the digest and the filing set
  need (`ALLOWED_API`), never its web layer;
- the fake court never reaches `src/` — nothing imports it, so the image
  (which copies `src/insolvia_filing` alone) cannot contain it.
"""

from __future__ import annotations

import ast
from pathlib import Path

PACKAGE = Path(__file__).resolve().parents[2] / "src" / "insolvia_filing"

# What of services/api this worker may import, and why — README, "Why the
# worker imports insolvia_api.core". Each is the API's ONE owner of a fact the
# worker must reproduce exactly: the approval's digest and consume, the filing
# set, the packet record, the drawing primitives, and the stores of the
# approval and the packet.
ALLOWED_API = {
    "insolvia_api.core",
    "insolvia_api.core.filing_approval",
    "insolvia_api.core.filing_set",
    "insolvia_api.core.packets",
    "insolvia_api.core.ports",
    "insolvia_api.adapters.aws.filing_approval_store",
    "insolvia_api.adapters.aws.packet_store",
}


def _imports(path: Path) -> set[str]:
    tree = ast.parse(path.read_text(), filename=str(path))
    result: set[str] = set()
    for node in ast.walk(tree):
        if isinstance(node, ast.Import):
            result.update(alias.name for alias in node.names)
        elif isinstance(node, ast.ImportFrom) and node.module and node.level == 0:
            result.add(node.module)
    return result


def _relative_imports(path: Path) -> set[str]:
    tree = ast.parse(path.read_text(), filename=str(path))
    layer = path.relative_to(PACKAGE).parts[0]
    result: set[str] = set()
    for node in ast.walk(tree):
        if isinstance(node, ast.ImportFrom) and node.level > 0 and node.module:
            depth = len(path.relative_to(PACKAGE).parts) - 1
            if node.level > depth:
                continue
            if node.level == depth:
                result.add(f"insolvia_filing.{node.module}")
            else:
                result.add(f"insolvia_filing.{layer}.{node.module}")
    return result


def test_environment_dependency_boundaries():
    forbidden = {
        "core": (
            "insolvia_filing.adapters",
            "insolvia_filing.entrypoints",
            "insolvia_core.adapters",
            "insolvia_api.adapters",
            "boto3",
            "botocore",
        ),
        "adapters": ("insolvia_filing.entrypoints",),
        "entrypoints": (),
    }
    violations = []
    for layer, prefixes in forbidden.items():
        for path in sorted((PACKAGE / layer).rglob("*.py")):
            for imported in sorted(_imports(path) | _relative_imports(path)):
                if any(imported == p or imported.startswith(f"{p}.") for p in prefixes):
                    violations.append(f"{path.relative_to(PACKAGE)} imports {imported}")
    assert violations == []


def test_only_the_named_parts_of_the_api_are_imported():
    used = set()
    for path in PACKAGE.rglob("*.py"):
        used |= {name for name in _imports(path) if name.startswith("insolvia_api")}
    assert used <= ALLOWED_API, sorted(used - ALLOWED_API)


def test_nothing_in_src_imports_the_fake_court_or_a_web_framework():
    offenders = []
    for path in PACKAGE.rglob("*.py"):
        for name in _imports(path):
            if name.split(".")[0] in {"fake_cmecf", "flask", "mangum", "tests"}:
                offenders.append(f"{path.relative_to(PACKAGE)}: {name}")
    assert offenders == []
