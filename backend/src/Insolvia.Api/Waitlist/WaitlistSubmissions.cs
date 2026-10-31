using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Insolvia.Core;
using Insolvia.Data.Waitlist;

namespace Insolvia.Api.Waitlist;

/// <summary>
/// Waitlist validation and the record's identity (services/api
/// core/waitlist.py). The rules mirror the marketing site's original SSR
/// implementation (issue #47), which moved behind this API (ADR 0001).
/// </summary>
public static partial class WaitlistSubmissions
{
    /// <summary>
    /// Per-field caps, in the order the "keep this under" checks run: the
    /// marketing form's caps, and the DNS name limit for the host the
    /// marketing action sends.
    /// </summary>
    public static readonly IReadOnlyList<(string Field, int Cap)> MaxLengths =
    [
        ("name", 200),
        ("firm", 200),
        ("email", 320),
        ("currentSoftware", 100),
        ("message", 2000),
        ("host", 253),
    ];

    private static readonly string[] Fields = ["name", "firm", "email", "currentSoftware", "message", "host"];

    /// <summary>
    /// Validate a request body; a <see cref="FieldValidationException"/> keyed
    /// by the JSON field names on failure. A non-string value counts as
    /// missing. Unknown keys are ignored — the marketing form's honeypot is
    /// checked in its action and never reaches this API.
    /// </summary>
    /// <param name="payload">The body.</param>
    /// <returns>The trimmed submission.</returns>
    public static WaitlistSubmission Parse(JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var values = Fields.ToDictionary(key => key, key => Json.AsString(payload[key]) is { } text ? TextRules.Strip(text) : "", StringComparer.Ordinal);

        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        if (values["name"].Length == 0)
        {
            errors["name"] = "Please tell us your name.";
        }

        if (values["firm"].Length == 0)
        {
            errors["firm"] = "Please tell us your firm's name.";
        }

        if (values["email"].Length == 0)
        {
            errors["email"] = "A work email is required.";
        }
        else if (!EmailShape().IsMatch(values["email"]))
        {
            errors["email"] = "That doesn't look like a valid email address.";
        }

        foreach (var (field, cap) in MaxLengths)
        {
            if (!errors.ContainsKey(field) && TextRules.Length(values[field]) > cap)
            {
                errors[field] = $"Please keep this under {cap} characters.";
            }
        }

        if (errors.Count > 0)
        {
            throw new FieldValidationException(errors);
        }

        return new WaitlistSubmission(values["name"], values["firm"], values["email"], values["currentSoftware"], values["message"], values["host"]);
    }

    /// <summary>
    /// Stamp a submission with a uuid4 and the UTC instant to the
    /// millisecond — the marketing implementation's <c>toISOString()</c>.
    /// </summary>
    /// <param name="submission">The submission.</param>
    /// <param name="clock">The clock; the system clock when null.</param>
    /// <returns>The record.</returns>
    public static WaitlistRecord Create(WaitlistSubmission submission, TimeProvider? clock = null) =>
        new(Guid.NewGuid().ToString(), Clock.NowMillis(clock), submission);

    // Python's `re.match(r"^[^@\s]+@[^@\s]+\.[^@\s]+$")`: `$` also matches
    // before ONE trailing newline, which the strip() before it has removed.
    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+\z")]
    private static partial Regex EmailShape();
}
