using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Insolvia.Core.Fields;
using Insolvia.Core.Ports;

namespace Insolvia.Core.Firms;

/// <summary>
/// The firm domain's request parsers (from <c>firms.py</c>). Wire field names
/// are the API's camelCase (<c>firstName</c>, <c>isAdmin</c>, …) except the
/// signature block and letterhead members, which are a
/// <c>filing_professional</c> body's snake_case. Unknown keys are ignored.
/// </summary>
public static partial class FirmParser
{
    /// <summary>The cap on a firm name.</summary>
    public const int MaxFirmName = 200;

    /// <summary>The cap on a WHOLE display name (the legacy <c>displayName</c> arm).</summary>
    public const int MaxDisplayName = 200;

    /// <summary>The cap on one HALF of a name — two at 200 would allow a 401-character display string.</summary>
    public const int MaxNamePart = 100;

    /// <summary>The cap on an email address.</summary>
    public const int MaxEmail = 320;

    // The chapters a firm may default a new case to — `cases.CHAPTERS`, restated.
    private static readonly int[] DefaultChapters = [7, 11, 12, 13];

    /// <summary>Validate a firm creation request. Status is not accepted: every firm starts active.</summary>
    public static FirmDraft ParseFirmCreation(JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var errors = Errors();
        var name = ParseName(payload["name"], errors, "name", MaxFirmName);
        if (errors.Count > 0 || name is null)
        {
            throw new FieldValidationException(errors);
        }

        return new FirmDraft(name);
    }

    /// <summary>
    /// Validate POST /v1/firm/users. <c>role</c> is required; supplied
    /// <c>permissions</c> MERGE over the role's defaults rather than replace
    /// them, so granting one feature does not silently revoke the rest.
    /// </summary>
    public static FirmUserDraft ParseFirmUserCreation(JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var errors = Errors();
        var email = ParseEmail(payload["email"], errors);

        // The explicit pair wins outright when present; otherwise the
        // transition arm reads a legacy `displayName`.
        (string First, string Last)? names;
        if (payload.ContainsKey("firstName") || payload.ContainsKey("lastName"))
        {
            var first = ParseName(payload["firstName"], errors, "firstName", MaxNamePart);
            var last = ParseName(payload["lastName"], errors, "lastName", MaxNamePart);
            names = first is not null && last is not null ? (first, last) : null;
        }
        else
        {
            names = ParseLegacyName(payload["displayName"], errors);
        }

        var role = ParseRole(payload["role"], errors);

        var isAdmin = false;
        if (payload.ContainsKey("isAdmin") && ParseFlag(payload["isAdmin"], errors, "isAdmin") is { } admin)
        {
            isAdmin = admin;
        }

        var accessAllCases = false;
        if (payload.ContainsKey("accessAllCases") && ParseFlag(payload["accessAllCases"], errors, "accessAllCases") is { } all)
        {
            accessAllCases = all;
        }

        IReadOnlyDictionary<Feature, PermissionLevel> overrides = new Dictionary<Feature, PermissionLevel>();
        if (payload.ContainsKey("permissions") && ParsePermissions(payload["permissions"], errors) is { } parsed)
        {
            overrides = parsed;
        }

        if (errors.Count > 0 || email is null || names is null || role is null)
        {
            throw new FieldValidationException(errors);
        }

        var permissions = Permissions.DefaultFor(role.Value).Concat(overrides).ToValueDictionary();
        return new FirmUserDraft(email, names.Value.First, names.Value.Last, role.Value, isAdmin, accessAllCases, permissions);
    }

    /// <summary>
    /// Validate PATCH /v1/firm/users/&lt;subject&gt;. <c>email</c> is NOT
    /// changeable (a pool operation); <c>permissions</c> REPLACES the map (a
    /// merging PATCH could only ever grant).
    /// </summary>
    public static FirmUserChanges ParseFirmUserUpdate(JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var errors = Errors();
        var changes = new FirmUserChanges();
        var any = false;

        if (payload.ContainsKey("firstName") && ParseName(payload["firstName"], errors, "firstName", MaxNamePart) is { } first)
        {
            changes = changes with { FirstName = first };
            any = true;
        }

        if (payload.ContainsKey("lastName") && ParseName(payload["lastName"], errors, "lastName", MaxNamePart) is { } last)
        {
            changes = changes with { LastName = last };
            any = true;
        }

        if (payload.ContainsKey("displayName") && !payload.ContainsKey("firstName") && !payload.ContainsKey("lastName")
            && ParseLegacyName(payload["displayName"], errors) is { } legacy)
        {
            changes = changes with { FirstName = legacy.First, LastName = legacy.Last };
            any = true;
        }

        if (payload.ContainsKey("role") && ParseRole(payload["role"], errors) is { } role)
        {
            changes = changes with { Role = role };
            any = true;
        }

        if (payload.ContainsKey("isAdmin") && ParseFlag(payload["isAdmin"], errors, "isAdmin") is { } isAdmin)
        {
            changes = changes with { IsAdmin = isAdmin };
            any = true;
        }

        if (payload.ContainsKey("accessAllCases") && ParseFlag(payload["accessAllCases"], errors, "accessAllCases") is { } accessAll)
        {
            changes = changes with { AccessAllCases = accessAll };
            any = true;
        }

        if (payload.ContainsKey("permissions") && ParsePermissions(payload["permissions"], errors) is { } permissions)
        {
            changes = changes with { Permissions = permissions };
            any = true;
        }

        if (payload.ContainsKey("status"))
        {
            if (Wire.TryParse<FirmUserStatus>(Json.AsString(payload["status"]), out var status))
            {
                changes = changes with { Status = status };
                any = true;
            }
            else
            {
                errors["status"] = "Status must be one of " + string.Join(", ", Wire.Names<FirmUserStatus>()) + ".";
            }
        }

        if (payload.ContainsKey("signatureBlock") && ParseSignatureBlock(payload["signatureBlock"], errors) is { } block)
        {
            changes = changes with { SignatureBlock = block };
            any = true;
        }

        return Finish(changes, any, errors);
    }

    /// <summary>
    /// Validate PATCH /v1/me: one's own name (either half alone) and one's own
    /// signature block. Everything else on the row is somebody else's
    /// statement about the member and is ignored here.
    /// </summary>
    public static FirmUserChanges ParseSelfUpdate(JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var errors = Errors();
        var changes = new FirmUserChanges();
        var any = false;

        if (payload.ContainsKey("firstName") && ParseName(payload["firstName"], errors, "firstName", MaxNamePart) is { } first)
        {
            changes = changes with { FirstName = first };
            any = true;
        }

        if (payload.ContainsKey("lastName") && ParseName(payload["lastName"], errors, "lastName", MaxNamePart) is { } last)
        {
            changes = changes with { LastName = last };
            any = true;
        }

        if (!any && errors.Count == 0 && payload.ContainsKey("displayName")
            && ParseLegacyName(payload["displayName"], errors) is { } legacy)
        {
            changes = changes with { FirstName = legacy.First, LastName = legacy.Last };
            any = true;
        }

        if (payload.ContainsKey("signatureBlock") && ParseSignatureBlock(payload["signatureBlock"], errors) is { } block)
        {
            changes = changes with { SignatureBlock = block };
            any = true;
        }

        return Finish(changes, any, errors);
    }

    /// <summary>
    /// Validate PATCH /v1/firm: the name, the three defaults and the
    /// letterhead (issue #360). <c>defaultCourt</c>/<c>defaultDivision</c> are
    /// a PAIR, validated against <paramref name="courts"/> as a case's own
    /// reference is. A body carrying only <c>status</c> is "no supported fields".
    /// </summary>
    public static FirmChanges ParseFirmUpdate(JsonObject payload, ICourtRegistry courts)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(courts);
        var errors = Errors();
        var changes = new FirmChanges();
        var any = false;

        if (payload.ContainsKey("name") && ParseName(payload["name"], errors, "name", MaxFirmName) is { } name)
        {
            changes = changes with { Name = name };
            any = true;
        }

        if ((payload.ContainsKey("defaultCourt") || payload.ContainsKey("defaultDivision"))
            && ParseDefaultCourt(payload, errors, courts) is { } court)
        {
            changes = changes with { DefaultCourt = court };
            any = true;
        }

        if (payload.ContainsKey("defaultChapter") && ParseDefaultChapter(payload["defaultChapter"], errors) is { } chapter)
        {
            changes = changes with { DefaultChapter = chapter };
            any = true;
        }

        if (payload.ContainsKey("letterhead") && ParseLetterhead(payload["letterhead"], errors) is { } letterhead)
        {
            changes = changes with { Letterhead = letterhead };
            any = true;
        }

        if (errors.Count > 0)
        {
            throw new FieldValidationException(errors);
        }

        if (!any)
        {
            throw new ValidationException("no supported fields to update");
        }

        return changes;
    }

    /// <summary>
    /// An email address, trimmed and LOWER-CASED (addresses are matched; a
    /// capital is not a second person). Shared with the client-portal
    /// invitation parser.
    /// </summary>
    internal static string? ParseEmail(JsonNode? value, Dictionary<string, string> errors)
    {
        var raw = Json.AsString(value);
        if (raw is null || TextRules.Strip(raw).Length == 0)
        {
            errors["email"] = "An email address is required.";
            return null;
        }

        var email = TextRules.Lower(TextRules.Strip(raw));
        if (TextRules.Length(email) > MaxEmail)
        {
            errors["email"] = $"Please keep this under {MaxEmail} characters.";
            return null;
        }

        if (!EmailShape().IsMatch(email))
        {
            errors["email"] = "That doesn't look like a valid email address.";
            return null;
        }

        return email;
    }

    // Deliberately loose: Cognito, which sends to it, is what establishes it.
    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailShape();

    private static Dictionary<string, string> Errors() => new(StringComparer.Ordinal);

    private static FirmUserChanges Finish(FirmUserChanges changes, bool any, Dictionary<string, string> errors)
    {
        if (errors.Count > 0)
        {
            throw new FieldValidationException(errors);
        }

        if (!any)
        {
            throw new ValidationException("no supported fields to update");
        }

        return changes;
    }

    private static string? ParseName(JsonNode? value, Dictionary<string, string> errors, string field, int cap)
    {
        var raw = Json.AsString(value);
        if (raw is null || TextRules.Strip(raw).Length == 0)
        {
            errors[field] = "A name is required.";
            return null;
        }

        var name = TextRules.Strip(raw);
        if (TextRules.Length(name) > cap)
        {
            errors[field] = $"Please keep this under {cap} characters.";
            return null;
        }

        return name;
    }

    // THE TRANSITION ARM: a client written before the name split still sends
    // one `displayName`. A single token is REFUSED rather than stored with an
    // empty surname.
    private static (string First, string Last)? ParseLegacyName(JsonNode? value, Dictionary<string, string> errors)
    {
        var name = ParseName(value, errors, "displayName", MaxDisplayName);
        if (name is null)
        {
            return null;
        }

        var (first, last) = FirmNames.SplitLegacyName(name);
        if (last.Length == 0)
        {
            errors["displayName"] = "Please give both a first and a last name.";
            return null;
        }

        return (first, last);
    }

    private static FirmRole? ParseRole(JsonNode? value, Dictionary<string, string> errors)
    {
        if (Wire.TryParse<FirmRole>(Json.AsString(value), out var role))
        {
            return role;
        }

        errors["role"] = "Role must be one of " + string.Join(", ", Wire.Names<FirmRole>()) + ".";
        return null;
    }

    // Strictly a JSON boolean: `"false"` is a non-empty string, and a truthy
    // check is the classic way an admin flag gets set by accident.
    private static bool? ParseFlag(JsonNode? value, Dictionary<string, string> errors, string field)
    {
        var flag = Json.AsBool(value);
        if (flag is null)
        {
            errors[field] = "Must be true or false.";
        }

        return flag;
    }

    // Unknown features are REFUSED, not dropped: dropping would leave the
    // admin believing they granted something.
    private static ValueDictionary<Feature, PermissionLevel>? ParsePermissions(JsonNode? value, Dictionary<string, string> errors)
    {
        if (value is not JsonObject map)
        {
            errors["permissions"] = "Permissions must be an object.";
            return null;
        }

        var parsed = new List<KeyValuePair<Feature, PermissionLevel>>();
        foreach (var (key, level) in map)
        {
            if (!Wire.TryParse<Feature>(key, out var feature))
            {
                errors["permissions"] = "Unknown feature. Features are " + string.Join(", ", Wire.Names<Feature>()) + ".";
                return null;
            }

            if (!Wire.TryParse<PermissionLevel>(Json.AsString(level), out var parsedLevel))
            {
                errors["permissions"] = "Each level must be one of " + string.Join(", ", Wire.Names<PermissionLevel>()) + ".";
                return null;
            }

            parsed.Add(new(feature, parsedLevel));
        }

        return parsed.ToValueDictionary();
    }

    private static Assign<Letterhead?>? ParseLetterhead(JsonNode? value, Dictionary<string, string> errors)
    {
        if (value is null)
        {
            return new Assign<Letterhead?>(null);
        }

        if (value is not JsonObject raw)
        {
            errors["letterhead"] = "Must be an object.";
            return null;
        }

        var before = errors.Count;
        var parsed = new Letterhead(
            Name: FieldParsers.Text(raw["name"], "letterhead.name", errors, MaxFirmName),
            Address: FieldParsers.ParseAddress(raw["address"], "letterhead.address", errors),
            Phone: FieldParsers.Text(raw["phone"], "letterhead.phone", errors, 32),
            Email: FieldParsers.Text(raw["email"], "letterhead.email", errors, MaxEmail));
        return errors.Count > before ? null : new Assign<Letterhead?>(parsed);
    }

    // The limits are `petitions.parse_filing_professional`'s: the block is
    // copied onto that record and must not hold what it would refuse.
    private static Assign<SignatureBlock?>? ParseSignatureBlock(JsonNode? value, Dictionary<string, string> errors)
    {
        if (value is null)
        {
            return new Assign<SignatureBlock?>(null);
        }

        if (value is not JsonObject raw)
        {
            errors["signatureBlock"] = "Must be an object.";
            return null;
        }

        var before = errors.Count;
        const string prefix = "signatureBlock";
        var parsed = new SignatureBlock(
            BarNumber: FieldParsers.Text(raw["bar_number"], $"{prefix}.bar_number", errors, 32),
            BarState: FieldParsers.Text(raw["bar_state"], $"{prefix}.bar_state", errors, 2),
            FirmName: FieldParsers.Text(raw["firm_name"], $"{prefix}.firm_name", errors),
            Address: FieldParsers.ParseAddress(raw["address"], $"{prefix}.address", errors),
            Phone: FieldParsers.Text(raw["phone"], $"{prefix}.phone", errors, 32),
            Email: FieldParsers.Text(raw["email"], $"{prefix}.email", errors));
        return errors.Count > before ? null : new Assign<SignatureBlock?>(parsed);
    }

    // Both strings to set, both null to clear; one without the other is
    // refused — a division only means something inside its court.
    private static Assign<DefaultCourt?>? ParseDefaultCourt(JsonObject payload, Dictionary<string, string> errors, ICourtRegistry courts)
    {
        var court = payload["defaultCourt"];
        var division = payload["defaultDivision"];
        if (court is null && division is null)
        {
            return new Assign<DefaultCourt?>(null);
        }

        var courtCode = Json.AsString(court);
        if (courtCode is null || TextRules.Strip(courtCode).Length == 0)
        {
            errors["defaultCourt"] = "Choose a court from the registry, or clear both.";
            return null;
        }

        var divisionCode = Json.AsString(division);
        if (divisionCode is null || TextRules.Strip(divisionCode).Length == 0)
        {
            errors["defaultDivision"] = "Choose the court's division, or clear both.";
            return null;
        }

        courtCode = TextRules.Strip(courtCode);
        divisionCode = TextRules.Strip(divisionCode);
        if (!courts.HasDivision(courtCode, divisionCode))
        {
            if (!courts.HasDistrict(courtCode))
            {
                errors["defaultCourt"] = "That court is not in the registry.";
            }
            else
            {
                errors["defaultDivision"] = "That division is not one of this court's.";
            }

            return null;
        }

        return new Assign<DefaultCourt?>(new DefaultCourt(courtCode, divisionCode));
    }

    private static Assign<int?>? ParseDefaultChapter(JsonNode? value, Dictionary<string, string> errors)
    {
        if (value is null)
        {
            return new Assign<int?>(null);
        }

        if (!Json.TryGetWholeNumber(value, out var chapter))
        {
            errors["defaultChapter"] = "Chapter must be a number.";
            return null;
        }

        if (!DefaultChapters.Any(allowed => allowed == chapter))
        {
            errors["defaultChapter"] = "Chapter must be one of " + string.Join(", ", DefaultChapters) + ".";
            return null;
        }

        return new Assign<int?>((int)chapter);
    }
}

/// <summary>The name split, written once so the parser's transition arm and the stored-row read cannot disagree.</summary>
public static class FirmNames
{
    /// <summary>
    /// Split a single legacy display name on its LAST space ("Mary Anne
    /// Smith" keeps "Mary Anne"). A single token yields an empty surname — we
    /// do not know it, and saying so is what lets the client ask.
    /// </summary>
    public static (string First, string Last) SplitLegacyName(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var trimmed = TextRules.Strip(value);
        if (trimmed.Length == 0)
        {
            return (string.Empty, string.Empty);
        }

        var space = trimmed.LastIndexOf(' ');
        return space < 0
            ? (trimmed, string.Empty)
            : (TextRules.Strip(trimmed[..space]), TextRules.Strip(trimmed[(space + 1)..]));
    }
}
