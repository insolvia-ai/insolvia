"""Where this unit's directory is, counted ONCE.

Three test modules used to each count their own way up to `src/` with
`Path(__file__).parents[n]`, and every one of them broke the moment the unit
tests moved one directory deeper into `tests/unit/`. This is that constant.

Deliberately NOT in `conftest.py`: a conftest is fixtures, and importing a
constant out of one reads like a mistake even when it works.
"""

from pathlib import Path

#: The services/mailer directory — `tests/paths.py` -> `tests/` -> here.
UNIT_DIR = Path(__file__).resolve().parents[1]
#: The repo root, for tests that read something outside this unit.
REPO_ROOT = UNIT_DIR.parents[1]
#: The package under test.
PACKAGE = UNIT_DIR / "src" / "insolvia_mailer"
