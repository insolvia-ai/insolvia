"""Who is asking, and what they are allowed to see.

THE ONE PLACE THE ACCESS RULE IS WRITTEN DOWN. Every store, every route and
every test compares against `may_see_case` rather than re-deriving it, because
a rule spelled out in three places is a rule that will eventually be spelled
out three different ways — and the failure mode of that is one firm reading
another's cases.

`Accessor` is deliberately not a bag of strings. It can only be constructed
from a resolved firm and a resolved firm user, so a function holding one knows
— from the type, without checking — that the caller is authenticated, belongs
to a firm, and is active in it. That is what `api/auth.py`'s `current_accessor`
exists to produce, and why routes take an Accessor rather than a subject.

Pure: no Flask, no boto3, no clock.
"""

from __future__ import annotations

from dataclasses import dataclass

from insolvia_core.firms import Firm, FirmUser, permits

from insolvia_api.core.cases import Case


@dataclass(frozen=True)
class Accessor:
    """An authenticated caller whose firm has been resolved.

    Holds the whole `FirmUser` rather than a copy of the fields that matter,
    so a permission check reads the same record the administration screen
    writes. A flattened copy would be a second place for `is_admin` to live.
    """

    firm: Firm
    user: FirmUser

    @property
    def subject(self) -> str:
        return self.user.subject

    @property
    def firm_id(self) -> str:
        return self.firm.id

    @property
    def sees_every_case(self) -> bool:
        """Whether this caller reaches their firm's cases without being linked
        to them one by one.

        Two independent reasons, and they stay independent: `is_admin` is
        MyCase's Admin User — every feature, every case, and the ability to
        manage staff — while `access_all_cases` is the narrower switch that
        gives a supervising attorney the whole caseload and nothing else.
        Collapsing them into one flag would mean the only way to see every
        matter is to also be able to change everyone's permissions.

        This is also WHICH INDEX A LIST QUERY USES: true reads `by-firm`,
        false reads `by-assignee`. See CaseStore.list_for_accessor.
        """
        return self.user.is_admin or self.user.access_all_cases

    def may(self, feature: str, level: str) -> bool:
        """Per-feature permission, delegated to core/firms so the fail-closed
        rules live in one place."""
        return permits(self.user, feature, level)


def may_see_case(accessor: Accessor, case: Case, *, assigned: bool) -> bool:
    """Whether `accessor` may see `case`. The whole rule, in one expression.

    Two conditions, and the first is the tenant boundary: a case belonging to
    another firm is invisible no matter who is asking, admin included. There is
    no "super admin" in this model — an admin is an admin OF A FIRM.

    The second is the linkage rule. Being in the right firm is not enough
    unless you are an admin, carry `access_all_cases`, or are linked to this
    particular matter. `assigned` is a FACT THE CALLER MUST SUPPLY — this
    function cannot read the store, so it cannot go and look — and every caller
    gets it from the same place: the ASSIGNEE#<subject> item in the case's own
    partition, read in the same round trip as the case itself.

    Passing `assigned=True` without having read that item is the way to break
    this. Both store implementations read it; nothing else calls this.
    """
    if case.firm_id != accessor.firm_id:
        return False
    return accessor.sees_every_case or assigned
