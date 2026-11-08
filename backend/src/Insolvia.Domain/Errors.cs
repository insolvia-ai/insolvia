namespace Insolvia.Core;

// The expected-failure hierarchy, ported from insolvia_core/errors.py. The
// API maps each type to a status code; the reasons each one exists (and why
// 403 and 404 are drawn where they are) are written once, on the type.

/// <summary>Base class for expected Insolvia API failures (Python's <c>ApiError</c>).</summary>
public class ApiException : Exception
{
    /// <summary>Creates the exception with no message.</summary>
    public ApiException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public ApiException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and its cause.</summary>
    public ApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A caller or the environment supplied an invalid value (Python's <c>ValidationError</c>).</summary>
public class ValidationException : ApiException
{
    /// <summary>Creates the exception with no message.</summary>
    public ValidationException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public ValidationException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and its cause.</summary>
    public ValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Per-field validation failures, keyed by the request's JSON field names
/// (Python's <c>FieldValidationError</c>). The API answers a 400 with an
/// <c>{"error", "fields"}</c> body so a form can put each message beside its input.
/// </summary>
public sealed class FieldValidationException : ValidationException
{
    /// <summary>Creates the exception from the accumulated per-field messages.</summary>
    public FieldValidationException(IReadOnlyDictionary<string, string> fields)
        : base(MessageFor(fields))
    {
        Fields = new Dictionary<string, string>(fields, StringComparer.Ordinal);
    }

    /// <summary>Field path to message, one entry per malformed field.</summary>
    public IReadOnlyDictionary<string, string> Fields { get; }

    private static string MessageFor(IReadOnlyDictionary<string, string> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        return "validation failed: " + string.Join(", ", fields.Keys.Order(StringComparer.Ordinal));
    }
}

/// <summary>
/// The resource exists and the caller may see it, but its current state does
/// not admit the request (Python's <c>ConflictError</c>) — deliberately a 409,
/// never a 404: the caller has already proven they may know the record exists.
/// </summary>
public class ConflictException : ApiException
{
    /// <summary>Creates the exception with no message.</summary>
    public ConflictException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public ConflictException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and its cause.</summary>
    public ConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A write linking a debtor to a firm client was refused by its condition on
/// the client row: archived, merged, or claimed by a merge between the route's
/// read and the write (Python's <c>ClientUnavailableError</c>).
/// <see cref="ClientId"/> says which client refused, never what about it.
/// </summary>
public sealed class ClientUnavailableException : ConflictException
{
    /// <summary>Creates the exception for the client that refused the link.</summary>
    public ClientUnavailableException(string clientId)
        : base("that client can no longer be linked to a case")
    {
        ClientId = clientId;
    }

    /// <summary>The client whose row refused the write.</summary>
    public string ClientId { get; }
}

/// <summary>
/// The caller is authenticated and still may not do this (Python's
/// <c>ForbiddenError</c>). A 403 — the one place the codebase does not hide
/// behind a 404, because what it reveals is a fact about the caller's own account.
/// </summary>
public class ForbiddenException : ApiException
{
    /// <summary>Creates the exception with no message.</summary>
    public ForbiddenException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public ForbiddenException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and its cause.</summary>
    public ForbiddenException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The requested resource does not exist, or does not belong to the caller
/// (Python's <c>NotFoundError</c>) — deliberately one answer for both, so an
/// id is never confirmed to a caller who may not know it.
/// </summary>
public class NotFoundException : ApiException
{
    /// <summary>Creates the exception with no message.</summary>
    public NotFoundException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public NotFoundException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and its cause.</summary>
    public NotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
