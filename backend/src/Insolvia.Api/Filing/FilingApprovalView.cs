using System.Text.Json.Serialization;
using Insolvia.Domain.Filings;
using Insolvia.Domain.Forms;

namespace Insolvia.Api.Filing;

/// <summary>
/// A case's filing approval as it stands: what would be approved now, and —
/// once one exists — the approval and the filing run it started.
/// </summary>
/// <param name="Basis">What an approval would be bound to, computed now.</param>
/// <param name="Approval">The case's current approval (voided if the filing set changed); absent when none.</param>
/// <param name="Filing">The filing run the approval started; absent before a worker claimed it.</param>
public sealed record FilingApprovalState(
    ApprovalBasisView Basis,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FilingApprovalView? Approval,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FilingView? Filing);

/// <summary>What a client sees of an attorney's approval to file.</summary>
/// <param name="Id">The approval's id.</param>
/// <param name="FilingId">The filing run it is for.</param>
/// <param name="Status">What it is now: <c>pending</c>, <c>consumed</c>, <c>voided</c>, or <c>expired</c> (pending past <c>expiresAt</c>).</param>
/// <param name="Digest">The basis digest the attorney approved.</param>
/// <param name="ApprovedBy">The approving attorney.</param>
/// <param name="ApprovedAt">When, ISO 8601 UTC with milliseconds.</param>
/// <param name="ExpiresAt">When it can no longer be consumed, ISO 8601 UTC with milliseconds.</param>
/// <param name="CredentialId">The CM/ECF credential it files with.</param>
/// <param name="Court">The court's code.</param>
/// <param name="Division">The division's code.</param>
/// <param name="PacketId">The packet it files.</param>
/// <param name="ConsumedAt">When the filing worker consumed it; absent until then.</param>
/// <param name="VoidedAt">When it was voided; absent unless voided.</param>
/// <param name="VoidReason">Why: <c>changed</c>, <c>superseded</c>, <c>cancelled</c> or <c>enqueue_failed</c>; absent unless voided.</param>
public sealed record FilingApprovalView(
    string Id,
    string FilingId,
    string Status,
    string Digest,
    string ApprovedBy,
    string ApprovedAt,
    string ExpiresAt,
    string CredentialId,
    string Court,
    string Division,
    string PacketId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ConsumedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? VoidedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? VoidReason)
{
    /// <summary>The view of <paramref name="approval"/> at <paramref name="now"/>.</summary>
    /// <param name="approval">The stored approval.</param>
    /// <param name="now">The moment its status is judged at.</param>
    /// <returns>The view.</returns>
    public static FilingApprovalView Of(FilingApproval approval, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(approval);
        return new(
            approval.ApprovalId,
            approval.FilingId,
            FilingApprovalRecords.EffectiveStatus(approval, now),
            approval.Digest,
            approval.AttorneyId,
            FilingApprovalRecords.Iso(approval.ApprovedAt),
            FilingApprovalRecords.Iso(DateTimeOffset.FromUnixTimeSeconds(approval.ExpiresAt)),
            approval.CredentialId,
            approval.Court,
            approval.Division,
            approval.PacketId,
            approval.ConsumedAt is { } consumed ? FilingApprovalRecords.Iso(consumed) : null,
            approval.VoidedAt is { } voided ? FilingApprovalRecords.Iso(voided) : null,
            approval.VoidReason);
    }
}

/// <summary>
/// WHAT THE ATTORNEY IS SHOWN before approving: the court, the documents in
/// docket order with their sizes and digests, the checklist, the fee handling
/// and the digest over all of it. Never the debtor's data, never a tax id.
/// </summary>
/// <param name="Scheme">The digest's scheme.</param>
/// <param name="Digest">The digest over everything shown; post it back to approve.</param>
/// <param name="Ready">Whether nothing blocks approving.</param>
/// <param name="Blockers">What blocks approving, for a person.</param>
/// <param name="RegistryRelease">The court-registry release the filing set was judged against.</param>
/// <param name="Documents">The documents, in docket order.</param>
/// <param name="Checklist">The hand-off checklist.</param>
/// <param name="Fee">How the filing fee is handled.</param>
/// <param name="SignInMaxAgeSeconds">How recent a sign-in approving needs.</param>
/// <param name="ApprovalTtlSeconds">How long an approval stays usable.</param>
/// <param name="Court">The court; absent while the case names none.</param>
/// <param name="Packet">The assembled packet; absent before one exists.</param>
public sealed record ApprovalBasisView(
    string Scheme,
    string Digest,
    bool Ready,
    IReadOnlyList<string> Blockers,
    string RegistryRelease,
    IReadOnlyList<BasisDocumentView> Documents,
    IReadOnlyList<BasisChecklistItemView> Checklist,
    BasisFeeView Fee,
    int SignInMaxAgeSeconds,
    int ApprovalTtlSeconds,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BasisCourtView? Court,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BasisPacketView? Packet)
{
    private const string FeeDetail = "Insolvia stores and enters no card details: the filing stops at the court's payment step and is handed back to you to pay in your own CM/ECF session, within the court's window.";

    /// <summary>The view of <paramref name="basis"/>.</summary>
    /// <param name="basis">The basis, computed now.</param>
    /// <returns>The view.</returns>
    public static ApprovalBasisView Of(ApprovalBasis basis)
    {
        ArgumentNullException.ThrowIfNull(basis);
        var set = basis.FilingSet;
        return new(
            ApprovalDigest.Scheme,
            basis.Digest,
            basis.Ready,
            [.. basis.Blockers],
            set.RegistryRelease,
            [.. set.Documents.Select((document, index) => new BasisDocumentView(
                index + 1,
                document.Key,
                document.Title,
                document.FileName,
                document.Source,
                document.Handling,
                document.Part is { } part ? new BasisFileView(part.ByteSize, part.PageCount, part.Sha256) : null,
                document.Note.Length > 0 ? document.Note : null))],
            [.. set.Checklist.Select(item => new BasisChecklistItemView(item.Id, item.Status, item.Title, item.Detail, item.Link))],
            new BasisFeeView(basis.Fee.Handling, basis.Fee.Verified, FeeDetail, basis.Fee.Deadline),
            FilingApprovals.SignInMaxAgeSeconds,
            FilingApprovals.TtlSeconds,
            set.CourtCode is not null && set.CourtName is not null ? new BasisCourtView(set.CourtCode, set.CourtName, set.DivisionName) : null,
            set.Packet is { } packet ? new BasisPacketView(packet.Id, packet.CreatedAt, packet.Sha256, packet.ByteSize) : null);
    }
}

/// <summary>One document of the filing set.</summary>
/// <param name="Position">Its place in docket order, from 1.</param>
/// <param name="Key">The document's stable key.</param>
/// <param name="Title">Its title.</param>
/// <param name="FileName">The name the court wants it under.</param>
/// <param name="Source"><c>packet</c> or <c>outside</c>.</param>
/// <param name="Handling"><c>file</c>, <c>own_event</c>, <c>restricted</c> or <c>not_filed</c>.</param>
/// <param name="File">The measured file; absent when it is not in the packet.</param>
/// <param name="Note">A note for the attorney; absent when none.</param>
public sealed record BasisDocumentView(
    int Position,
    string Key,
    string Title,
    string FileName,
    string Source,
    string Handling,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BasisFileView? File,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Note);

/// <summary>A document's file as measured at assembly.</summary>
/// <param name="ByteSize">Its size in bytes.</param>
/// <param name="PageCount">Its pages; absent for a file that is not a PDF.</param>
/// <param name="Sha256">The digest of its bytes; absent when not measured.</param>
public sealed record BasisFileView(
    long ByteSize,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? PageCount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Sha256);

/// <summary>One line of the hand-off checklist.</summary>
/// <param name="Id">The line's id.</param>
/// <param name="Status"><c>ready</c>, <c>missing</c>, <c>action</c> or <c>confirm</c>.</param>
/// <param name="Title">Its title.</param>
/// <param name="Detail">Its detail, for a person.</param>
/// <param name="Link">The app segment that fixes it; absent when the step is outside Insolvia.</param>
public sealed record BasisChecklistItemView(
    string Id,
    string Status,
    string Title,
    string Detail,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Link);

/// <summary>How the filing fee is handled: handed back at the court's payment step.</summary>
/// <param name="Handling">The handling's code.</param>
/// <param name="Verified">Whether the court's rule is verified.</param>
/// <param name="Detail">What happens at the payment step, for a person.</param>
/// <param name="Deadline">The court's payment window; absent when not recorded.</param>
public sealed record BasisFeeView(
    string Handling,
    bool Verified,
    string Detail,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Deadline);

/// <summary>The court a filing goes to.</summary>
/// <param name="Code">The court's code.</param>
/// <param name="Name">Its name.</param>
/// <param name="DivisionName">The division's name; absent when none.</param>
public sealed record BasisCourtView(
    string Code,
    string Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DivisionName);

/// <summary>The assembled packet a filing files.</summary>
/// <param name="Id">The packet's id.</param>
/// <param name="CreatedAt">When it was assembled.</param>
/// <param name="Sha256">The digest of the zip.</param>
/// <param name="ByteSize">Its size in bytes.</param>
public sealed record BasisPacketView(string Id, string CreatedAt, string Sha256, long ByteSize);

/// <summary>
/// An attorney's approval (<c>POST /v1/cases/{caseId}/filing-approval</c>):
/// the digest of the basis as shown. Parsed by <c>FilingApprovals.ApproveAsync</c>.
/// </summary>
/// <param name="Digest">The basis digest shown; a mismatch is refused.</param>
/// <param name="CredentialId">The CM/ECF credential to file with; absent for the caller's only one.</param>
[SnakeCase]
public sealed record FilingApproveRequest(
    string Digest,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CredentialId);

/// <summary>A refused approval: the filing set is not ready.</summary>
/// <param name="Error">Always <c>FilingSetNotReady</c>.</param>
/// <param name="Message">What went wrong, for a person.</param>
/// <param name="Blockers">What blocks approving.</param>
public sealed record FilingSetNotReadyBody(string Error, string Message, IReadOnlyList<string> Blockers);

/// <summary>A refused resolution.</summary>
/// <param name="Error">Always <c>ResolutionRefused</c>.</param>
/// <param name="Message">What went wrong, for a person.</param>
/// <param name="Reason">The refusal's code.</param>
public sealed record ResolutionRefusedBody(string Error, string Message, string Reason);
