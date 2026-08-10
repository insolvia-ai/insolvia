class ApiError(Exception):
    """Base class for expected Insolvia API failures."""


class ValidationError(ApiError):
    """A caller or the environment supplied an invalid value."""


class FieldValidationError(ValidationError):
    """Per-field validation failures, keyed by the request's JSON field names.

    The API layer maps this to a 400 with an {"error", "fields"} body so the
    marketing site's action can surface each message next to its input.
    """

    def __init__(self, fields: dict[str, str]) -> None:
        super().__init__("validation failed: " + ", ".join(sorted(fields)))
        self.fields = fields


class ConflictError(ApiError):
    """The resource exists and the caller may see it, but its current state
    does not admit the request.

    Deliberately NOT a 404, and the distinction is the opposite of the one
    NotFoundError draws below. 404 is this codebase's anti-oracle answer: it
    hides whether a resource exists from a caller who has not proven they may
    know. By the time this is raised the caller has already proven it — they
    are an administrator of the firm they are acting on, or they own the case
    and the row resolved — so there is nothing left to hide, and answering 404
    would tell an honest client that a record it can see in its own listing is
    gone. It would then drop the record instead of retrying the step that
    failed.
    """


class ForbiddenError(ApiError):
    """The caller is authenticated, and still may not do this.

    A 403, and the one place this codebase deliberately does NOT hide behind a
    404 — which needs saying, because NotFoundError below argues the opposite
    for case ids. The difference is what a caller could learn:

      - "This case id belongs to another firm" is a fact about somebody else's
        data, and confirming it turns an endpoint into an enumeration oracle.
        404.
      - "You are not in a firm" and "your firm has not granted you documents"
        are facts about the CALLER'S OWN account. There is nothing to enumerate
        and no third party to protect, and a 404 here would send an honest
        client — and the person supporting them — hunting for a missing record
        instead of an unassigned permission.

    Two things raise it: a signed-in user with no firm user record (see
    api/auth.py's current_accessor), and a per-feature permission check.
    """


class NotFoundError(ApiError):
    """The requested resource does not exist, or does not belong to the caller.

    Deliberately one error for both. A case that exists but belongs to someone
    else answers 404, not 403: a 403 would confirm the id is real, which turns
    this endpoint into an oracle for enumerating other firms' case ids.
    """
