namespace Insolvia.Data.Pagination;

/// <summary>
/// A cursor this service did not mint for this query. A store translates it
/// into the domain's validation error ("cursor is not valid", 400).
/// </summary>
public sealed class InvalidCursorException : Exception
{
    /// <summary>The one message every cursor failure carries.</summary>
    public const string Text = "cursor is not valid";

    /// <summary>A rejected cursor.</summary>
    public InvalidCursorException()
        : base(Text)
    {
    }

    /// <summary>A rejected cursor, with the parse failure kept for logs.</summary>
    public InvalidCursorException(Exception innerException)
        : base(Text, innerException)
    {
    }

    /// <summary>Required by CA1032; the message is always <see cref="Text"/>.</summary>
    public InvalidCursorException(string message, Exception innerException)
        : base(Text, innerException)
    {
        _ = message;
    }

    /// <summary>Required by CA1032; the message is always <see cref="Text"/>.</summary>
    public InvalidCursorException(string message)
        : base(Text)
    {
        _ = message;
    }
}
