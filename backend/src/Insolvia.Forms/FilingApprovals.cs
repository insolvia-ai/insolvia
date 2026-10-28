using System.Text.Json.Nodes;
using Insolvia.Core;
using Insolvia.Core.AccessLog;
using Insolvia.Core.Auth;
using Insolvia.Core.Cases;
using Insolvia.Core.Courts;
using Insolvia.Core.FilingAuthorizations;
using Insolvia.Core.FilingCredentials;
using Insolvia.Core.Filings;
using Insolvia.Core.Firms;
using Insolvia.Core.Ports;

namespace Insolvia.Forms;

/// <summary>The caller may not approve this filing (403): not an attorney, or no openable court login of their own.</summary>
public sealed class ApprovalNotPermittedException : ForbiddenException
{
    /// <summary>Creates the exception with no message.</summary>
    public ApprovalNotPermittedException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public ApprovalNotPermittedException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and its cause.</summary>
    public ApprovalNotPermittedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The filing set has a blocking check outstanding (409, with <see cref="Blockers"/>).</summary>
public sealed class FilingSetNotReadyException : ConflictException
{
    /// <summary>Creates the exception with no message.</summary>
    public FilingSetNotReadyException()
    {
        Blockers = [];
    }

    /// <summary>Creates the exception with a message.</summary>
    public FilingSetNotReadyException(string message)
        : base(message)
    {
        Blockers = [];
    }

    /// <summary>Creates the exception with a message and its cause.</summary>
    public FilingSetNotReadyException(string message, Exception innerException)
        : base(message, innerException)
    {
        Blockers = [];
    }

    /// <summary>Creates the exception naming the blockers.</summary>
    public FilingSetNotReadyException(IReadOnlyList<string> blockers)
        : base("The filing set is not ready to approve: " + string.Join(", ", blockers ?? []) + ".")
    {
        Blockers = blockers ?? [];
    }

    /// <summary>The checklist ids still outstanding.</summary>
    public IReadOnlyList<string> Blockers { get; }
}

/// <summary>The approval could not hand its job to the filing queue; it was voided (<c>enqueue_failed</c>) first. A 503.</summary>
public sealed class FilingQueueUnavailableException : Exception
{
    /// <summary>Creates the exception with no message.</summary>
    public FilingQueueUnavailableException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public FilingQueueUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and its cause.</summary>
    public FilingQueueUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>No approval the filing worker may act on (unknown, used, voided, expired, changed). The worker hands back — never a retry.</summary>
public sealed class ApprovalUnavailableException : Exception
{
    /// <summary>Creates the exception with no message.</summary>
    public ApprovalUnavailableException()
    {
        Reason = string.Empty;
    }

    /// <summary>Creates the exception naming the reason.</summary>
    public ApprovalUnavailableException(string reason)
        : base(reason)
    {
        Reason = reason ?? string.Empty;
    }

    /// <summary>Creates the exception with a message and its cause.</summary>
    public ApprovalUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
        Reason = message ?? string.Empty;
    }

    /// <summary>Why.</summary>
    public string Reason { get; }
}

/// <summary>
/// The attorney's per-filing approval (ADR 0024 build PR 6, guardrail 1;
/// from <c>core/filing_approval.py</c>) — the rules the API's approval routes
/// and the filing worker's consume SHARE, which is why they live here and
/// not in a host. Approving writes the approval record and hands the filing
/// queue ONE job (<see cref="FilingJobs.Message"/>); nothing else produces one.
/// </summary>
/// <remarks>
/// Life: <c>pending</c> → <c>consumed</c> (once, by the worker) or <c>voided</c>
/// (<c>changed</c>, <c>superseded</c>, <c>cancelled</c>, <c>enqueue_failed</c>);
/// a pending approval past its expiry reads as <c>expired</c>. Single-use is
/// one conditional write; any change to the filing set voids it; one live
/// approval per case; it expires after <see cref="TtlSeconds"/>. Every
/// approve, void and consume writes an access row keyed by the case.
/// </remarks>
public static class FilingApprovals
{
    /// <summary>THE FRESH SIGN-IN: how old <c>auth_time</c> may be at Approve (guardrail 2's window).</summary>
    public const int SignInMaxAgeSeconds = 300;

    /// <summary>THE APPROVAL'S LIFETIME: one hour from approval to the worker's consume.</summary>
    public const int TtlSeconds = 3600;

    /// <summary>
    /// <see cref="ApprovalDigest.Basis"/> over the stores as they stand — the
    /// ONE composition of it: the approval routes and the filing worker
    /// (consume, and the re-check before the final submit) all call this, so
    /// the same reads give the same digest.
    /// </summary>
    public static async Task<(CaseData Data, ApprovalBasis Basis)> BasisForCaseAsync(
        Case @case,
        IDebtorStore debtors,
        ICaseEntityStore entities,
        IPacketReader packets,
        DateOnly asOf,
        CourtRegistry? registry = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(@case);
        ArgumentNullException.ThrowIfNull(packets);
        var data = await CaseDataReader.ReadAsync(@case, debtors, entities, cancellationToken).ConfigureAwait(false);
        var listed = await packets.ListForCaseAsync(@case.Id, cancellationToken).ConfigureAwait(false);
        var release = (registry ?? CourtRegistry.Shipped).Resolve(asOf);
        return (data, ApprovalDigest.Basis(data, listed, release, asOf));
    }

    /// <summary>
    /// Approve the filing set the caller was shown, and enqueue its one job.
    /// <paramref name="basis"/> is the basis over the stores NOW; the payload
    /// carries the digest the client rendered. Refuses, in order — each a
    /// denied <c>filing.approve</c> row naming it: a stale sign-in (403
    /// ReauthenticationRequired); not an attorney (403); a blocker (409); a
    /// digest other than today's (409); no current authorization or no
    /// openable login of the caller's own for the court (403), or two (409);
    /// a filing in flight or unresolved (409).
    /// </summary>
    public static async Task<FilingApproval> ApproveAsync(
        JsonNode? payload,
        CaseData data,
        ApprovalBasis basis,
        string firmId,
        string attorneyId,
        FirmRole role,
        long? authenticatedAt,
        DateTimeOffset now,
        IFilingCredentialStore credentials,
        IFilingAuthorizationStore authorizations,
        IFilingApprovalStore approvals,
        IFilingStore filings,
        IFilingJobSender queue,
        IAccessLog accessLog,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(basis);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(authorizations);
        ArgumentNullException.ThrowIfNull(approvals);
        ArgumentNullException.ThrowIfNull(filings);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(accessLog);
        var caseId = data.Case.Id;
        Task Refuse(string reason) => accessLog.RecordAsync(
            AccessEvents.Record(attorneyId, AccessAction.FilingApprove, caseId: caseId, outcome: AccessOutcome.Denied, purpose: reason, clock: new At(now)),
            cancellationToken);

        long fresh;
        try
        {
            fresh = TokenVerification.RequireRecentAuthentication(authenticatedAt, now, SignInMaxAgeSeconds);
        }
        catch (ForbiddenException)
        {
            await Refuse("reauthentication_required").ConfigureAwait(false);
            throw;
        }

        if (role != FirmRole.Attorney)
        {
            await Refuse("not_an_attorney").ConfigureAwait(false);
            throw new ApprovalNotPermittedException("Only the attorney whose court login files the case may approve it.");
        }

        var (shownDigest, credentialId) = ParseApprove(payload);
        if (basis.Blockers.Count > 0)
        {
            await Refuse("not_ready").ConfigureAwait(false);
            throw new FilingSetNotReadyException(basis.Blockers);
        }

        if (shownDigest != basis.Digest)
        {
            await Refuse("changed_since_shown").ConfigureAwait(false);
            throw new ConflictException("The filing set has changed since it was shown — review it again and approve what is there now.");
        }

        FilingCredential credential;
        string authorizationId;
        try
        {
            (credential, authorizationId) = await CredentialForAsync(data.Case.Court ?? string.Empty, firmId, attorneyId, credentialId, credentials, authorizations, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception refusal) when (refusal is ForbiddenException or ConflictException)
        {
            await Refuse(refusal switch
            {
                AuthorizationRequiredException => "authorization_required",
                ForbiddenException => "no_credential",
                _ => "credential_ambiguous",
            }).ConfigureAwait(false);
            throw;
        }

        var existing = await approvals.CurrentAsync(caseId, cancellationToken).ConfigureAwait(false);
        if (existing is { Status: ApprovalStatus.Consumed })
        {
            // A consumed approval stands in the way UNLESS its filing was
            // resolved as never having reached the court — the one thing
            // that frees a case.
            var record = await filings.GetAsync(caseId, existing.FilingId, cancellationToken).ConfigureAwait(false);
            if (record is null || !FilingRules.FreesCase(record))
            {
                var unresolved = record is not null && FilingRules.IsResolvable(record);
                await Refuse(unresolved ? "filing_unresolved" : "filing_in_flight").ConfigureAwait(false);
                throw new ConflictException(unresolved
                    ? "This case's last filing was handed back: record what the court's docket shows for it before approving another."
                    : "This case's last approval is already being filed — a second approval would be a second filing.");
            }
        }

        var packet = basis.FilingSet.Packet ?? throw new InvalidOperationException("a missing packet is a blocker");
        var stamp = Clock.NowMillis(new At(now));
        var approval = new FilingApproval(
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString(),
            caseId,
            firmId,
            attorneyId,
            credential.CredentialId,
            authorizationId,
            basis.Digest,
            packet.Id,
            data.Case.Court ?? string.Empty,
            data.Case.Division ?? string.Empty,
            basis.FilingSet.RegistryRelease,
            stamp,
            fresh,
            (long)Math.Floor(FilingApprovalRecords.Seconds(now)) + TtlSeconds);
        var replaced = existing is { Status: ApprovalStatus.Pending } ? existing : null;
        await approvals.CreateAsync(approval, existing, stamp, cancellationToken).ConfigureAwait(false);
        await accessLog.RecordAsync(
            AccessEvents.Record(attorneyId, AccessAction.FilingApprove, caseId: caseId, filingId: approval.FilingId, clock: new At(now)),
            cancellationToken).ConfigureAwait(false);
        if (replaced is not null)
        {
            await accessLog.RecordAsync(
                AccessEvents.Record(attorneyId, AccessAction.FilingVoid, caseId: caseId, purpose: VoidReasons.Superseded, filingId: replaced.FilingId, clock: new At(now)),
                cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await queue.EnqueueAsync(approval, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await VoidAsync(approval, VoidReasons.EnqueueFailed, attorneyId, now, approvals, accessLog, cancellationToken).ConfigureAwait(false);
            throw new FilingQueueUnavailableException("The filing could not be queued; the approval was voided.", error);
        }

        return approval;
    }

    /// <summary>
    /// Void a PENDING approval. The voided record, or null when it was not
    /// pending any more (the conditional write decides). Logged only when it happened.
    /// </summary>
    public static async Task<FilingApproval?> VoidAsync(
        FilingApproval approval,
        string reason,
        string principal,
        DateTimeOffset now,
        IFilingApprovalStore approvals,
        IAccessLog accessLog,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approval);
        ArgumentNullException.ThrowIfNull(approvals);
        ArgumentNullException.ThrowIfNull(accessLog);
        var at = Clock.NowMillis(new At(now));
        if (!await approvals.VoidAsync(approval.CaseId, approval.ApprovalId, reason, at, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        await accessLog.RecordAsync(
            AccessEvents.Record(principal, AccessAction.FilingVoid, caseId: approval.CaseId, purpose: reason, filingId: approval.FilingId, clock: new At(now)),
            cancellationToken).ConfigureAwait(false);
        return FilingApprovalRecords.Voided(approval, reason, at);
    }

    /// <summary>
    /// The case's current approval as of now — VOIDED here, recorded, if it
    /// is still pending but the filing set no longer has its digest, so
    /// "anything changed" shows the first time anybody looks.
    /// </summary>
    public static async Task<FilingApproval?> CurrentAsync(
        string caseId,
        ApprovalBasis basis,
        string principal,
        DateTimeOffset now,
        IFilingApprovalStore approvals,
        IAccessLog accessLog,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(basis);
        ArgumentNullException.ThrowIfNull(approvals);
        var approval = await approvals.CurrentAsync(caseId, cancellationToken).ConfigureAwait(false);
        if (approval is not null && FilingApprovalRecords.EffectiveStatus(approval, now) == "pending" && approval.Digest != basis.Digest)
        {
            return await VoidAsync(approval, VoidReasons.Changed, principal, now, approvals, accessLog, cancellationToken).ConfigureAwait(false)
                ?? await approvals.CurrentAsync(caseId, cancellationToken).ConfigureAwait(false);
        }

        return approval;
    }

    /// <summary>Withdraw the case's pending approval before the worker consumes it (no fresh sign-in: it only takes authority away). 409 when nothing is pending.</summary>
    public static async Task<FilingApproval> CancelAsync(
        string caseId,
        string principal,
        DateTimeOffset now,
        IFilingApprovalStore approvals,
        IAccessLog accessLog,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approvals);
        var approval = await approvals.CurrentAsync(caseId, cancellationToken).ConfigureAwait(false);
        if (approval is null || FilingApprovalRecords.EffectiveStatus(approval, now) != "pending")
        {
            throw new ConflictException("There is no pending approval to cancel.");
        }

        return await VoidAsync(approval, VoidReasons.Cancelled, principal, now, approvals, accessLog, cancellationToken).ConfigureAwait(false)
            ?? throw new ConflictException("The approval was consumed or voided first.");
    }

    /// <summary>
    /// THE WORKER'S ACT, and the one way an approval is used: once.
    /// <paramref name="basis"/> is recomputed from the stores now. Refuses
    /// (<see cref="ApprovalUnavailableException"/>) one that is unknown, not
    /// pending or expired; VOIDS one whose digest the filing set no longer has
    /// (<c>changed</c>) and refuses it; otherwise ONE conditional write —
    /// pending, same digest, unexpired — so of two consumers exactly one wins.
    /// </summary>
    public static async Task<FilingApproval> ConsumeAsync(
        string caseId,
        string approvalId,
        ApprovalBasis basis,
        DateTimeOffset now,
        IFilingApprovalStore approvals,
        IAccessLog accessLog,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(basis);
        ArgumentNullException.ThrowIfNull(approvals);
        ArgumentNullException.ThrowIfNull(accessLog);
        var approval = await approvals.GetAsync(caseId, approvalId, cancellationToken).ConfigureAwait(false)
            ?? throw new ApprovalUnavailableException("unknown");

        async Task<ApprovalUnavailableException> Refuse(string reason)
        {
            await accessLog.RecordAsync(
                AccessEvents.Record(approval.AttorneyId, AccessAction.FilingConsume, caseId: caseId, outcome: AccessOutcome.Denied, purpose: reason, filingId: approval.FilingId, clock: new At(now)),
                cancellationToken).ConfigureAwait(false);
            return new ApprovalUnavailableException(reason);
        }

        var status = FilingApprovalRecords.EffectiveStatus(approval, now);
        if (status != "pending")
        {
            throw await Refuse(status).ConfigureAwait(false);
        }

        if (approval.Digest != basis.Digest)
        {
            await VoidAsync(approval, VoidReasons.Changed, approval.AttorneyId, now, approvals, accessLog, cancellationToken).ConfigureAwait(false);
            throw await Refuse("changed").ConfigureAwait(false);
        }

        var consumedAt = Clock.NowMillis(new At(now));
        if (!await approvals.ConsumeAsync(caseId, approvalId, basis.Digest, (long)Math.Floor(FilingApprovalRecords.Seconds(now)), consumedAt, cancellationToken).ConfigureAwait(false))
        {
            throw await Refuse("already_used").ConfigureAwait(false);
        }

        await accessLog.RecordAsync(
            AccessEvents.Record(approval.AttorneyId, AccessAction.FilingConsume, caseId: caseId, filingId: approval.FilingId, clock: new At(now)),
            cancellationToken).ConfigureAwait(false);
        return FilingApprovalRecords.Consumed(approval, consumedAt);
    }

    private static (string Digest, string? CredentialId) ParseApprove(JsonNode? payload)
    {
        if (payload is not JsonObject body)
        {
            throw new ValidationException("request body must be a JSON object");
        }

        var digest = Json.AsString(body["digest"]);
        if (digest is null || PyText.Length(digest) != 64)
        {
            throw new ValidationException("digest is required: the digest of the filing set you were shown");
        }

        var credential = body["credential_id"];
        if (credential is not null && Json.AsString(credential) is null)
        {
            throw new ValidationException("credential_id must be a string");
        }

        return (digest, Json.AsString(credential));
    }

    /// <summary>The caller's OWN court login that would file this case, and the authorization it stands on.</summary>
    private static async Task<(FilingCredential Credential, string AuthorizationId)> CredentialForAsync(
        string court,
        string firmId,
        string attorneyId,
        string? credentialId,
        IFilingCredentialStore credentials,
        IFilingAuthorizationStore authorizations,
        CancellationToken cancellationToken)
    {
        var authorization = await authorizations.GetCurrentAsync(firmId, attorneyId, cancellationToken).ConfigureAwait(false);
        if (authorization is null || !FilingAuthorizationRules.IsCurrent(authorization, firmId, attorneyId))
        {
            throw new AuthorizationRequiredException("Sign the filing authorization before approving a filing.");
        }

        var usable = (await credentials.ListForAttorneyAsync(firmId, attorneyId, cancellationToken).ConfigureAwait(false))
            .Where(credential => credential.Courts.Contains(court) && FilingCredentialRules.IsOpenable(credential, authorization))
            .Where(credential => credentialId is null || credential.CredentialId == credentialId)
            .ToList();
        if (usable.Count == 0)
        {
            throw new ApprovalNotPermittedException("You have no court login stored for this court. Only the attorney whose login would file the case may approve it.");
        }

        if (usable.Count > 1)
        {
            throw new ConflictException("You have more than one login for this court — say which one files it.");
        }

        return (usable[0], authorization.AuthorizationId);
    }

    /// <summary>A clock that reads the caller's one "now".</summary>
    private sealed class At(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
