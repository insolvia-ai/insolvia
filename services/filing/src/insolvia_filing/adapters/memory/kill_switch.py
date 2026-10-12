"""The local kill switch: a value set at composition (FILING_SUBMISSIONS_
ENABLED) and, in tests, flipped mid-run to prove the re-check before the
final submit reads it again."""

from __future__ import annotations


class StaticKillSwitch:
    def __init__(self, enabled: bool) -> None:
        self.enabled = enabled
        self.reads = 0

    def submissions_enabled(self) -> bool:
        self.reads += 1
        return self.enabled
