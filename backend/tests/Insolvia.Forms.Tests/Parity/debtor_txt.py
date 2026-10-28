"""The Case Upload parity oracle: Debtor.txt as services/api builds it.

For each of the four parity case files the Engines suite shares
(backend/tests/Insolvia.Engines.Tests/Parity/*.case.json), this builds the
Case Upload file with services/api's `case_upload.build_debtor_txt` and
writes its exact bytes to `Parity/debtor-txt/<case>.Debtor.txt`.
CaseUploadParityTests builds the same file with Insolvia.Forms and asserts
the bytes are identical — encoding, line endings, delimiters and blanks
included.

The two reference cases name no court; they are placed in FLMB's first
division, as services/api's test_case_upload places them (`in_court`). The
two seed samples already sit in FLMB. Their tax ids are the SSA's
advertising block (987-65-4320..4329), never issued. Run from the repo root:

    PYTHONPATH=services/api/src:packages/insolvia_core/src \\
      services/api/.venv/bin/python backend/tests/Insolvia.Forms.Tests/Parity/debtor_txt.py

The output is deterministic; a diff after regenerating is a behaviour change
in the Python oracle and must be reviewed.
"""

from __future__ import annotations

import importlib.util
import sys
from dataclasses import replace
from pathlib import Path

from insolvia_api.core.case_upload import build_debtor_txt, resolve_spec
from insolvia_core import courts

HERE = Path(__file__).resolve().parent
OUT = HERE / "debtor-txt"


def _load(path: Path, name: str):
    spec = importlib.util.spec_from_file_location(name, path)
    assert spec and spec.loader
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def main() -> int:
    dump = _load(HERE / "dump.py", "forms_parity_dump")
    generate = dump.generate_module()
    as_of = dump.AS_OF
    registry = courts.resolve(as_of)
    spec = resolve_spec(as_of)
    OUT.mkdir(exist_ok=True)
    for name in dump.CASE_FILES:
        data = dump.case_data(name, generate)
        if data.case.court is None:
            district = registry.district("flmb")
            assert district is not None
            data = replace(
                data,
                case=replace(
                    data.case,
                    court="flmb",
                    division=district.divisions[0].code,
                    district=district.name,
                ),
            )
        built = build_debtor_txt(data, as_of=as_of, registry=registry, spec=spec)
        (OUT / f"{name}.Debtor.txt").write_bytes(built.content)
        print(f"{name}: {len(built.content)} bytes, sha256 {built.sha256}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
