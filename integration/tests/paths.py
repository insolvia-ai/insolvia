"""Where the repository is, counted once."""

from pathlib import Path

#: The repo root: integration/tests/paths.py -> integration/tests -> integration -> here.
REPO_ROOT = Path(__file__).resolve().parents[2]
