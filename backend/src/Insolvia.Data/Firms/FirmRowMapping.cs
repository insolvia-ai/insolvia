using Insolvia.Core;
using Insolvia.Core.Claims;
using Insolvia.Core.Debtors;
using Insolvia.Core.DocumentRequests;
using Insolvia.Core.Fields;
using Insolvia.Core.FirmClients;
using Insolvia.Core.Firms;
using Insolvia.Core.LibraryCreditors;
using Insolvia.Core.Questionnaires;
using Insolvia.Data.Storage;

namespace Insolvia.Data.Firms;

/// <summary>
/// A firm client's editable fields — the <c>body</c> document. Exactly the
/// caller-editable half of <see cref="FirmClient"/> (<see cref="FirmClientDraft"/>
/// minus the retained date, which is a column the funnel conditions on).
/// </summary>
internal sealed record FirmClientBody(
    PersonName Name,
    ValueCollection<OtherName> OtherNamesUsed,
    DateOnly? DateOfBirth,
    Address ResidenceAddress,
    Address MailingAddress,
    string? Phone,
    string? Mobile,
    string? Email,
    string? LeadSource,
    string? ReferredBy);

/// <summary>Domain record ↔ row, for every firm-scoped table.</summary>
internal static class FirmRowMapping
{
    public static FirmRow ToRow(Firm firm) => new()
    {
        Id = firm.Id,
        Name = firm.Name,
        Status = firm.Status,
        CreatedAt = firm.CreatedAt,
        UpdatedAt = firm.UpdatedAt,
        CreatedBy = firm.CreatedBy,
        CreatedByEmail = firm.CreatedByEmail,
        DefaultCourt = firm.DefaultCourt?.Court,
        DefaultDivision = firm.DefaultCourt?.Division,
        DefaultChapter = firm.DefaultChapter,
        Letterhead = StoredJson.ToNullableDocument(firm.Letterhead),
    };

    public static Firm ToDomain(FirmRow row) => new(
        row.Id,
        row.Name,
        row.Status,
        row.CreatedAt,
        row.UpdatedAt,
        row.CreatedBy,
        row.CreatedByEmail,
        row.DefaultCourt is null || row.DefaultDivision is null ? null : new DefaultCourt(row.DefaultCourt, row.DefaultDivision),
        row.DefaultChapter,
        StoredJson.FromNullableDocument<Letterhead>(row.Letterhead));

    public static FirmUserRow ToRow(FirmUser user) => new()
    {
        FirmId = user.FirmId,
        Subject = user.Subject,
        Email = user.Email,
        FirstName = user.FirstName,
        LastName = user.LastName,
        Role = user.Role,
        IsAdmin = user.IsAdmin,
        AccessAllCases = user.AccessAllCases,
        Permissions = StoredJson.PermissionsToDocument(user.Permissions),
        Status = user.Status,
        CreatedAt = user.CreatedAt,
        UpdatedAt = user.UpdatedAt,
        SignatureBlock = StoredJson.ToNullableDocument(user.SignatureBlock),
    };

    public static FirmUser ToDomain(FirmUserRow row) => new(
        row.FirmId,
        row.Subject,
        row.Email,
        row.FirstName,
        row.LastName,
        row.Role,
        row.IsAdmin,
        row.AccessAllCases,
        StoredJson.PermissionsFromDocument(row.Permissions),
        row.Status,
        row.CreatedAt,
        row.UpdatedAt,
        StoredJson.FromNullableDocument<SignatureBlock>(row.SignatureBlock));

    public static LibraryCreditorRow ToRow(LibraryCreditor creditor) => new()
    {
        FirmId = creditor.FirmId,
        Id = creditor.Id,
        Name = creditor.Name,
        Address = StoredJson.ToDocument(creditor.Address),
        AdditionalNoticeParties = StoredJson.ToDocument(creditor.AdditionalNoticeParties),
        Preferred = creditor.Preferred,
        Notes = creditor.Notes,
        CreatedAt = creditor.CreatedAt,
        UpdatedAt = creditor.UpdatedAt,
    };

    public static LibraryCreditor ToDomain(LibraryCreditorRow row) => new(
        row.FirmId,
        row.Id,
        row.Name,
        StoredJson.FromDocument<Address>(row.Address),
        StoredJson.FromDocument<ValueCollection<NoticeParty>>(row.AdditionalNoticeParties),
        row.Preferred,
        row.Notes,
        row.CreatedAt,
        row.UpdatedAt);

    public static FirmClientRow ToRow(FirmClient client) => new()
    {
        FirmId = client.FirmId,
        Id = client.Id,
        Status = client.Status,
        CreatedAt = client.CreatedAt,
        UpdatedAt = client.UpdatedAt,
        CreatedBy = client.CreatedBy,
        Body = StoredJson.ToDocument(BodyOf(client)),
        FirstRetainedAt = client.FirstRetainedAt,
        TaxIdRef = client.TaxIdRef,
        TaxIdLastFour = client.TaxIdLastFour,
        MergedInto = client.MergedInto,
        MergingInto = client.MergingInto,
        MergingFrom = client.MergingFrom,
        ProspectStage = client.ProspectStage,
    };

    public static FirmClientBody BodyOf(FirmClient client) => new(
        client.Name,
        client.OtherNamesUsed,
        client.DateOfBirth,
        client.ResidenceAddress,
        client.MailingAddress,
        client.Phone,
        client.Mobile,
        client.Email,
        client.LeadSource,
        client.ReferredBy);

    public static FirmClient ToDomain(FirmClientRow row)
    {
        var body = StoredJson.FromDocument<FirmClientBody>(row.Body);
        return new FirmClient(row.Id, row.FirmId, row.Status, row.CreatedAt, row.UpdatedAt, row.CreatedBy)
        {
            Name = body.Name ?? new PersonName(),
            OtherNamesUsed = body.OtherNamesUsed ?? [],
            DateOfBirth = body.DateOfBirth,
            ResidenceAddress = body.ResidenceAddress ?? new Address(),
            MailingAddress = body.MailingAddress ?? new Address(),
            Phone = body.Phone,
            Mobile = body.Mobile,
            Email = body.Email,
            LeadSource = body.LeadSource,
            ReferredBy = body.ReferredBy,
            FirstRetainedAt = row.FirstRetainedAt,
            TaxIdRef = row.TaxIdRef,
            TaxIdLastFour = row.TaxIdLastFour,
            MergedInto = row.MergedInto,
            MergingInto = row.MergingInto,
            MergingFrom = row.MergingFrom,
            ProspectStage = row.ProspectStage,
        };
    }

    public static QuestionnaireConfig ToDomain(FirmQuestionnaireRow row) =>
        new(row.FirmId, StoredJson.SectionsFromDocument(row.Sections), row.UpdatedAt, row.UpdatedBy);

    public static DocumentChecklist ToDomain(FirmDocumentChecklistRow row) =>
        new(row.FirmId, StoredJson.FromDocument<ValueCollection<ChecklistItem>>(row.Items), row.UpdatedAt, row.UpdatedBy);
}
