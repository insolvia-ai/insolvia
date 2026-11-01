using System.Text.Json.Nodes;
using Insolvia.Core.Fields;

namespace Insolvia.Core.Questionnaires;

/// <summary>
/// One catalogue entry. <see cref="Covers"/> is for staff;
/// <see cref="DefaultInstructions"/> is what a client reads when the firm has
/// written nothing of its own.
/// </summary>
public sealed record Section(string Id, string Title, string Covers, string DefaultInstructions, bool Switchable);

/// <summary>A firm's choice for one section. Null instructions mean the catalogue's default — not "no instructions".</summary>
public sealed record SectionSetting(bool Enabled, string? Instructions = null);

/// <summary>
/// A firm's stored questionnaire config — it exists only once the firm has
/// saved something; ABSENT MEANS DEFAULTS. <see cref="UpdatedBy"/> is a fact
/// about the write, never an authorization input.
/// </summary>
public sealed record QuestionnaireConfig(
    string FirmId,
    ValueDictionary<string, SectionSetting> Sections,
    DateTimeOffset UpdatedAt,
    string UpdatedBy);

/// <summary>A catalogue section with the firm's config applied — what the staff screen and the portal are built from.</summary>
public sealed record ResolvedSection(Section Section, bool Enabled, string? CustomInstructions)
{
    /// <summary>The instructions in force: the firm's own, else the default.</summary>
    public string Instructions => string.IsNullOrEmpty(CustomInstructions) ? Section.DefaultInstructions : CustomInstructions;
}

/// <summary>
/// The client questionnaire's sections, and which a firm shows its clients
/// (ADR 0023 PR 3 / #362; from <c>questionnaire.py</c>). THE SECTION IDS ARE
/// A CONTRACT — answers hang off them; add a section, never rename one.
/// <c>personal_information</c> is ALWAYS ON. Switching a section off hides it
/// from CLIENTS only; staff see every section.
/// </summary>
public static class Questionnaire
{
    /// <summary>The library creditor's notes limit, for the same kind of firm prose.</summary>
    public const int MaxInstructions = 2000;

    /// <summary>The voluntary petition.</summary>
    public const string PersonalInformation = "personal_information";

    /// <summary>Schedule A/B and C.</summary>
    public const string Property = "property";

    /// <summary>Schedules D, E/F and H.</summary>
    public const string Debts = "debts";

    /// <summary>Schedule I and the means test.</summary>
    public const string Income = "income";

    /// <summary>Schedule J.</summary>
    public const string Expenses = "expenses";

    /// <summary>SOFA, Schedule G, and the rest.</summary>
    public const string Other = "other";

    /// <summary>Every section is ON by default.</summary>
    public static readonly SectionSetting DefaultSetting = new(Enabled: true);

    /// <summary>The catalogue, in the order a client works through it (the petition's and its schedules').</summary>
    public static readonly IReadOnlyList<Section> Catalogue =
    [
        new(
            PersonalInformation,
            "Personal information",
            "The voluntary petition: names, addresses, contact details, household and prior filings.",
            "Tell us who you are and where you live. Use your full legal name as it appears on your identification, and list any other names you have used in the last eight years.",
            Switchable: false),
        new(
            Property,
            "Property",
            "Schedule A/B and the exemptions on Schedule C: real estate, vehicles, accounts, household goods and other assets.",
            "List everything you own or have an interest in, wherever it is: your home, vehicles, bank accounts, retirement accounts, household goods and anything else of value. An estimate of what each is worth today is enough.",
            Switchable: true),
        new(
            Debts,
            "Debts",
            "Schedules D and E/F: secured, priority and unsecured creditors, and the codebtors on Schedule H.",
            "List everyone you owe money to, including debts you dispute and debts someone else also owes. Recent statements or collection letters are the best source for names, addresses and balances.",
            Switchable: true),
        new(
            Income,
            "Income",
            "Schedule I and the means test: employment, wages, benefits and other income for you and your household.",
            "Tell us about your work and every source of income your household receives. Recent pay stubs and benefit letters will help you give exact amounts.",
            Switchable: true),
        new(
            Expenses,
            "Expenses",
            "Schedule J: the household's regular monthly expenses.",
            "Tell us what your household spends in a typical month on housing, utilities, food, transportation, insurance and other regular costs.",
            Switchable: true),
        new(
            Other,
            "Other",
            "The statement of financial affairs, leases and contracts on Schedule G, and anything not asked elsewhere.",
            "Answer a few questions about recent payments, transfers, lawsuits and other events, and tell us about any leases or contracts you are part of.",
            Switchable: true),
    ];

    /// <summary>The catalogue's ids, in order.</summary>
    public static readonly IReadOnlyList<string> SectionIds = [.. Catalogue.Select(section => section.Id)];

    private static readonly Dictionary<string, Section> ById = Catalogue.ToDictionary(section => section.Id, StringComparer.Ordinal);

    /// <summary>
    /// Every catalogue section, in order, with <paramref name="config"/>
    /// applied. A section the config does not name takes its default (a new
    /// section reaches every firm); a stored id the catalogue lacks is
    /// ignored; <c>personal_information</c> is on whatever the row says.
    /// </summary>
    public static IReadOnlyList<ResolvedSection> Resolve(QuestionnaireConfig? config)
    {
        var resolved = new List<ResolvedSection>();
        foreach (var section in Catalogue)
        {
            var setting = config is not null && config.Sections.TryGetValue(section.Id, out var stored) ? stored : DefaultSetting;
            resolved.Add(new ResolvedSection(section, setting.Enabled || !section.Switchable, setting.Instructions));
        }

        return resolved;
    }

    /// <summary>The sections the firm's CLIENTS see — the enabled ones, in order.</summary>
    public static IReadOnlyList<ResolvedSection> ClientSections(QuestionnaireConfig? config) =>
        [.. Resolve(config).Where(section => section.Enabled)];

    /// <summary>
    /// Validate PUT <c>/v1/firm/questionnaire</c>: <c>{"sections": [...]}</c>,
    /// every catalogue section named exactly once — a whole-record save.
    /// Instructions sent blank, or identical to the default, are stored as
    /// none, so the default keeps following the catalogue.
    /// </summary>
    public static ValueDictionary<string, SectionSetting> ParseUpdate(JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload["sections"] is not JsonArray raw)
        {
            throw new FieldValidationException(new Dictionary<string, string> { ["sections"] = "Must be a list of sections." });
        }

        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var settings = new List<KeyValuePair<string, SectionSetting>>();
        var named = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < raw.Count; index++)
        {
            if (raw[index] is not JsonObject entry)
            {
                errors[$"sections.{index}"] = "Must be an object.";
                continue;
            }

            var sectionId = Json.AsString(entry["id"]);
            if (sectionId is null || !ById.TryGetValue(sectionId, out var section))
            {
                // No trailing period, exactly as the contract's message reads.
                errors[$"sections.{index}.id"] = "Must be one of " + string.Join(", ", SectionIds);
                continue;
            }

            if (named.Contains(sectionId))
            {
                errors[$"sections.{sectionId}"] = "Named more than once.";
                continue;
            }

            var path = $"sections.{sectionId}";
            var enabled = FieldParsers.Boolean(entry["enabled"], $"{path}.enabled", errors);
            if (enabled is null && !errors.ContainsKey($"{path}.enabled"))
            {
                errors[$"{path}.enabled"] = "Must be true or false.";
            }

            if (enabled == false && !section.Switchable)
            {
                errors[$"{path}.enabled"] = $"{section.Title} is always on.";
            }

            var instructions = FieldParsers.Narrative(entry["instructions"], $"{path}.instructions", errors, MaxInstructions);
            if (instructions == section.DefaultInstructions)
            {
                instructions = null;
            }

            named.Add(sectionId);
            settings.Add(new(sectionId, new SectionSetting(enabled ?? false, instructions)));
        }

        foreach (var sectionId in SectionIds)
        {
            if (!named.Contains(sectionId) && !errors.ContainsKey($"sections.{sectionId}"))
            {
                errors[$"sections.{sectionId}"] = "Missing from the save.";
            }
        }

        if (errors.Count > 0)
        {
            throw new FieldValidationException(errors);
        }

        return settings.ToValueDictionary();
    }

    /// <summary>The config a save writes.</summary>
    public static QuestionnaireConfig BuildConfig(IReadOnlyDictionary<string, SectionSetting> settings, string firmId, string updatedBy, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new QuestionnaireConfig(firmId, settings.ToValueDictionary(), Clock.Now(clock), updatedBy);
    }
}
