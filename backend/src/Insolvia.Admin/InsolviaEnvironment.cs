namespace Insolvia.Admin;

/// <summary>
/// Which environment this process serves, read from <c>INSOLVIA_ENV</c>.
/// Mirrors <c>load_config</c> in services/admin: unset means <c>local</c>, and
/// any other value outside the three names refuses to start, so a typo
/// cannot silently run staging with local defaults.
/// </summary>
public sealed record InsolviaEnvironment
{
    /// <summary>The names <c>INSOLVIA_ENV</c> accepts.</summary>
    public static readonly IReadOnlyList<string> Names = ["local", "staging", "production"];

    private InsolviaEnvironment(string name) => Name = name;

    /// <summary>The environment's name, as <c>/health</c> reports it.</summary>
    public string Name { get; }

    /// <summary>Parses the value of <c>INSOLVIA_ENV</c>.</summary>
    /// <param name="value">The variable's value; null or empty means <c>local</c>.</param>
    /// <exception cref="ArgumentException">The value names no environment.</exception>
    public static InsolviaEnvironment FromVariable(string? value)
    {
        var name = string.IsNullOrEmpty(value) ? "local" : value;
        if (!Names.Contains(name, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"INSOLVIA_ENV must be one of {string.Join(", ", Names)}, got '{name}'.",
                nameof(value));
        }

        return new InsolviaEnvironment(name);
    }
}
