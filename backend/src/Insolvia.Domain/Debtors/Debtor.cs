using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Insolvia.Core.CaseEntities;
using Insolvia.Core.Fields;
using Insolvia.Core.Provenance;
using Insolvia.Core.TaxIds;

namespace Insolvia.Core.Debtors;

// The debtor record — B101 Part 1, and the root the rest of a case hangs off
// (from debtors.py). A joint filing is TWO debtor records under one case, not
// one record with spouse-suffixed columns; a non-filing spouse is a filing
// role, not a flag. PROGRESSIVE BY CONSTRUCTION: nothing is required, only
// well-formed if present.

/// <summary>B101 line 6.</summary>
public sealed record Venue(VenueBasis? Basis = null, string? Explanation = null);

/// <summary>B101 line 15.</summary>
public sealed record CreditCounseling(CounselingStatus? Status = null, CounselingExemption? ExemptionReason = null);

/// <summary>
/// A debtor's case data — what provenance paths address and what is stored
/// (Python's <c>debtor_body</c>). Identity, provenance, the tax id and the
/// client link are NOT here: the server set them, nobody sent them.
/// </summary>
public sealed record DebtorBody(
    PersonName Name,
    ValueCollection<OtherName> OtherNamesUsed,
    ValueCollection<string> EmployerIds,
    Address ResidenceAddress,
    Address MailingAddress,
    string? Phone,
    string? Mobile,
    string? Email,
    Venue Venue,
    CreditCounseling CreditCounseling,
    DateOnly? SignedAt)
{
    /// <summary>The empty body — a debtor with nothing filled in yet.</summary>
    public static DebtorBody Empty { get; } = new(new PersonName(), [], [], new Address(), new Address(), null, null, null, new Venue(), new CreditCounseling(), null);
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
/// A validated debtor body before identity. <see cref="TaxId"/> is the parsed
/// WRITE (the digits being entered, or the last four being kept) — the digits
/// exist only here, between the parse and <see cref="TaxIdRules.StoreAsync"/>.
/// </summary>
public sealed record DebtorDraft(DebtorBody Body, TaxIdInput? TaxId, ValueDictionary<string, ProvenanceEntry> Provenance);

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

    /// <summary>A whole-record filing-role value from a path segment or body.</summary>
    public static FilingRole ParseFilingRole(JsonNode? value)
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var role = FieldParsers.Choice<FilingRole>(value, "filing_role", errors);
        if (role is null)
        {
            throw new FieldValidationException(errors.Count > 0
                ? errors
                : new Dictionary<string, string> { ["filing_role"] = "Must be one of " + string.Join(", ", Wire.Names<FilingRole>()) + "." });
        }

        return role.Value;
    }

    /// <summary>
    /// Validate a WHOLE debtor body; unknown keys are ignored. The tax id is
    /// parsed for shape only. INVARIANT 1 runs against the body as it will be
    /// stored, and the tax id is ONE field for provenance (<c>tax_id</c>, never
    /// its members). <paramref name="enforceProvenance"/> is a write rule,
    /// switched off on reads.
    /// </summary>
    public static DebtorDraft Parse(JsonObject payload, bool enforceProvenance = true)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);

        var taxId = TaxIdRules.Parse(payload["tax_id"], "tax_id", errors);
        var name = FieldParsers.ParseName(payload["name"], "name", errors);
        var otherNames = OtherNames.Parse(payload["other_names_used"], errors);
        var employerIds = ParseEmployerIds(payload["employer_ids"], errors);
        var residence = FieldParsers.ParseAddress(payload["residence_address"], "residence_address", errors);
        var mailing = FieldParsers.ParseAddress(payload["mailing_address"], "mailing_address", errors);
        var phone = FieldParsers.Text(payload["phone"], "phone", errors, limit: 40);
        var mobile = FieldParsers.Text(payload["mobile"], "mobile", errors, limit: 40);
        var email = FieldParsers.Text(payload["email"], "email", errors);
        var venueRaw = FieldParsers.Mapping(payload["venue"], "venue", errors);
        var venue = new Venue(
            FieldParsers.Choice<VenueBasis>(venueRaw["basis"], "venue.basis", errors),
            FieldParsers.Text(venueRaw["explanation"], "venue.explanation", errors, limit: 1000));
        var counselingRaw = FieldParsers.Mapping(payload["credit_counseling"], "credit_counseling", errors);
        var counseling = new CreditCounseling(
            FieldParsers.Choice<CounselingStatus>(counselingRaw["status"], "credit_counseling.status", errors),
            FieldParsers.Choice<CounselingExemption>(counselingRaw["exemption_reason"], "credit_counseling.exemption_reason", errors));
        var signedAt = FieldParsers.FormDate(payload["signed_at"], "signed_at", errors);
        IdRows.ThrowIfAny(errors);

        // Provenance AFTER the body: the fixable thing is reported first.
        var provenance = ProvenanceRules.Parse(payload["provenance"]);
        var body = new DebtorBody(name, otherNames, employerIds, residence, mailing, phone, mobile, email, venue, counseling, signedAt);
        if (enforceProvenance)
        {
            var checkedBody = BodyNode(body);
            if (taxId is not null)
            {
                checkedBody["tax_id"] = "present";
            }

            ProvenanceRules.RequireProvenance(checkedBody, provenance);
        }

        return new DebtorDraft(body, taxId, provenance);
    }

    /// <summary>The body as plain nested JSON (Python's <c>debtor_body</c>).</summary>
    public static JsonObject BodyNode(DebtorBody body) => BodyJson.ToNode(body);

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
            throw new ArgumentException("client_id and case_created_at are set together");
        }

        var now = Clock.Now(clock);
        return new Debtor(Guid.NewGuid().ToString(), caseId, filingRole, now, now, draft.Body)
        {
            TaxId = taxId,
            Provenance = draft.Provenance,
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
            Provenance = draft.Provenance,
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

    private static ValueCollection<string> ParseEmployerIds(JsonNode? value, Dictionary<string, string> errors)
    {
        if (value is null)
        {
            return [];
        }

        if (value is not JsonArray list)
        {
            errors["employer_ids"] = "Must be a list.";
            return [];
        }

        var ids = new List<string>();
        for (var index = 0; index < list.Count; index++)
        {
            if (FieldParsers.Text(list[index], $"employer_ids[{index}]", errors, limit: 20) is { } text)
            {
                ids.Add(text);
            }
        }

        return ids.ToValueCollection();
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
