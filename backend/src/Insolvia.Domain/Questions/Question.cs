using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Insolvia.Core.Candidates;
using Insolvia.Core.CaseEntities;
using Insolvia.Core.Clients;
using Insolvia.Core.Debtors;
using Insolvia.Core.Questionnaires;

namespace Insolvia.Core.Questions;

// The portal questionnaire's QUESTIONS and the answers a client gives (ADR
// 0023 PR 4; from questions.py). An answer is a CANDIDATE — the client
// channel's proposal — validated through the target's own parser and
// reviewed by staff like any other; it becomes case data only on acceptance.

/// <summary>
/// One box on the portal screen. <see cref="Key"/> is the key in the answer's
/// value and — for merge and nest — the field of the target body it lands in.
/// </summary>
public sealed record AnswerInput(string Key, string Label, AnswerInputType Type, bool Required = false, string? Help = null);

/// <summary>One catalogue question.</summary>
/// <param name="Id">Stable, <c>&lt;section&gt;.&lt;name&gt;</c>.</param>
/// <param name="Section">The questionnaire section it belongs to.</param>
/// <param name="Text">What the client reads.</param>
/// <param name="Target">The collection (or <c>debtors</c>) an answer proposes into.</param>
/// <param name="Inputs">The boxes.</param>
public sealed record Question(string Id, string Section, string Text, string Target, ValueCollection<AnswerInput> Inputs)
{
    /// <summary>Whether a client may give many answers (one per row).</summary>
    public bool Repeats { get; init; }

    /// <summary>Whether the answer is about the answering debtor.</summary>
    public bool PerDebtor { get; init; }

    /// <summary>Where the answer lands in the target body.</summary>
    public AnswerPlacement Placement { get; init; } = AnswerPlacement.Merge;

    /// <summary>The debtor field a nest or append answer lands in.</summary>
    public string? DebtorField { get; init; }

    /// <summary>Server-fixed members merged into every answer (a category, a status).</summary>
    public JsonObject Fixed { get; init; } = [];

    /// <summary>Help text under the question.</summary>
    public string? Help { get; init; }

    /// <summary>A retired question still labels old answers but takes no new ones.</summary>
    public bool Retired { get; init; }

    /// <summary>The inputs' keys, in order.</summary>
    public IReadOnlyList<string> InputKeys => [.. Inputs.Select(input => input.Key)];
}

/// <summary>A validated <c>POST /v1/portal/answers</c>.</summary>
public sealed record AnswerRequest(Question Question, ClientRole FilingRole, JsonObject Payload);

/// <summary>What a candidate's question locator says, read back.</summary>
public sealed record AnswerRef(Question Question, string FilingRole);

/// <summary>The catalogue and the answer rules.</summary>
public static class QuestionRules
{
    /// <summary>The candidate channel every answer carries.</summary>
    public const OriginChannel Channel = OriginChannel.Client;

    /// <summary>The locator kind that marks a candidate as an answer.</summary>
    public const string LocatorKind = "question";

    /// <summary>The target naming the debtor record.</summary>
    public const string DebtorsTarget = "debtors";

    /// <summary>A client may have this many answers waiting at once.</summary>
    public const int MaxPendingAnswers = 200;

    /// <summary>The members an address input carries — never <c>raw</c>, a machine's fallback.</summary>
    public static readonly IReadOnlyList<string> AddressMembers = ["line1", "line2", "city", "state", "postal_code", "county"];

    /// <summary>The questions, in the order the portal asks them.</summary>
    public static readonly ValueCollection<Question> Catalogue =
    [
        new("personal_information.legal_name", Questionnaire.PersonalInformation, "What is your full legal name?", DebtorsTarget,
            [
                new("given", "First name", AnswerInputType.Text, Required: true),
                new("middle", "Middle name", AnswerInputType.Text),
                new("surname", "Last name", AnswerInputType.Text, Required: true),
                new("suffix", "Suffix", AnswerInputType.Text, Help: "Jr., Sr., III"),
            ])
        {
            Help = "As it appears on your driver's license or other identification.",
            Placement = AnswerPlacement.Nest,
            DebtorField = "name",
            PerDebtor = true,
        },
        new("personal_information.other_names", Questionnaire.PersonalInformation, "Have you used any other names in the last 8 years?", DebtorsTarget,
            [
                new("given", "First name", AnswerInputType.Text),
                new("middle", "Middle name", AnswerInputType.Text),
                new("surname", "Last name", AnswerInputType.Text),
                new("business_name", "Business name", AnswerInputType.Text),
            ])
        {
            Help = "Include maiden names, earlier married names, and business names you used. Add one at a time.",
            Placement = AnswerPlacement.Append,
            DebtorField = "other_names_used",
            PerDebtor = true,
            Repeats = true,
        },
        new("personal_information.residence_address", Questionnaire.PersonalInformation, "Where do you live?", DebtorsTarget,
            [Address("residence_address", "Home address")])
        {
            Help = "The address of the home you live in now, including the county.",
            PerDebtor = true,
        },
        new("personal_information.mailing_address", Questionnaire.PersonalInformation, "Do you get mail somewhere else?", DebtorsTarget,
            [Address("mailing_address", "Mailing address")])
        {
            Help = "Only if your mail goes to a different address than your home.",
            PerDebtor = true,
        },
        new("personal_information.phone", Questionnaire.PersonalInformation, "What is the best phone number to reach you?", DebtorsTarget,
            [new("phone", "Phone number", AnswerInputType.Text, Required: true)])
        {
            PerDebtor = true,
        },
        new("personal_information.mobile", Questionnaire.PersonalInformation, "Do you have a cell phone number?", DebtorsTarget,
            [new("mobile", "Cell phone number", AnswerInputType.Text, Required: true)])
        {
            Help = "Only if it is different from the number above.",
            PerDebtor = true,
        },
        new("personal_information.email", Questionnaire.PersonalInformation, "What email address should we use for you?", DebtorsTarget,
            [new("email", "Email address", AnswerInputType.Text, Required: true)])
        {
            PerDebtor = true,
        },
        new("property.real_estate", Questionnaire.Property, "Do you own a home, land or other real estate?", "assets",
            [
                new("description", "Address or description", AnswerInputType.LongText, Required: true),
                new("value_entire", "What it is worth today", AnswerInputType.Money),
            ])
        {
            Help = "Add each property you own or have a share in.",
            Repeats = true,
            Fixed = new JsonObject { ["category"] = "real_property" },
        },
        new("property.vehicle", Questionnaire.Property, "Do you own a car, truck, motorcycle or other vehicle?", "assets",
            [
                new("year", "Year", AnswerInputType.WholeNumber),
                new("make", "Make", AnswerInputType.Text, Required: true),
                new("model", "Model", AnswerInputType.Text),
                new("mileage", "Approximate mileage", AnswerInputType.WholeNumber),
                new("value_entire", "What it is worth today", AnswerInputType.Money),
            ])
        {
            Help = "Add each vehicle, including ones you are still paying for.",
            Repeats = true,
            Fixed = new JsonObject { ["category"] = "vehicle" },
        },
        new("property.bank_account", Questionnaire.Property, "Do you have checking, savings or other bank accounts?", "assets",
            [
                new("description", "Bank and type of account", AnswerInputType.Text, Required: true, Help: "For example: First Example Bank, checking"),
                new("value_entire", "Balance today", AnswerInputType.Money),
            ])
        {
            Help = "Add each account, with the bank's name and today's balance.",
            Repeats = true,
            Fixed = new JsonObject { ["category"] = "deposits_of_money" },
        },
        new("property.retirement_account", Questionnaire.Property, "Do you have a 401(k), IRA, pension or other retirement account?", "assets",
            [
                new("description", "Type of account and who holds it", AnswerInputType.Text, Required: true),
                new("value_entire", "Balance today", AnswerInputType.Money),
            ])
        {
            Repeats = true,
            Fixed = new JsonObject { ["category"] = "retirement_accounts" },
        },
        new("debts.creditor", Questionnaire.Debts, "Who do you owe money to?", "creditors",
            [
                new("name", "Name of the person or company", AnswerInputType.Text, Required: true),
                Address("address", "Their address"),
            ])
        {
            Help = "Add each person or company you owe, including debts you dispute. Use the name and address on their latest statement or letter.",
            Repeats = true,
        },
        new("income.employment", Questionnaire.Income, "Where do you work?", "employments",
            [
                new("employer_name", "Employer", AnswerInputType.Text, Required: true),
                new("occupation", "Your job title", AnswerInputType.Text),
                Address("employer_address", "Employer's address"),
                new("employed_since", "When you started", AnswerInputType.Date),
            ])
        {
            Help = "Add each job you have now.",
            Repeats = true,
            PerDebtor = true,
            Fixed = new JsonObject { ["status"] = "employed" },
        },
        Expense("expenses.housing", "How much do you pay each month for rent or your mortgage?", "rent_or_home_ownership"),
        Expense("expenses.utilities", "How much do you pay each month for electricity, heat and gas?", "electricity_heat_gas"),
        Expense("expenses.food", "How much does your household spend each month on food and household supplies?", "food_and_housekeeping"),
        Expense("expenses.transportation", "How much do you spend each month on gas, transit and car upkeep?", "transportation") with
        {
            Help = "Not your car payment or insurance.",
        },
        new("other.lease", Questionnaire.Other, "Are you part of a lease or contract that is still in effect?", "contract_leases",
            [
                new("counterparty_name", "Who the lease or contract is with", AnswerInputType.Text, Required: true),
                new("description", "What it is for", AnswerInputType.LongText),
            ])
        {
            Help = "For example: an apartment lease, a car lease, or a phone contract.",
            Repeats = true,
        },
    ];

    /// <summary>Every question id, in catalogue order.</summary>
    public static readonly IReadOnlyList<string> QuestionIds = [.. Catalogue.Select(question => question.Id)];

    private static readonly Dictionary<string, Question> ById = CheckedCatalogue();

    /// <summary>The catalogue entry, retired ones included — the review queue labels every answer ever given.</summary>
    public static Question? Find(string questionId) => ById.GetValueOrDefault(questionId);

    /// <summary>The live questions of one section, in order.</summary>
    public static IReadOnlyList<Question> For(string sectionId) =>
        [.. Catalogue.Where(question => question.Section == sectionId && !question.Retired)];

    /// <summary>
    /// Validate an answer's <paramref name="value"/> and return the candidate
    /// payload. Keys other than the question's inputs are ignored, so a client
    /// cannot reach a field the question does not ask about.
    /// <paramref name="elementId"/> is an append answer's element id — minted on
    /// the first write, kept on an edit.
    /// </summary>
    public static JsonObject BuildPayload(Question entry, JsonNode? value, string? elementId = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (value is not JsonObject given)
        {
            throw new FieldValidationException(new Dictionary<string, string> { ["value"] = "Must be an object." });
        }

        var inputs = new JsonObject();
        foreach (var input in entry.Inputs)
        {
            if (given[input.Key] is { } raw)
            {
                inputs[input.Key] = RawInput(input, raw);
            }
        }

        JsonObject rawPayload;
        if (entry.Placement == AnswerPlacement.Nest && entry.DebtorField is not null)
        {
            rawPayload = new JsonObject { [entry.DebtorField] = inputs };
        }
        else if (entry.Placement == AnswerPlacement.Append && entry.DebtorField is not null)
        {
            var element = new JsonObject { ["id"] = elementId ?? Guid.NewGuid().ToString() };
            foreach (var (key, member) in inputs)
            {
                element[key] = member?.DeepClone();
            }

            rawPayload = new JsonObject { [entry.DebtorField] = new JsonArray(element) };
        }
        else
        {
            rawPayload = (JsonObject)inputs.DeepClone();
            foreach (var (key, member) in entry.Fixed)
            {
                rawPayload[key] = member?.DeepClone();
            }
        }

        JsonObject body;
        try
        {
            body = ValidateTarget(entry, rawPayload);
        }
        catch (FieldValidationException error)
        {
            throw new FieldValidationException(error.Fields.ToDictionary(pair => ErrorKey(entry, pair.Key), pair => pair.Value, StringComparer.Ordinal));
        }

        var payload = new JsonObject();
        if (entry.Placement is AnswerPlacement.Nest or AnswerPlacement.Append && entry.DebtorField is not null)
        {
            if (body[entry.DebtorField!] is { } member)
            {
                payload[entry.DebtorField!] = member.DeepClone();
            }
        }
        else
        {
            foreach (var key in entry.InputKeys.Concat(entry.Fixed.Select(pair => pair.Key)))
            {
                if (body[key] is { } member && !payload.ContainsKey(key))
                {
                    payload[key] = member.DeepClone();
                }
            }
        }

        var answered = AnswerValue(entry, payload);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var input in entry.Inputs)
        {
            if (input.Required && answered[input.Key] is null)
            {
                errors[$"value.{input.Key}"] = "This is required.";
            }
        }

        if (answered.Count == 0 && errors.Count == 0)
        {
            errors["value"] = "Answer at least one part of the question.";
        }

        if (errors.Count > 0)
        {
            throw new FieldValidationException(errors);
        }

        return payload;
    }

    /// <summary>
    /// The inverse of <see cref="BuildPayload"/>: the client's own value. Fixed
    /// members and an appended element's id are the server's.
    /// </summary>
    public static JsonObject AnswerValue(Question entry, JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(payload);
        JsonObject source;
        if (entry.Placement == AnswerPlacement.Nest && entry.DebtorField is not null)
        {
            source = payload[entry.DebtorField] as JsonObject ?? [];
        }
        else if (entry.Placement == AnswerPlacement.Append && entry.DebtorField is not null)
        {
            source = payload[entry.DebtorField] is JsonArray { Count: > 0 } elements && elements[0] is JsonObject first ? first : [];
        }
        else
        {
            source = payload;
        }

        var value = new JsonObject();
        foreach (var key in entry.InputKeys)
        {
            if (source.TryGetPropertyValue(key, out var member))
            {
                value[key] = member?.DeepClone();
            }
        }

        return value;
    }

    /// <summary>An append answer's list-element id, or null.</summary>
    public static string? ElementIdOf(Question entry, JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(payload);
        if (entry.Placement != AnswerPlacement.Append || entry.DebtorField is null)
        {
            return null;
        }

        return payload[entry.DebtorField] is JsonArray { Count: > 0 } elements && elements[0] is JsonObject first
            ? Json.AsString(first["id"])
            : null;
    }

    /// <summary>
    /// Validate <c>POST /v1/portal/answers</c>: <c>{questionId, filingRole?, value}</c>.
    /// <paramref name="sections"/> is the firm's resolved config (a switched-off
    /// section's questions are not answerable); <paramref name="roles"/> the binding's.
    /// </summary>
    public static AnswerRequest ParseAnswer(JsonObject body, IReadOnlyList<ResolvedSection> sections, IReadOnlyList<ClientRole> roles)
    {
        ArgumentNullException.ThrowIfNull(body);
        var entry = EnabledQuestion(body["questionId"], sections);
        var role = FilingRoleOf(body["filingRole"], roles);
        return new AnswerRequest(entry, role, BuildPayload(entry, body["value"]));
    }

    /// <summary>The candidate's locator for an answer — a question kind, snake_case members.</summary>
    public static JsonObject AnswerLocator(Question entry, ClientRole filingRole)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new JsonObject
        {
            ["kind"] = LocatorKind,
            ["section_id"] = entry.Section,
            ["question_id"] = entry.Id,
            ["filing_role"] = filingRole.ToWire(),
        };
    }

    /// <summary>The question a client candidate answers, or null for any other candidate.</summary>
    public static AnswerRef? RefOf(Candidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.Origin.Channel != Channel || candidate.Locator is not { } locator || Json.AsString(locator["kind"]) != LocatorKind)
        {
            return null;
        }

        var questionId = Json.AsString(locator["question_id"]);
        var filingRole = Json.AsString(locator["filing_role"]);
        var entry = questionId is null ? null : Find(questionId);
        return entry is null || filingRole is null ? null : new AnswerRef(entry, filingRole);
    }

    /// <summary>A candidate this client wrote — everything else in the queue is invisible to them.</summary>
    public static bool IsOwnAnswer(Candidate candidate, string subject)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return candidate.Origin.Channel == Channel && candidate.Origin.Subject == subject;
    }

    /// <summary>
    /// The pending candidate for a new answer, or a 409: a non-repeating
    /// question already has a pending answer from this client for this role
    /// (change it instead), or the client has <see cref="MaxPendingAnswers"/> waiting.
    /// </summary>
    public static Candidate CreateAnswer(AnswerRequest request, string caseId, CandidateOrigin origin, IReadOnlyList<Candidate> existing, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(existing);
        if (origin.Channel != Channel)
        {
            throw new ArgumentException("an answer's origin is the client channel", nameof(origin));
        }

        var pending = existing.Where(candidate => candidate.Status == CandidateStatus.Pending && IsOwnAnswer(candidate, origin.Subject)).ToList();
        if (pending.Count >= MaxPendingAnswers)
        {
            throw new ConflictException("you have too many answers waiting for review — your firm will review them before you can add more");
        }

        if (!request.Question.Repeats)
        {
            foreach (var candidate in pending)
            {
                if (RefOf(candidate) is { } found && found.Question.Id == request.Question.Id && found.FilingRole == request.FilingRole.ToWire())
                {
                    throw new ConflictException("this question already has an answer waiting for review — change that answer instead");
                }
            }
        }

        return CandidateRules.Create(
            new ProposalDraft(request.Question.Target, request.Payload, null, null),
            caseId,
            origin,
            locator: AnswerLocator(request.Question, request.FilingRole),
            clock: clock);
    }

    /// <summary>
    /// The client's own answer in an enabled section, or a 404 — never a 403: a
    /// client sees no candidate but their own, so "exists but isn't yours"
    /// would be an oracle over the queue.
    /// </summary>
    public static (Candidate Candidate, AnswerRef Ref) OwnAnswerOr404(Candidate? candidate, string subject, IReadOnlyList<ResolvedSection> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);
        if (candidate is null || !IsOwnAnswer(candidate, subject))
        {
            throw new NotFoundException("answer not found");
        }

        var found = RefOf(candidate);
        var enabled = sections.Where(resolved => resolved.Enabled).Select(resolved => resolved.Section.Id).ToHashSet(StringComparer.Ordinal);
        if (found is null || !enabled.Contains(found.Question.Section))
        {
            throw new NotFoundException("answer not found");
        }

        return (candidate, found);
    }

    /// <summary>
    /// The edited copy of a PENDING answer. Once staff have acted on it, a 409 —
    /// a change is then a new answer. An appended element keeps its id.
    /// </summary>
    public static Candidate ReviseAnswer(Candidate candidate, AnswerRef answer, JsonNode? value, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(answer);
        if (candidate.Status != CandidateStatus.Pending)
        {
            throw new ConflictException($"this answer has already been reviewed (status: {candidate.Status.ToWire()})");
        }

        var payload = BuildPayload(answer.Question, value, ElementIdOf(answer.Question, candidate.Payload));
        return candidate with { Payload = payload, UpdatedAt = Clock.Now(clock) };
    }

    private static AnswerInput Address(string key, string label, string? help = null) => new(key, label, AnswerInputType.Address, Help: help);

    private static Question Expense(string id, string text, string category) =>
        new(id, Questionnaire.Expenses, text, "expenses", [new("amount", "Monthly amount", AnswerInputType.Money, Required: true)])
        {
            Fixed = new JsonObject { ["category"] = category },
        };

    private static JsonNode RawInput(AnswerInput input, JsonNode raw)
    {
        if (input.Type == AnswerInputType.Address && raw is JsonObject address)
        {
            var narrowed = new JsonObject();
            foreach (var member in AddressMembers)
            {
                if (address.TryGetPropertyValue(member, out var memberValue))
                {
                    narrowed[member] = memberValue?.DeepClone();
                }
            }

            return narrowed;
        }

        return raw.DeepClone();
    }

    /// <summary>The payload through the TARGET'S OWN PARSER, and back as the canonical pruned body. Provenance is not enforced.</summary>
    private static JsonObject ValidateTarget(Question entry, JsonObject payload)
    {
        if (entry.Target == DebtorsTarget)
        {
            return BodyJson.Prune(DebtorRules.BodyNode(DebtorRules.Parse(payload, enforceProvenance: false).Body));
        }

        return BodyJson.Prune(CaseCollections.ByCollection[entry.Target].ParseDraft(payload, enforceProvenance: false).BodyNode());
    }

    private static string ErrorKey(Question entry, string path)
    {
        if (entry.Placement == AnswerPlacement.Nest && entry.DebtorField is not null)
        {
            var prefix = $"{entry.DebtorField}.";
            path = path.StartsWith(prefix, StringComparison.Ordinal) ? path[prefix.Length..] : path;
        }
        else if (entry.Placement == AnswerPlacement.Append && entry.DebtorField is not null)
        {
            var prefix = $"{entry.DebtorField}[";
            var close = path.IndexOf("].", StringComparison.Ordinal);
            if (path.StartsWith(prefix, StringComparison.Ordinal) && close >= 0)
            {
                path = path[(close + 2)..];
            }
        }

        return $"value.{path}";
    }

    private static Question EnabledQuestion(JsonNode? questionId, IReadOnlyList<ResolvedSection> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);
        var enabled = sections.Where(resolved => resolved.Enabled).Select(resolved => resolved.Section.Id).ToHashSet(StringComparer.Ordinal);
        var id = Json.AsString(questionId);
        var entry = id is null ? null : Find(id);
        if (entry is null || entry.Retired || !enabled.Contains(entry.Section))
        {
            throw new FieldValidationException(new Dictionary<string, string> { ["questionId"] = "Not a question in your questionnaire." });
        }

        return entry;
    }

    /// <summary>
    /// Which debtor an answer is for: a client holding one role answers for it,
    /// named or not; a client holding both must say which.
    /// </summary>
    private static ClientRole FilingRoleOf(JsonNode? value, IReadOnlyList<ClientRole> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);
        if (value is null)
        {
            return roles.Count == 1
                ? roles[0]
                : throw new FieldValidationException(new Dictionary<string, string> { ["filingRole"] = "Say which of you this answer is for." });
        }

        if (!Wire.TryParse<ClientRole>(Json.AsString(value), out var role))
        {
            throw new FieldValidationException(new Dictionary<string, string>
            {
                ["filingRole"] = "Must be one of " + string.Join(", ", Wire.Names<ClientRole>()) + ".",
            });
        }

        return roles.Contains(role)
            ? role
            : throw new FieldValidationException(new Dictionary<string, string> { ["filingRole"] = "You can only answer for yourself on this case." });
    }

    /// <summary>
    /// The catalogue's own consistency, checked once on load: a typo'd
    /// section, target or placement would otherwise surface as a 500 the first
    /// time a client answered that question.
    /// </summary>
    private static Dictionary<string, Question> CheckedCatalogue()
    {
        var seen = new Dictionary<string, Question>(StringComparer.Ordinal);
        foreach (var entry in Catalogue)
        {
            if (!seen.TryAdd(entry.Id, entry))
            {
                throw new ValidationException($"duplicate question id '{entry.Id}'");
            }

            if (!Questionnaire.SectionIds.Contains(entry.Section))
            {
                throw new ValidationException($"{entry.Id}: unknown section '{entry.Section}'");
            }

            if (!entry.Id.StartsWith($"{entry.Section}.", StringComparison.Ordinal))
            {
                throw new ValidationException($"{entry.Id}: id must start with its section");
            }

            if (entry.Target != DebtorsTarget && CaseCollections.Find(entry.Target) is null)
            {
                throw new ValidationException($"{entry.Id}: unknown target '{entry.Target}'");
            }

            if (entry.Placement != AnswerPlacement.Merge && (entry.Target != DebtorsTarget || string.IsNullOrEmpty(entry.DebtorField)))
            {
                throw new ValidationException($"{entry.Id}: nest/append is for debtor fields");
            }
        }

        return seen;
    }
}

/// <summary>What kind of box an answer input is (Python's <c>INPUT_TYPES</c>).</summary>
public enum AnswerInputType
{
    /// <summary>A single line.</summary>
    [JsonStringEnumMemberName("text")]
    Text,

    /// <summary>A multi-line box.</summary>
    [JsonStringEnumMemberName("long_text")]
    LongText,

    /// <summary>A calendar date.</summary>
    [JsonStringEnumMemberName("date")]
    Date,

    /// <summary>A dollar amount.</summary>
    [JsonStringEnumMemberName("money")]
    Money,

    /// <summary>A whole number.</summary>
    [JsonStringEnumMemberName("whole_number")]
    WholeNumber,

    /// <summary>A structured address.</summary>
    [JsonStringEnumMemberName("address")]
    Address,
}

/// <summary>Where an answer lands in its target body (Python's <c>MERGE</c>/<c>NEST</c>/<c>APPEND</c>).</summary>
public enum AnswerPlacement
{
    /// <summary>The inputs are the body's own fields.</summary>
    [JsonStringEnumMemberName("merge")]
    Merge,

    /// <summary>The inputs are the members of one debtor field (the name).</summary>
    [JsonStringEnumMemberName("nest")]
    Nest,

    /// <summary>The inputs are one new element of a debtor list (an alias).</summary>
    [JsonStringEnumMemberName("append")]
    Append,
}
