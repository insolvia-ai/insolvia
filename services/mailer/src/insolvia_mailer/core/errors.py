class MailerError(Exception):
    """Base class for expected Mailer failures."""


class ValidationError(MailerError):
    """A caller supplied an invalid request."""


class AuthorizationError(MailerError):
    """The authenticated caller is not registered for the service route."""


class ConflictError(MailerError):
    """An idempotency key was reused with different content."""


class RetryableError(MailerError):
    """The operation may succeed when retried."""


class AttachmentBlockedError(MailerError):
    """An attachment failed a terminal safety check."""
