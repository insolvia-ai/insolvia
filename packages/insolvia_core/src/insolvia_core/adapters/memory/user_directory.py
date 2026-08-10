from __future__ import annotations

import uuid

from insolvia_core.errors import ConflictError


class MemoryUserDirectory:
    """Ephemeral UserDirectory for tests and the plain development server.

    Mints a uuid4 where Cognito would mint a subject, and — the part that
    matters — refuses a duplicate address exactly as Cognito's
    `UsernameExistsException` does. A fake that happily created the same
    address twice would let a suite pass on code that attaches a firm-user row
    to somebody else's account.

    It sends no invitation, which is the honest local shape: there is no mail
    here. `subjects` is public so a test can read back what a route created.
    """

    def __init__(self) -> None:
        self.subjects: dict[str, str] = {}

    def create_user(self, email: str) -> str:
        # Case-INSENSITIVE, because the real pool is: `infra/modules/auth` sets
        # `username_configuration { case_sensitive = false }` (issue #179), so
        # Cognito refuses `A@X.TEST` when `a@x.test` already exists. In
        # practice every address arrives lower-cased from
        # insolvia_core.firms._parse_email — the AWS adapter's docstring owns
        # that story — but an exact-match fake would still be weaker than
        # production for any caller that skips the parser.
        if any(existing.lower() == email.lower() for existing in self.subjects):
            raise ConflictError("that email address already has an Insolvia account")
        subject = str(uuid.uuid4())
        self.subjects[email] = subject
        return subject
