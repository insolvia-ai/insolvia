namespace Insolvia.Engines;

// The Python exception kinds the engines raise and their callers tell apart,
// carried over as types: a registry has no release for a date (LookupError),
// a table has no row for a key (KeyError — a LookupError too, and str() of
// one is the repr of its message, which some problem lists print verbatim),
// a value no rule accepts (ValueError), and a committed release that does not
// load (the loaders' ValueError).

/// <summary>Python's <c>LookupError</c>: nothing in force for the date asked about, or no row for a key.</summary>
public class EngineLookupException : Exception
{
    /// <summary>Creates the exception with no message.</summary>
    public EngineLookupException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public EngineLookupException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and its cause.</summary>
    public EngineLookupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Python's <c>KeyError</c>: a table with no row for the key asked for.
/// <see cref="Py.Str"/> renders it the way <c>str(KeyError(...))</c> does.
/// </summary>
public sealed class EngineKeyException : EngineLookupException
{
    /// <summary>Creates the exception with no message.</summary>
    public EngineKeyException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public EngineKeyException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and its cause.</summary>
    public EngineKeyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Python's <c>ValueError</c> from a computation: an input no rule accepts.</summary>
public sealed class EngineValueException : Exception
{
    /// <summary>Creates the exception with no message.</summary>
    public EngineValueException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public EngineValueException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and its cause.</summary>
    public EngineValueException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A committed regulatory release that does not load — the loaders' <c>ValueError</c>.</summary>
public sealed class MalformedReleaseException : Exception
{
    /// <summary>Creates the exception with no message.</summary>
    public MalformedReleaseException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public MalformedReleaseException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and its cause.</summary>
    public MalformedReleaseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
