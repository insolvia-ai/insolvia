namespace Insolvia.Domain.CaseEntities;

/// <summary>The hand parsers' error accumulator — the debtor's, until its routes bind a typed request (ADR 0032).</summary>
public static class IdRows
{
    /// <summary>Throws the accumulated errors, if any.</summary>
    public static void ThrowIfAny(IDictionary<string, string> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (errors.Count > 0)
        {
            throw new FieldValidationException(new Dictionary<string, string>(errors, StringComparer.Ordinal));
        }
    }
}
