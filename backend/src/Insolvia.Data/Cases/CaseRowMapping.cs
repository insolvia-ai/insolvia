using Insolvia.Core;
using Insolvia.Core.Candidates;
using Insolvia.Core.CaseEntities;
using Insolvia.Core.Cases;
using Insolvia.Core.CaseTasks;
using Insolvia.Core.Debtors;
using Insolvia.Core.DocumentRequests;
using Insolvia.Core.Documents;
using Insolvia.Core.Filings;
using Insolvia.Core.Provenance;
using Insolvia.Core.TaxIds;
using Insolvia.Data.Storage;

namespace Insolvia.Data.Cases;

/// <summary>Domain record ↔ row, for every case-scoped table with a store.</summary>
internal static class CaseRowMapping
{
    // ── Cases ───────────────────────────────────────────────────────

    public static CaseRow ToRow(Case stored) => new()
    {
        Id = stored.Id,
        FirmId = stored.FirmId,
        CreatedBy = stored.CreatedBy,
        Chapter = stored.Chapter,
        District = stored.District,
        Status = stored.Status,
        CreatedAt = stored.CreatedAt,
        UpdatedAt = stored.UpdatedAt,
        Court = stored.Court,
        Division = stored.Division,
        FormRevisions = StoredJson.ToNullableDocument(stored.FormRevisions?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)),
        ConstantsSetId = stored.ConstantsSetId,
        ExemptionSet = stored.ExemptionSet,
        FiledAt = stored.FiledAt,
        Meeting341At = stored.Meeting341At,
        CaseNumber = stored.CaseNumber,
        CaseNumberKey = CaseNumbers.Key(stored.Court, stored.CaseNumber),
        Judge = stored.Judge,
        Trustee = stored.Trustee,
        OfficeFileNumber = stored.OfficeFileNumber,
        ArchivedAt = stored.ArchivedAt,
        ArchivedBy = stored.ArchivedBy,
        DeletedAt = stored.DeletedAt,
        DeletedBy = stored.DeletedBy,
    };

    public static Case ToDomain(CaseRow row) => new(row.Id, row.FirmId, row.CreatedBy, row.Chapter, row.District, row.Status, row.CreatedAt, row.UpdatedAt)
    {
        Court = row.Court,
        Division = row.Division,
        FormRevisions = row.FormRevisions is null
            ? null
            : StoredJson.FromDocument<Dictionary<string, string>>(row.FormRevisions).OrderBy(pair => pair.Key, StringComparer.Ordinal).ToValueDictionary(),
        ConstantsSetId = row.ConstantsSetId,
        ExemptionSet = row.ExemptionSet,
        FiledAt = row.FiledAt,
        Meeting341At = row.Meeting341At,
        CaseNumber = row.CaseNumber,
        Judge = row.Judge,
        Trustee = row.Trustee,
        OfficeFileNumber = row.OfficeFileNumber,
        ArchivedAt = row.ArchivedAt,
        ArchivedBy = row.ArchivedBy,
        DeletedAt = row.DeletedAt,
        DeletedBy = row.DeletedBy,
    };

    public static CaseAssignmentRow ToRow(CaseAssignment assignment, string firmId) => new()
    {
        CaseId = assignment.CaseId,
        Subject = assignment.Subject,
        FirmId = firmId,
        CaseCreatedAt = assignment.CaseCreatedAt,
        AssignedAt = assignment.AssignedAt,
        AssignedBy = assignment.AssignedBy,
    };

    public static CaseAssignment ToDomain(CaseAssignmentRow row) =>
        new(row.CaseId, row.Subject, row.CaseCreatedAt, row.AssignedAt, row.AssignedBy);

    public static LifecycleEventRow ToRow(LifecycleEvent recorded, string firmId) => new()
    {
        CaseId = recorded.CaseId,
        RecordedAt = recorded.RecordedAt,
        FirmId = firmId,
        Kind = recorded.Kind,
        EffectiveOn = recorded.EffectiveOn,
        RecordedBy = recorded.RecordedBy,
        Provenance = recorded.Provenance,
        CandidateId = recorded.CandidateId,
        FromStatus = recorded.FromStatus,
        ToStatus = recorded.ToStatus,
        Chapter = recorded.Chapter,
        FilingId = recorded.FilingId,
        FromChapter = recorded.FromChapter,
        ToChapter = recorded.ToChapter,
        PlanVersionId = recorded.PlanVersionId,
    };

    public static LifecycleEvent ToDomain(LifecycleEventRow row) =>
        new(row.CaseId, row.Kind, row.EffectiveOn, row.RecordedAt, row.RecordedBy, row.Provenance)
        {
            CandidateId = row.CandidateId,
            FromStatus = row.FromStatus,
            ToStatus = row.ToStatus,
            Chapter = row.Chapter,
            FilingId = row.FilingId,
            FromChapter = row.FromChapter,
            ToChapter = row.ToChapter,
            PlanVersionId = row.PlanVersionId,
        };

    // ── Debtors and the generic collections ─────────────────────────

    public static DebtorRow ToRow(Debtor debtor, string firmId) => new()
    {
        CaseId = debtor.CaseId,
        FilingRole = debtor.FilingRole,
        FirmId = firmId,
        Id = debtor.Id,
        CreatedAt = debtor.CreatedAt,
        UpdatedAt = debtor.UpdatedAt,
        Body = StoredJson.FromNode(BodyJson.Prune(DebtorRules.BodyNode(debtor.Body))),
        Provenance = StoredJson.FromNode(ProvenanceRules.ToJson(debtor.Provenance)),
        TaxIdKind = debtor.TaxId?.Kind,
        TaxIdLastFour = debtor.TaxId?.LastFour,
        TaxIdRef = debtor.TaxId?.Ref,
        ClientId = debtor.ClientId,
        CaseCreatedAt = debtor.CaseCreatedAt,
    };

    /// <summary>
    /// A stored debtor through the domain's own parser, provenance NOT
    /// enforced (a rule tightening never makes stored rows unreadable).
    /// </summary>
    public static Debtor ToDomain(DebtorRow row)
    {
        var payload = StoredJson.ToObject(row.Body);
        payload["provenance"] = StoredJson.ToObject(row.Provenance);
        var draft = DebtorRules.Parse(payload, enforceProvenance: false);
        return new Debtor(row.Id, row.CaseId, row.FilingRole, row.CreatedAt, row.UpdatedAt, draft.Body)
        {
            TaxId = row.TaxIdKind is { } kind && row.TaxIdLastFour is not null && row.TaxIdRef is not null
                ? new TaxIdRef(kind, row.TaxIdLastFour, row.TaxIdRef)
                : null,
            Provenance = draft.Provenance,
            ClientId = row.ClientId,
            CaseCreatedAt = row.CaseCreatedAt,
        };
    }

    public static CaseEntityRow ToRow(ICaseEntity entity, string firmId) => new()
    {
        CaseId = entity.CaseId,
        Kind = entity.Kind.SkPrefix,
        Id = entity.Id,
        FirmId = firmId,
        CreatedAt = entity.CreatedAt,
        UpdatedAt = entity.UpdatedAt,
        Body = StoredJson.FromNode(BodyJson.Prune(entity.BodyNode())),
        Provenance = StoredJson.FromNode(ProvenanceRules.ToJson(entity.Provenance)),
        Amended = entity.Amended,
    };

    public static ICaseEntity ToDomain(CaseEntityRow row, IEntityKind kind)
    {
        var payload = StoredJson.ToObject(row.Body);
        payload["provenance"] = StoredJson.ToObject(row.Provenance);
        payload["amended"] = row.Amended;
        return kind.Restore(row.Id, row.CaseId, row.CreatedAt, row.UpdatedAt, kind.ParseDraft(payload, enforceProvenance: false));
    }

    // ── Documents, candidates, tasks, requests ──────────────────────

    public static DocumentRow ToRow(Document document, string firmId) => new()
    {
        CaseId = document.CaseId,
        Id = document.Id,
        FirmId = firmId,
        Kind = document.Kind,
        FileName = document.FileName,
        ContentType = document.ContentType,
        ByteSize = document.ByteSize,
        StorageRef = document.StorageRef,
        UploadedBy = document.UploadedBy,
        UploadedAt = document.UploadedAt,
        Status = document.Status,
        Etag = document.Etag,
        Channel = document.Channel,
        RequestId = document.RequestId,
    };

    public static Document ToDomain(DocumentRow row) =>
        new(row.Id, row.CaseId, row.Kind, row.FileName, row.ContentType, row.ByteSize, row.StorageRef, row.UploadedBy, row.UploadedAt)
        {
            Status = row.Status,
            Etag = row.Etag,
            Channel = row.Channel,
            RequestId = row.RequestId,
        };

    public static CandidateRow ToRow(Candidate candidate, string firmId) => new()
    {
        CaseId = candidate.CaseId,
        Id = candidate.Id,
        FirmId = firmId,
        EntityType = candidate.EntityType,
        Payload = StoredJson.FromNode(candidate.Payload),
        Status = candidate.Status,
        OriginChannel = candidate.Origin.Channel,
        OriginClientId = candidate.Origin.ClientId,
        OriginSubject = candidate.Origin.Subject,
        CreatedAt = candidate.CreatedAt,
        UpdatedAt = candidate.UpdatedAt,
        DocumentId = candidate.DocumentId,
        ExternalRef = StoredJson.ToNullableDocument(candidate.ExternalRef),
        Note = candidate.Note,
        Confidence = candidate.Confidence,
        Locator = StoredJson.FromNullableNode(candidate.Locator),
        ConfirmedBy = candidate.ConfirmedBy,
        ConfirmedAt = candidate.ConfirmedAt,
        CorrectedPayload = StoredJson.FromNullableNode(candidate.CorrectedPayload),
        ResultingRecordId = candidate.ResultingRecordId,
    };

    public static Candidate ToDomain(CandidateRow row) =>
        new(
            row.Id,
            row.CaseId,
            row.EntityType,
            StoredJson.ToObject(row.Payload),
            row.Status,
            new CandidateOrigin(row.OriginChannel, row.OriginClientId, row.OriginSubject),
            row.CreatedAt,
            row.UpdatedAt)
        {
            DocumentId = row.DocumentId,
            ExternalRef = StoredJson.FromNullableDocument<ExternalRef>(row.ExternalRef),
            Note = row.Note,
            Confidence = row.Confidence,
            Locator = StoredJson.ToNullableObject(row.Locator),
            ConfirmedBy = row.ConfirmedBy,
            ConfirmedAt = row.ConfirmedAt,
            CorrectedPayload = StoredJson.ToNullableObject(row.CorrectedPayload),
            ResultingRecordId = row.ResultingRecordId,
        };

    public static CaseTaskRow ToRow(CaseTask task, string firmId) => new()
    {
        CaseId = task.CaseId,
        Id = task.Id,
        FirmId = firmId,
        Subject = task.Subject,
        Description = task.Description,
        DueDate = task.DueDate,
        AssigneeSubject = task.AssigneeSubject,
        FormSeries = task.FormSeries,
        Done = task.Done,
        CompletedAt = task.CompletedAt,
        CreatedAt = task.CreatedAt,
        UpdatedAt = task.UpdatedAt,
        CreatedBy = task.CreatedBy,
    };

    public static CaseTask ToDomain(CaseTaskRow row) =>
        new(row.Id, row.CaseId, row.Subject, row.Description, row.DueDate, row.AssigneeSubject, row.FormSeries, row.Done, row.CompletedAt, row.CreatedAt, row.UpdatedAt, row.CreatedBy);

    public static DocumentRequestRow ToRow(DocumentRequest request, string firmId) => new()
    {
        CaseId = request.CaseId,
        Id = request.Id,
        FirmId = firmId,
        Title = request.Title,
        Kind = request.Kind,
        Description = request.Description,
        Status = request.Status,
        DocumentIds = [.. request.DocumentIds],
        CreatedAt = request.CreatedAt,
        CreatedBy = request.CreatedBy,
        UpdatedAt = request.UpdatedAt,
        ReceivedAt = request.ReceivedAt,
    };

    public static DocumentRequest ToDomain(DocumentRequestRow row) =>
        new(row.Id, row.CaseId, row.Title, row.Kind, row.Description, row.Status, row.DocumentIds.ToValueCollection(), row.CreatedAt, row.CreatedBy, row.UpdatedAt, row.ReceivedAt);

    // ── Tax ids and filings ─────────────────────────────────────────

    public static TaxIdRow ToRow(string caseId, SealedTaxId sealedId) => new()
    {
        CaseId = caseId,
        Ref = sealedId.Ref,
        FirmId = sealedId.FirmId,
        Kind = sealedId.Kind,
        LastFour = sealedId.LastFour,
        Scheme = sealedId.Envelope.Scheme,
        Ciphertext = Convert.FromBase64String(sealedId.Envelope.Ciphertext),
        Nonce = Convert.FromBase64String(sealedId.Envelope.Nonce),
        WrappedKey = Convert.FromBase64String(sealedId.Envelope.WrappedKey),
        CreatedAt = sealedId.CreatedAt,
        UpdatedAt = sealedId.UpdatedAt,
    };

    public static SealedTaxId ToDomain(TaxIdRow row) => new(
        row.Ref,
        row.FirmId,
        row.Kind,
        row.LastFour,
        new Envelope(row.Scheme, Convert.ToBase64String(row.Ciphertext), Convert.ToBase64String(row.Nonce), Convert.ToBase64String(row.WrappedKey)),
        row.CreatedAt,
        row.UpdatedAt);

    public static FilingRow ToRow(Filing filing) => new()
    {
        CaseId = filing.CaseId,
        FilingId = filing.FilingId,
        FirmId = filing.FirmId,
        ApprovalId = filing.ApprovalId,
        AttorneyId = filing.AttorneyId,
        CredentialId = filing.CredentialId,
        Court = filing.Court,
        Division = filing.Division,
        State = filing.State,
        AttemptId = filing.AttemptId,
        ClaimedAt = filing.ClaimedAt,
        LeaseExpiresAt = filing.LeaseExpiresAt,
        UpdatedAt = filing.UpdatedAt,
        History = StoredJson.ToDocument(filing.History),
        Driver = filing.Driver,
        HandBack = StoredJson.ToNullableDocument(filing.HandBack),
        UnknownReason = filing.UnknownReason,
        Confirmation = StoredJson.ToNullableDocument(filing.Confirmation),
        ReceiptDocumentId = filing.ReceiptDocumentId,
        FollowUps = [.. filing.FollowUps],
        Resolution = StoredJson.ToNullableDocument(filing.Resolution),
    };

    public static Filing ToDomain(FilingRow row) =>
        new(row.FilingId, row.CaseId, row.ApprovalId, row.FirmId, row.AttorneyId, row.CredentialId, row.Court, row.Division, row.State, row.AttemptId, row.ClaimedAt, row.LeaseExpiresAt, row.UpdatedAt)
        {
            History = StoredJson.FromDocument<ValueCollection<FilingStep>>(row.History),
            Driver = row.Driver,
            HandBack = StoredJson.FromNullableDocument<HandBackNote>(row.HandBack),
            UnknownReason = row.UnknownReason,
            Confirmation = StoredJson.FromNullableDocument<Confirmation>(row.Confirmation),
            ReceiptDocumentId = row.ReceiptDocumentId,
            FollowUps = row.FollowUps.ToValueCollection(),
            Resolution = StoredJson.FromNullableDocument<Resolution>(row.Resolution),
        };
}
