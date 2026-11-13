using System.ComponentModel.DataAnnotations;
using Insolvia.Domain.Fields;

namespace Insolvia.Domain.Questionnaires;

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

    /// <summary>The config a save writes.</summary>
    public static QuestionnaireConfig BuildConfig(IReadOnlyDictionary<string, SectionSetting> settings, string firmId, string updatedBy, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new QuestionnaireConfig(firmId, settings.ToValueDictionary(), Clock.Now(clock), updatedBy);
    }
}

/// <summary>
/// The firm's questionnaire config as saved (<c>PUT /v1/firm/questionnaire</c>):
/// every catalogue section named exactly once — a whole-record save.
/// </summary>
/// <param name="Sections">One entry for every section.</param>
public sealed record QuestionnaireRequest(
    [property: Required(ErrorMessage = "Must be a list of sections.")] IReadOnlyList<QuestionnaireSectionRequest>? Sections) : IValidatableObject
{
    /// <summary>
    /// The settings a save stores. Instructions sent blank, or identical to
    /// the default, are stored as none, so the default keeps following the catalogue.
    /// </summary>
    /// <returns>Section id to setting, in the order sent.</returns>
    public ValueDictionary<string, SectionSetting> Settings() =>
        Sections!.Select(entry => KeyValuePair.Create(
            entry.Id!,
            new SectionSetting(
                entry.Enabled!.Value,
                string.IsNullOrWhiteSpace(entry.Instructions) || entry.Instructions == Questionnaire.Catalogue.First(section => section.Id == entry.Id).DefaultInstructions ? null : entry.Instructions)))
        .ToValueDictionary();

    /// <summary>Each id a catalogue section's, named once; every section named; an unswitchable section on.</summary>
    /// <param name="validationContext">Unused.</param>
    /// <returns>The failures.</returns>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var named = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < Sections!.Count; index++)
        {
            var entry = Sections[index];
            var section = Questionnaire.Catalogue.FirstOrDefault(candidate => candidate.Id == entry.Id);
            if (section is null)
            {
                yield return new ValidationResult("Must be one of " + string.Join(", ", Questionnaire.SectionIds), [$"sections[{index}].id"]);
            }
            else if (!named.Add(section.Id))
            {
                yield return new ValidationResult("Named more than once.", [$"sections[{index}].id"]);
            }
            else if (entry.Enabled == false && !section.Switchable)
            {
                yield return new ValidationResult($"{section.Title} is always on.", [$"sections[{index}].enabled"]);
            }
        }

        var missing = Questionnaire.SectionIds.Where(id => !named.Contains(id)).ToList();
        if (missing.Count > 0 && named.Count == Sections.Count)
        {
            yield return new ValidationResult("Missing from the save: " + string.Join(", ", missing) + ".", [nameof(Sections)]);
        }
    }
}

/// <summary>The firm's choice for one questionnaire section.</summary>
/// <param name="Id">The section's id.</param>
/// <param name="Enabled">Whether clients see it; an unswitchable section must be true.</param>
/// <param name="Instructions">The firm's instructions; absent, or the default text, means the default.</param>
public sealed record QuestionnaireSectionRequest(
    string? Id,
    [property: Required(ErrorMessage = "Must be true or false.")] bool? Enabled,
    [property: Narrative(Questionnaire.MaxInstructions)] string? Instructions);
