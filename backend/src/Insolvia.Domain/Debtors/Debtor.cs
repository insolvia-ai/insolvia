using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Insolvia.Domain.CaseEntities;
using Insolvia.Domain.Fields;
using Insolvia.Domain.Provenance;
using Insolvia.Domain.TaxIds;

namespace Insolvia.Domain.Debtors;

// The debtor record — B101 Part 1, and the root the rest of a case hangs off
// (from debtors.py). A joint filing is TWO debtor records under one case, not
// one record with spouse-suffixed columns; a non-filing spouse is a filing
// role, not a flag. PROGRESSIVE BY CONSTRUCTION: nothing is required, only
// well-formed if present.

/// <summary>B101 line 6.</summary>
public sealed record Venue(VenueBasis? Basis = null, [property: Text(1000)] string? Explanation = null);

/// <summary>B101 line 15.</summary>
public sealed record CreditCounseling(CounselingStatus? Status = null, CounselingExemption? ExemptionReason = null);

/// <summary>
/// A debtor's case data — what provenance paths address and what is stored,
/// camelCase with its unanswered members left out (ADR 0032). Identity,
/// provenance, the tax id and the client link are NOT here: the server set
/// them, nobody sent them.
/// </summary>
public sealed record DebtorBody(
    PersonName? Name = null,
    ValueCollection<OtherName>? OtherNamesUsed = null,
    ValueCollection<string>? EmployerIds = null,
    Address? ResidenceAddress = null,
    Address? MailingAddress = null,
    [property: Text(40)] string? Phone = null,
    [property: Text(40)] string? Mobile = null,
    [property: Text] string? Email = null,
    Venue? Venue = null,
    CreditCounseling? CreditCounseling = null,
    DateOnly? SignedAt = null)
{
    /// <summary>The empty body — a debtor with nothing filled in yet.</summary>
    public static DebtorBody Empty { get; } = new();

    /// <summary>The legal name.</summary>
    public PersonName Name { get; init; } = Name ?? new();

    /// <summary>The 8-year aliases, each addressed by its id.</summary>
    [Rows]
    public ValueCollection<OtherName> OtherNamesUsed { get; init; } = OtherNamesUsed ?? [];

    /// <summary>Employer identification numbers.</summary>
    [Text(20)]
    public ValueCollection<string> EmployerIds { get; init; } = EmployerIds ?? [];

    /// <summary>Where the debtor lives (B101 line 5).</summary>
    public Address ResidenceAddress { get; init; } = ResidenceAddress ?? new();

    /// <summary>Where mail goes, when not home.</summary>
    public Address MailingAddress { get; init; } = MailingAddress ?? new();

    /// <summary>B101 line 6.</summary>
    public Venue Venue { get; init; } = Venue ?? new();

    /// <summary>B101 line 15.</summary>
    public CreditCounseling CreditCounseling { get; init; } = CreditCounseling ?? new();
}

/// <summary>
/// A stored debtor. <see cref="ClientId"/> is the firm client it was copied from
/// (ADR 0022) — SERVER-OWNED, never read from a body; with it,
/// <see cref="CaseCreatedAt"/> (the by-client index's sort). <see cref="TaxId"/>
/// is the reference to the sealed number, never the number.
/// </summary>
/// <param name="Id">The server-minted uuid, stable across saves.</param>
/// <param name="CaseId">The case.</param>
/// <param name="FilingRole">Which debtor — one record per role per case.</param>
/// <param name="CreatedAt">When it was first saved.</param>
/// <param name="UpdatedAt">When it was last saved.</param>
/// <param name="Body">The case data.</param>
public sealed record Debtor(string Id, string CaseId, FilingRole FilingRole, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DebtorBody Body)
{
    /// <summary>The reference to the sealed tax id.</summary>
    public TaxIdRef? TaxId { get; init; }

    /// <summary>Where each populated field came from.</summary>
    public ValueDictionary<string, ProvenanceEntry> Provenance { get; init; } = ValueCollections.EmptyDictionary<string, ProvenanceEntry>();

    /// <summary>The firm client this debtor was copied from.</summary>
    public string? ClientId { get; init; }

    /// <summary>The CASE's creation, set with <see cref="ClientId"/> and only with it.</summary>
    public DateTimeOffset? CaseCreatedAt { get; init; }
}

/// <summary>
/// A debtor as written (<c>PUT …/debtors/{filingRole}</c>): the body, the
/// tax id being entered or kept, and where each populated field came from.
/// <see cref="TaxId"/> holds the digits only between the request and
/// <see cref="TaxIdRules.StoreAsync"/>.
/// </summary>
/// <param name="Body">The case data; absent is empty.</param>
/// <param name="TaxId">The number being entered (<c>value</c>), or the last four being kept (<c>lastFour</c>); absent removes it.</param>
/// <param name="Provenance">Where each populated field came from, by body path; the tax id is the one path <c>taxId</c>.</param>
public sealed record DebtorDraft(
    DebtorBody? Body = null,
    TaxIdInput? TaxId = null,
    IReadOnlyDictionary<string, ProvenanceEntry>? Provenance = null)
{
    /// <summary>The case data.</summary>
    public DebtorBody Body { get; init; } = Body ?? DebtorBody.Empty;

    /// <summary>Where each populated field came from.</summary>
    public IReadOnlyDictionary<string, ProvenanceEntry> Provenance { get; init; } = Provenance ?? ValueCollections.EmptyDictionary<string, ProvenanceEntry>();
}

/// <summary>What <c>IDebtorStore.LinkAsync</c> answers (Python's <c>LinkOutcome</c>).</summary>
public enum LinkOutcome
{
    /// <summary>The link was written.</summary>
    [JsonStringEnumMemberName("written")]
    Written,

    /// <summary>The create lost its race: the role is already filled.</summary>
    [JsonStringEnumMemberName("role_taken")]
    RoleTaken,

    /// <summary>Another role of the case holds this client.</summary>
    [JsonStringEnumMemberName("client_taken")]
    ClientTaken,

    /// <summary>The client row refused: archived, merged, or being merged away.</summary>
    [JsonStringEnumMemberName("client_unavailable")]
    ClientUnavailable,
}

/// <summary>The debtor rules (Python's module-level functions in <c>debtors.py</c>).</summary>
public static class DebtorRules
{
    /// <summary>Every filing role, in the order the forms print debtors.</summary>
    public static IReadOnlyList<FilingRole> FilingRoles => Wire.Members<FilingRole>();

    /// <summary>A filing role from a path segment.</summary>
    public static FilingRole ParseFilingRole(string value) =>
        Wire.TryParse<FilingRole>(value, out var role)
            ? role
            : throw new FieldValidationException(new Dictionary<string, string> { ["filingRole"] = "Must be one of " + string.Join(", ", Wire.Names<FilingRole>()) + "." });

    /// <summary>A body that arrived as JSON (a seed, a candidate, an answer), read and checked as a request's would be.</summary>
    public static DebtorBody ReadBody(JsonNode? node) => (DebtorBody)BodyJson.Read(node, typeof(DebtorBody));

    /// <summary>The body as stored and as provenance paths address it.</summary>
    public static JsonObject BodyNode(DebtorBody body) => BodyJson.ToNode(body);

    /// <summary>
    /// The draft a write may store: its provenance well formed, and an entry
    /// for every populated field (invariant 1) — the tax id is ONE field,
    /// <c>taxId</c>, never its members.
    /// </summary>
    public static DebtorDraft Checked(DebtorDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var provenance = ProvenanceRules.Check(draft.Provenance);
        var body = BodyNode(draft.Body);
        if (draft.TaxId is not null)
        {
            body["taxId"] = "present";
        }

        ProvenanceRules.RequireProvenance(body, provenance);
        return draft with { Provenance = provenance };
    }

    /// <summary>
    /// A questionnaire save may KEEP <c>client</c> (and <c>client_answered</c>)
    /// provenance, never mint it — the autosave echoing a field the preparer
    /// did not touch. Anything else is refused, not silently downgraded.
    /// </summary>
    public static void RequireClientProvenanceKept(DebtorDraft draft, Debtor? stored)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ProvenanceRules.RequireServerSourcesKept(
            BodyNode(draft.Body),
            draft.Provenance,
            stored is null ? null : BodyNode(stored.Body),
            stored?.Provenance);
    }

    /// <summary>
    /// A new debtor. <paramref name="clientId"/> and
    /// <paramref name="caseCreatedAt"/> travel together — one without the other
    /// is refused rather than half-indexed.
    /// </summary>
    public static Debtor Create(
        DebtorDraft draft,
        string caseId,
        FilingRole filingRole,
        TaxIdRef? taxId = null,
        string? clientId = null,
        DateTimeOffset? caseCreatedAt = null,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if ((clientId is null) != (caseCreatedAt is null))
        {
            throw new ArgumentException("clientId and caseCreatedAt are set together");
        }

        var now = Clock.Now(clock);
        return new Debtor(Guid.NewGuid().ToString(), caseId, filingRole, now, now, draft.Body)
        {
            TaxId = taxId,
            Provenance = draft.Provenance.ToValueDictionary(),
            ClientId = clientId,
            CaseCreatedAt = caseCreatedAt,
        };
    }

    /// <summary>
    /// The draft's body over <paramref name="existing"/>, keeping its id and
    /// <c>created_at</c>. <paramref name="taxId"/> is what the debtor carries
    /// AFTER this save (a draft with no tax id clears it). The client link is
    /// KEPT — a questionnaire save cannot link, re-link or unlink.
    /// </summary>
    public static Debtor Replace(Debtor existing, DebtorDraft draft, TaxIdRef? taxId = null, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(draft);
        return existing with
        {
            UpdatedAt = Clock.Now(clock),
            Body = draft.Body,
            TaxId = taxId,
            Provenance = draft.Provenance.ToValueDictionary(),
        };
    }

    /// <summary>
    /// <paramref name="existing"/> pointed at another firm client, its copied
    /// fields untouched — re-linking changes which client the debtor is, never
    /// what the case says about them.
    /// </summary>
    public static Debtor LinkClient(Debtor existing, string clientId, DateTimeOffset caseCreatedAt, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(existing);
        return existing with { ClientId = clientId, CaseCreatedAt = caseCreatedAt, UpdatedAt = Clock.Now(clock) };
    }

    /// <summary>
    /// Position in the filing roles — the order the forms print debtors in,
    /// and the ONE definition both stores sort by.
    /// </summary>
    public static int RoleOrder(FilingRole filingRole)
    {
        var index = FilingRoles.ToList().IndexOf(filingRole);
        return index < 0 ? FilingRoles.Count : index;
    }
}

/// <summary>B101 line 6: why this district (Python's <c>VENUE_BASES</c>).</summary>
public enum VenueBasis
{
    /// <summary>The wire value <c>lived_longest_180_days</c>.</summary>
    [JsonStringEnumMemberName("lived_longest_180_days")]
    LivedLongest180Days,

    /// <summary>The wire value <c>other</c>.</summary>
    [JsonStringEnumMemberName("other")]
    Other,
}

/// <summary>B101 line 15's four credit-counseling checkboxes, named rather than numbered (Python's <c>COUNSELING_STATUSES</c>).</summary>
public enum CounselingStatus
{
    /// <summary>The wire value <c>completed_with_certificate</c>.</summary>
    [JsonStringEnumMemberName("completed_with_certificate")]
    CompletedWithCertificate,

    /// <summary>The wire value <c>completed_certificate_pending</c>.</summary>
    [JsonStringEnumMemberName("completed_certificate_pending")]
    CompletedCertificatePending,

    /// <summary>The wire value <c>exigent_circumstances_waiver_requested</c>.</summary>
    [JsonStringEnumMemberName("exigent_circumstances_waiver_requested")]
    ExigentCircumstancesWaiverRequested,

    /// <summary>The wire value <c>not_required</c>.</summary>
    [JsonStringEnumMemberName("not_required")]
    NotRequired,
}

/// <summary>The form's three grounds for <c>not_required</c> (Python's <c>COUNSELING_EXEMPTIONS</c>).</summary>
public enum CounselingExemption
{
    /// <summary>The wire value <c>incapacity</c>.</summary>
    [JsonStringEnumMemberName("incapacity")]
    Incapacity,

    /// <summary>The wire value <c>disability</c>.</summary>
    [JsonStringEnumMemberName("disability")]
    Disability,

    /// <summary>The wire value <c>active_duty</c>.</summary>
    [JsonStringEnumMemberName("active_duty")]
    ActiveDuty,
}
