using System.Collections.Frozen;
using Insolvia.Filing.Http;

namespace Insolvia.Filing.Drivers;

/// <summary>
/// Stop before the final submit (from <c>drivers/base.py</c>'s
/// <c>HandBackError</c>). <see cref="Reason"/> is a key of
/// <see cref="HandBackReasons.HandBack"/>; <see cref="CourtSaid"/> the court's
/// own short message when it gave one.
/// </summary>
public sealed class HandBackException : Exception
{
    /// <summary>Creates the exception with no message.</summary>
    public HandBackException()
    {
        Reason = string.Empty;
    }

    /// <summary>Creates the exception for a reason.</summary>
    public HandBackException(string reason)
        : base(reason)
    {
        Reason = reason;
    }

    /// <summary>Creates the exception with a reason and its cause.</summary>
    public HandBackException(string reason, Exception innerException)
        : base(reason, innerException)
    {
        Reason = reason;
    }

    /// <summary>Creates the exception with a reason and what the court said.</summary>
    public HandBackException(string reason, string? courtSaid, Exception? innerException = null)
        : base(reason, innerException)
    {
        Reason = reason;
        CourtSaid = courtSaid;
    }

    /// <summary>The hand-back reason.</summary>
    public string Reason { get; }

    /// <summary>What the court said.</summary>
    public string? CourtSaid { get; }
}

/// <summary>
/// The final submit happened (or may have) and its result is not a
/// recognised confirmation. <see cref="Reason"/> is a key of
/// <see cref="HandBackReasons.OutcomeUnknown"/>. Never retried.
/// </summary>
public sealed class SubmitUncertainException : Exception
{
    /// <summary>Creates the exception with no message.</summary>
    public SubmitUncertainException()
    {
        Reason = string.Empty;
    }

    /// <summary>Creates the exception for a reason.</summary>
    public SubmitUncertainException(string reason)
        : base(reason)
    {
        Reason = reason;
    }

    /// <summary>Creates the exception with a reason and its cause.</summary>
    public SubmitUncertainException(string reason, Exception innerException)
        : base(reason, innerException)
    {
        Reason = reason;
    }

    /// <summary>Creates the exception with a reason and what the court said.</summary>
    public SubmitUncertainException(string reason, string? courtSaid, Exception? innerException = null)
        : base(reason, innerException)
    {
        Reason = reason;
        CourtSaid = courtSaid;
    }

    /// <summary>The outcome-unknown reason.</summary>
    public string Reason { get; }

    /// <summary>What the court said.</summary>
    public string? CourtSaid { get; }
}

/// <summary>What the opening screens need: the fake opens a case by chapter, office and joint flag.</summary>
/// <param name="Court">The court's code.</param>
/// <param name="Division">The division's code — the office.</param>
/// <param name="Chapter">The chapter.</param>
/// <param name="Joint">Two debtors.</param>
public sealed record CaseOpening(string Court, string Division, int Chapter, bool Joint);

/// <summary>
/// One document to upload, in docket order: the bytes and the digest the
/// approval was bound to (already checked against the bytes). Its text form
/// never carries the content.
/// </summary>
/// <param name="Position">Docket position.</param>
/// <param name="Key">The filing-set key.</param>
/// <param name="FileName">The upload name.</param>
/// <param name="Handling">The docket event.</param>
/// <param name="Content">The bytes.</param>
/// <param name="Sha256">Their digest.</param>
public sealed record FilingFile(int Position, string Key, string FileName, string Handling, byte[] Content, string Sha256)
{
    /// <inheritdoc />
    public override string ToString() => $"FilingFile {{ Position = {Position}, Key = {Key}, FileName = {FileName}, Sha256 = {Sha256} }}";
}

/// <summary>What the court's confirmation screen said.</summary>
/// <param name="CaseNumber">The court's case number.</param>
/// <param name="FiledAt">The court's timestamp.</param>
/// <param name="DocketEntries">The entries it created.</param>
/// <param name="ReceiptNumber">The court's receipt, if any.</param>
/// <param name="FeeDue">The fee it asks for, if any.</param>
/// <param name="Page">The page, verbatim.</param>
public sealed record CourtConfirmation(string CaseNumber, string FiledAt, IReadOnlyList<string> DocketEntries, string? ReceiptNumber, string? FeeDue, byte[] Page)
{
    /// <inheritdoc />
    public override string ToString() => $"CourtConfirmation {{ CaseNumber = {CaseNumber}, FiledAt = {FiledAt} }}";
}

/// <summary>
/// One court session — the four steps the WORKER calls in order, writing a
/// state between each (<c>sign_in</c> → signed_in, <c>open_case</c> →
/// uploading, <c>upload</c>, then the mark and <c>final_submit</c>). A step
/// that cannot go on throws <see cref="HandBackException"/> — always safe
/// before the final submit. <see cref="FinalSubmitAsync"/> NEVER throws
/// <see cref="HandBackException"/>: anything but a recognised confirmation is
/// <see cref="SubmitUncertainException"/>. A driver never retries, never
/// clicks forward through a screen it does not recognise, never alters its input.
/// </summary>
public interface ICourtSession
{
    /// <summary>Sign in. <paramref name="totp"/> computes the code at the moment the court asks — never ahead.</summary>
    Task SignInAsync(string login, string password, Func<string> totp, CancellationToken cancellationToken = default);

    /// <summary>Open the case.</summary>
    Task OpenCaseAsync(CaseOpening opening, CancellationToken cancellationToken = default);

    /// <summary>Upload, then check the court's own list against what was sent.</summary>
    Task UploadAsync(IReadOnlyList<FilingFile> files, CancellationToken cancellationToken = default);

    /// <summary>THE CLICK.</summary>
    Task<CourtConfirmation> FinalSubmitAsync(CancellationToken cancellationToken = default);
}

/// <summary>One court's screens (ADR 0024, "Per-court drivers").</summary>
public interface ICourtDriver
{
    /// <summary>The driver's id, recorded on the filing.</summary>
    string DriverId { get; }

    /// <summary>Every origin the driver will contact — checked against the fence BEFORE the credential is opened.</summary>
    IReadOnlyList<string> BaseUrls { get; }

    /// <summary>Whether the case opens through the court's Case Upload (Debtor.txt, uploaded first).</summary>
    bool CaseUpload { get; }

    /// <summary>A session over one run's fenced client.</summary>
    ICourtSession Start(ICourtHttpClient http);
}

/// <summary>The driver registry and its resolver.</summary>
public static class CourtDrivers
{
    /// <summary>
    /// The verified drivers, by court code. EMPTY on purpose: a court joins
    /// only in ADR 0024 PR 10, after its driver opened the fixture case end to
    /// end on that court's training database — and its host joins
    /// <see cref="Fence.HostFence"/> in the same reviewed diff. Until then
    /// every real court hands back.
    /// </summary>
    public static readonly FrozenDictionary<string, Func<ICourtDriver>> Verified =
        new Dictionary<string, Func<ICourtDriver>>(StringComparer.Ordinal).ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// The driver that files in <paramref name="court"/> here, or null: hand
    /// it back. <paramref name="fake"/> is the local composition's fake driver;
    /// locally it plays every court, anywhere else it is refused even if composed.
    /// </summary>
    public static ICourtDriver? Resolve(string court, string environment, Func<ICourtDriver>? fake = null)
    {
        if (fake is not null)
        {
            return environment == "local" ? fake() : null;
        }

        return Verified.TryGetValue(court ?? string.Empty, out var factory) ? factory() : null;
    }
}
