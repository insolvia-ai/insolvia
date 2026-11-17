"""What ships: the image carries the worker and the API's source, never the
fake court; and the PDF library is the API's exact pin."""

from __future__ import annotations

import re
from pathlib import Path

UNIT_DIR = Path(__file__).resolve().parents[2]
REPO_ROOT = UNIT_DIR.parents[1]


def _copies(dockerfile: str) -> list[str]:
    return [
        line.split()[1] for line in dockerfile.splitlines() if line.startswith("COPY ")
    ]


def test_the_image_never_copies_the_fake_court():
    copies = _copies((UNIT_DIR / "Dockerfile").read_text())
    assert copies
    assert not any("fake" in source for source in copies)
    assert "services/filing/src/" in copies


def test_the_pdf_library_is_the_apis_exact_pin():
    pin = re.compile(r"^pypdf==\S+$", re.MULTILINE)
    ours = pin.findall((UNIT_DIR / "requirements.txt").read_text())
    theirs = pin.findall((REPO_ROOT / "services/api/requirements.txt").read_text())
    assert ours == theirs
    assert len(ours) == 1
