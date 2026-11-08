using System.Text.Json.Serialization;

namespace Insolvia.Core.AccessLog;

/// <summary>
/// What happened (Python's <c>ACTIONS</c>). Kept COARSE — that an access
/// occurred and by whom, never a diff — but never so coarse that the
/// disclosures that matter share a row: a document download is not a case
/// read, and a tax-id read is not either. <c>access_log.py</c>'s comment block
/// owns why each one exists; the wire strings are pinned by a test.
/// </summary>
public enum AccessAction
{
    /// <summary>A case was created.</summary>
    [JsonStringEnumMemberName("case.create")]
    CaseCreate,

    /// <summary>A case was read.</summary>
    [JsonStringEnumMemberName("case.read")]
    CaseRead,

    /// <summary>A case was changed (archive and restore included).</summary>
    [JsonStringEnumMemberName("case.update")]
    CaseUpdate,

    /// <summary>A document upload was started.</summary>
    [JsonStringEnumMemberName("document.create")]
    DocumentCreate,

    /// <summary>A case's document list was read.</summary>
    [JsonStringEnumMemberName("document.read")]
    DocumentRead,

    /// <summary>A document's bytes were handed out.</summary>
    [JsonStringEnumMemberName("document.download")]
    DocumentDownload,

    /// <summary>A document was destroyed.</summary>
    [JsonStringEnumMemberName("document.delete")]
    DocumentDelete,

    /// <summary>A pipeline job that reads the whole file was accepted (ADR 0018).</summary>
    [JsonStringEnumMemberName("job.accept")]
    JobAccept,

    /// <summary>An agent proposed records for review (issue #262).</summary>
    [JsonStringEnumMemberName("candidate.propose")]
    CandidatePropose,

    /// <summary>An agent withdrew a proposal.</summary>
    [JsonStringEnumMemberName("candidate.withdraw")]
    CandidateWithdraw,

    /// <summary>A debtor's tax identifier was decrypted (#382); carries filing role and purpose.</summary>
    [JsonStringEnumMemberName("taxid.read")]
    TaxIdRead,

    /// <summary>A firm user invited a client to the portal; carries roles.</summary>
    [JsonStringEnumMemberName("client.invite")]
    ClientInvite,

    /// <summary>A firm user revoked a client's portal access; carries roles.</summary>
    [JsonStringEnumMemberName("client.revoke")]
    ClientRevoke,

    /// <summary>A client read through the portal.</summary>
    [JsonStringEnumMemberName("portal.read")]
    PortalRead,

    /// <summary>A client answered through the portal.</summary>
    [JsonStringEnumMemberName("portal.answer")]
    PortalAnswer,

    /// <summary>A firm client was created (keyed by the client).</summary>
    [JsonStringEnumMemberName("client.create")]
    ClientCreate,

    /// <summary>A firm client record was read (keyed by the client).</summary>
    [JsonStringEnumMemberName("client.read")]
    ClientRead,

    /// <summary>A firm client was changed or archived (keyed by the client).</summary>
    [JsonStringEnumMemberName("client.update")]
    ClientUpdate,

    /// <summary>Two clients were merged — written once under each client.</summary>
    [JsonStringEnumMemberName("client.merge")]
    ClientMerge,

    /// <summary>A matter was deleted (#355).</summary>
    [JsonStringEnumMemberName("case.delete")]
    CaseDelete,

    /// <summary>An attorney enrolled a CM/ECF credential (keyed by the credential).</summary>
    [JsonStringEnumMemberName("credential.enrol")]
    CredentialEnrol,

    /// <summary>A credential was destroyed.</summary>
    [JsonStringEnumMemberName("credential.revoke")]
    CredentialRevoke,

    /// <summary>A credential was opened for a filing; carries purpose and filing id.</summary>
    [JsonStringEnumMemberName("credential.open")]
    CredentialOpen,

    /// <summary>An attorney signed the filing authorization (keyed by the authorization).</summary>
    [JsonStringEnumMemberName("authorization.sign")]
    AuthorizationSign,

    /// <summary>An attorney withdrew the filing authorization.</summary>
    [JsonStringEnumMemberName("authorization.withdraw")]
    AuthorizationWithdraw,

    /// <summary>A filing was approved; carries the filing id.</summary>
    [JsonStringEnumMemberName("filing.approve")]
    FilingApprove,

    /// <summary>An approval was voided; purpose is the reason.</summary>
    [JsonStringEnumMemberName("filing.void")]
    FilingVoid,

    /// <summary>An approval was consumed by the worker.</summary>
    [JsonStringEnumMemberName("filing.consume")]
    FilingConsume,

    /// <summary>The final submit; purpose is its result.</summary>
    [JsonStringEnumMemberName("filing.submit")]
    FilingSubmit,

    /// <summary>A run stopped before the final submit; purpose is the reason.</summary>
    [JsonStringEnumMemberName("filing.hand_back")]
    FilingHandBack,

    /// <summary>The attorney's answer to a hand-back or unknown outcome.</summary>
    [JsonStringEnumMemberName("filing.resolve")]
    FilingResolve,

    // The pipeline's reads (18.17). Python's routes and workers wrote these
    // strings without listing them in ACTIONS; the enum makes them a closed set.

    /// <summary>A case's packets were listed.</summary>
    [JsonStringEnumMemberName("packet.read")]
    PacketRead,

    /// <summary>One packet's download URL was minted.</summary>
    [JsonStringEnumMemberName("packet.download")]
    PacketDownload,

    /// <summary>The packet worker read the whole case file, on the accepting preparer's behalf.</summary>
    [JsonStringEnumMemberName("packet.assemble")]
    PacketAssemble,

    /// <summary>The review worker read the whole case file, on the accepting preparer's behalf.</summary>
    [JsonStringEnumMemberName("petition.review")]
    PetitionReview,

    /// <summary>The extraction worker read a source document, on the accepting preparer's behalf.</summary>
    [JsonStringEnumMemberName("document.extract")]
    DocumentExtract,

    /// <summary>A case's review queue was read.</summary>
    [JsonStringEnumMemberName("extraction.read")]
    ExtractionRead,

    /// <summary>A candidate was accepted or rejected.</summary>
    [JsonStringEnumMemberName("extraction.review")]
    ExtractionReview,

    /// <summary>One form was rendered for preview (the forms hub, 18.15c) — beside, never instead of, B121's <c>taxid.read</c> rows.</summary>
    [JsonStringEnumMemberName("form_preview.render")]
    FormPreviewRender,
}

/// <summary>Whether the caller got the data. A denied read is the interesting row: it is what probing looks like.</summary>
public enum AccessOutcome
{
    /// <summary>Access was granted.</summary>
    [JsonStringEnumMemberName("allowed")]
    Allowed,

    /// <summary>Access was refused.</summary>
    [JsonStringEnumMemberName("denied")]
    Denied,
}

/// <summary>What a row is ABOUT — exactly one of these, named by its subject key's prefix.</summary>
public enum AccessSubjectKind
{
    /// <summary>A case: <c>CASE#&lt;case_id&gt;</c>.</summary>
    [JsonStringEnumMemberName("CASE")]
    Case,

    /// <summary>A firm client: <c>CLIENT#&lt;client_id&gt;</c>.</summary>
    [JsonStringEnumMemberName("CLIENT")]
    Client,

    /// <summary>A filing credential: <c>CREDENTIAL#&lt;credential_id&gt;</c>.</summary>
    [JsonStringEnumMemberName("CREDENTIAL")]
    Credential,

    /// <summary>A filing authorization: <c>AUTHORIZATION#&lt;authorization_id&gt;</c>.</summary>
    [JsonStringEnumMemberName("AUTHORIZATION")]
    Authorization,
}

/// <summary>
/// One append-only access-log row (issue 8.3; from <c>access_log.py</c>):
/// who read or changed which case, client, credential or authorization —
/// the question CloudTrail cannot answer, since it only ever sees the
/// service's role. Nothing expires; the log is write-only by design.
/// </summary>
/// <param name="SubjectKind">What the row is about.</param>
/// <param name="SubjectId">Its id.</param>
/// <param name="Principal">Who acted (a firm user's or a client's subject).</param>
/// <param name="Action">What they did.</param>
/// <param name="Outcome">Whether they got it.</param>
/// <param name="RecordedAt">When, UTC, to the millisecond.</param>
/// <param name="EventId">The row's own id.</param>
public sealed record AccessEvent(
    AccessSubjectKind SubjectKind,
    string SubjectId,
    string Principal,
    AccessAction Action,
    AccessOutcome Outcome,
    DateTimeOffset RecordedAt,
    string EventId)
{
    /// <summary>Only <c>taxid.read</c>: the debtor whose identifier was opened.</summary>
    public string? FilingRole { get; init; }

    /// <summary>Why: the form printed, a credential's use, a void's reason, a submit's result.</summary>
    public string? Purpose { get; init; }

    /// <summary>Only <c>client.invite</c>/<c>client.revoke</c>: the binding's roles, canonical order, comma-joined.</summary>
    public string? Roles { get; init; }

    /// <summary><c>credential.open</c> and the <c>filing.*</c> rows: the filing the row is about.</summary>
    public string? FilingId { get; init; }

    /// <summary>The subject as one key — <c>CASE#&lt;id&gt;</c>, <c>CLIENT#&lt;id&gt;</c>, … — the log's partition.</summary>
    public string SubjectKey => SubjectKind.ToWire() + "#" + SubjectId;

    /// <summary>The case this row is about, or null.</summary>
    public string? CaseId => SubjectKind == AccessSubjectKind.Case ? SubjectId : null;

    /// <summary>The firm client this row is about, or null.</summary>
    public string? ClientId => SubjectKind == AccessSubjectKind.Client ? SubjectId : null;

    /// <summary>The filing credential this row is about, or null.</summary>
    public string? CredentialId => SubjectKind == AccessSubjectKind.Credential ? SubjectId : null;

    /// <summary>The filing authorization this row is about, or null.</summary>
    public string? AuthorizationId => SubjectKind == AccessSubjectKind.Authorization ? SubjectId : null;
}

/// <summary>Building access-log rows.</summary>
public static class AccessEvents
{
    /// <summary>
    /// One row about EXACTLY ONE subject. Naming more than one, or none, is a
    /// programming error: a row about two things answers neither "who saw
    /// this case" nor "who saw this client".
    /// </summary>
    public static AccessEvent Record(
        string principal,
        AccessAction action,
        string? caseId = null,
        string? clientId = null,
        string? credentialId = null,
        string? authorizationId = null,
        AccessOutcome outcome = AccessOutcome.Allowed,
        string? filingRole = null,
        string? purpose = null,
        IEnumerable<string>? roles = null,
        string? filingId = null,
        TimeProvider? clock = null)
    {
        var subjects = new (AccessSubjectKind Kind, string? Id)[]
        {
            (AccessSubjectKind.Case, caseId),
            (AccessSubjectKind.Client, clientId),
            (AccessSubjectKind.Credential, credentialId),
            (AccessSubjectKind.Authorization, authorizationId),
        }.Where(subject => subject.Id is not null).ToList();
        if (subjects.Count != 1)
        {
            throw new ArgumentException("an access event names exactly one of case_id, client_id, credential_id, authorization_id");
        }

        var joined = roles is null ? null : string.Join(",", roles);
        return new AccessEvent(subjects[0].Kind, subjects[0].Id!, principal, action, outcome, Clock.NowMillis(clock), Guid.NewGuid().ToString())
        {
            FilingRole = filingRole,
            Purpose = purpose,
            Roles = string.IsNullOrEmpty(joined) ? null : joined,
            FilingId = filingId,
        };
    }
}
