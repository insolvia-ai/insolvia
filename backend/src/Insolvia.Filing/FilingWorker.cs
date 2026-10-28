using System.IO.Compression;
using System.Security.Cryptography;
using Insolvia.Core;
using Insolvia.Core.AccessLog;
using Insolvia.Core.Cases;
using Insolvia.Core.Documents;
using Insolvia.Core.FilingCredentials;
using Insolvia.Core.Filings;
using Insolvia.Core.Ports;
using Insolvia.Filing.Drivers;
using Insolvia.Filing.Fence;
using Insolvia.Filing.Http;
using Insolvia.Forms;

namespace Insolvia.Filing;

/// <summary>Everything one run reads and writes — composed once per host (<see cref="FilingComposition"/>).</summary>
public sealed record FilingDeps
{
    /// <summary><c>local</c>, <c>staging</c> or <c>production</c>.</summary>
    public required string Environment { get; init; }

    /// <summary>The filing records.</summary>
    public required IFilingStore Filings { get; init; }

    /// <summary>The approvals.</summary>
    public required IFilingApprovalStore Approvals { get; init; }

    /// <summary>The cases (the worker's read: no accessor, no access rule).</summary>
    public required ICaseStore Cases { get; init; }

    /// <summary>The debtors.</summary>
    public required IDebtorStore Debtors { get; init; }

    /// <summary>The case records.</summary>
    public required ICaseEntityStore Entities { get; init; }

    /// <summary>The packets, newest first.</summary>
    public required IPacketReader Packets { get; init; }

    /// <summary>The case-document bucket.</summary>
    public required IDocumentBlobStore Blobs { get; init; }

    /// <summary>The documents (the receipt's row).</summary>
    public required IDocumentStore Documents { get; init; }

    /// <summary>The vault's credentials.</summary>
    public required IFilingCredentialStore Credentials { get; init; }

    /// <summary>The signed authorizations.</summary>
    public required IFilingAuthorizationStore Authorizations { get; init; }

    /// <summary>THE OPENER — composed in this host alone (ADR 0024, guardrail 3).</summary>
    public required IFilingCredentialOpener Opener { get; init; }

    /// <summary>The access log.</summary>
    public required IAccessLog AccessLog { get; init; }

    /// <summary>The kill switch, read fresh each time.</summary>
    public required IKillSwitch KillSwitch { get; init; }

    /// <summary>This environment's fence.</summary>
    public required HostFence Fence { get; init; }

    /// <summary>A fresh, fenced HTTP client per run (one session's cookies).</summary>
    public required Func<ICourtHttpClient> Http { get; init; }

    /// <summary>The log.</summary>
    public required IFilingLog Log { get; init; }

    /// <summary>The local composition's fake driver; null everywhere deployed.</summary>
    public Func<ICourtDriver>? FakeDriver { get; init; }

    /// <summary>
    /// The sealed tax ids — read by the Case Upload file's logged full-value
    /// read (ADR 0024 PR 9, purpose <see cref="CaseUploads.TaxIdPurpose"/>),
    /// and by nothing else in this worker.
    /// </summary>
    public required ITaxIdStore TaxIds { get; init; }

    /// <summary>The case key's cipher, OPENING only, under the tax-id encryption context.</summary>
    public required ITaxIdCipher TaxIdCipher { get; init; }

    /// <summary>The record's clock: claims, leases, the approval's expiry.</summary>
    public TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>The COURT's clock — a TOTP code is valid for the court's 30-second step, which is wall time.</summary>
    public TimeProvider WallClock { get; init; } = TimeProvider.System;

    /// <summary>How long one attempt may hold a claim (the Lambda's timeout).</summary>
    public int LeaseSeconds { get; init; } = FilingConfig.DefaultLeaseSeconds;
}

/// <summary>What one message came to: a filing state, <c>dropped</c> (no such approval) or <c>noop</c> (a redelivery with nothing to do).</summary>
/// <param name="Outcome">The outcome.</param>
/// <param name="FilingId">The filing, when known.</param>
/// <param name="Reason">Why, when there is one.</param>
public sealed record FilingResult(string Outcome, string? FilingId = null, string? Reason = null);

/// <summary>
/// THE FILING WORKER — one filing job, start to end (from
/// <c>core/worker.py</c>, ADR 0024 PR 7). Each numbered step goes on or
/// STOPS, and a stop is final:
/// <list type="number">
/// <item>Parse the message; find its approval; one naming no approval of that filing is dropped (nothing written).</item>
/// <item>A RECORD EXISTS — a redelivery. Never a second run: terminal → nothing; <c>at_final_submit</c> →
/// <c>outcome_unknown</c> (never retried); <c>submitted</c> → finish the capture (no court); before the mark →
/// within the lease leave it, past it hand back <c>interrupted</c>.</item>
/// <item>CLAIM: create the record (<c>claimed</c>), conditional on there being none.</item>
/// <item>CONSUME the approval over the digest recomputed from the stores (Forms' <see cref="FilingApprovals"/>).</item>
/// <item>The kill switch. 6. The driver (none for every real court today). 7. The fence for every origin — all BEFORE the credential opens.</item>
/// <item>The documents, each checked against the SHA-256 the approval bound; for a Case Upload court, Debtor.txt built first
/// (<see cref="Run.CaseUploadAsync"/>) — a file that cannot be built stops here, before the credential.</item>
/// <item>OPEN the credential (logged before decrypt), sign in (TOTP at the court's ask) → signed_in; open → uploading; upload.</item>
/// <item>RE-CHECK immediately before the final submit: kill switch, credential (opened again), approval, digest.</item>
/// <item>MARK <c>at_final_submit</c>, durably, BEFORE the click. 12. SUBMIT — anything but a confirmation is <c>outcome_unknown</c>.</item>
/// <item><c>submitted</c> with the court's facts; then the receipt and THE CASE FILED (<see cref="CaseLifecycle.FileCase"/>) in the same write as <c>filed</c>.</item>
/// </list>
/// Every state change is <see cref="Run.MoveAsync"/>, conditional on (state, attempt). The password and
/// seed live in one frame and reach no log, record or exception.
/// </summary>
public static class FilingWorker
{
    /// <summary>The access rows' purposes.</summary>
    public const string PurposeSignIn = "sign_in";

    /// <summary>The re-check's purpose.</summary>
    public const string PurposeRecheck = "final_submit_recheck";

    /// <summary>The digest read's purpose.</summary>
    public const string PurposeDigest = "filing_digest";

    /// <summary>The follow-up every submitted filing carries: the fee, paid by the attorney.</summary>
    public const string FollowUpFee = "pay_fee";

    /// <summary>How many times the capture re-reads a case that moved under its write.</summary>
    public const int CaptureAttempts = 3;

    /// <summary>
    /// One filing job, from its queue message body. Returns on every outcome a
    /// run can decide; an exception escapes only before a record exists (a
    /// store unreachable at claim time — an SQS retry is then safe) or when the
    /// host is torn down (<see cref="OperationCanceledException"/>: the record
    /// stays where it stood, and the redelivery resumes through step 2).
    /// </summary>
    public static async Task<FilingResult> RunAsync(string body, FilingDeps deps, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deps);
        FilingJob job;
        try
        {
            job = FilingJobs.Parse(body);
        }
        catch (ValidationException)
        {
            deps.Log.Write("WARNING", "dropped a message that is not a filing job");
            return new FilingResult("dropped", Reason: "not_a_filing_job");
        }

        var approval = await deps.Approvals.GetAsync(job.CaseId, job.ApprovalId, cancellationToken).ConfigureAwait(false);
        if (approval is null || approval.FilingId != job.FilingId)
        {
            deps.Log.Write("WARNING", "dropped a filing job naming no approval of that filing", ("case_id", job.CaseId), ("filing_id", job.FilingId));
            return new FilingResult("dropped", job.FilingId, "no_such_approval");
        }

        var run = new Run(deps, approval);
        var existing = await deps.Filings.GetAsync(approval.CaseId, approval.FilingId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            run.Filing = existing;
            return await ResumeAsync(run, cancellationToken).ConfigureAwait(false);
        }

        var now = Clock.NowMillis(deps.Clock);
        var claimed = new Core.Filings.Filing(
            approval.FilingId,
            approval.CaseId,
            approval.ApprovalId,
            approval.FirmId,
            approval.AttorneyId,
            approval.CredentialId,
            approval.Court,
            approval.Division,
            FilingState.Claimed,
            Guid.NewGuid().ToString(),
            now,
            now.ToUnixTimeSeconds() + deps.LeaseSeconds,
            now)
        {
            History = [new FilingStep(FilingState.Claimed, now)],
        };
        if (!await deps.Filings.ClaimAsync(claimed, cancellationToken).ConfigureAwait(false))
        {
            // Another consumer claimed it between our read and our write.
            return new FilingResult("noop", approval.FilingId, "claimed_elsewhere");
        }

        run.Filing = claimed;
        deps.Log.Write("INFO", "filing claimed", ("filing_id", claimed.FilingId), ("case_id", claimed.CaseId));
        try
        {
            return await FileAsync(run, cancellationToken).ConfigureAwait(false);
        }
        catch (StopException stop)
        {
            return await StopBeforeSubmitAsync(run, stop.Reason, stop.CourtSaid, cancellationToken).ConfigureAwait(false);
        }
        catch (HandBackException stop)
        {
            return await StopBeforeSubmitAsync(run, stop.Reason, stop.CourtSaid, cancellationToken).ConfigureAwait(false);
        }
        catch (HostNotAllowedException)
        {
            return await StopBeforeSubmitAsync(run, "host_not_allowed", null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException && run.Filing is { } current && FilingRules.BeforeSubmit.Contains(current.State))
        {
            // Before the mark an unexpected error is still a hand-back: nothing
            // reached the court, and the worker never retries a filing. The
            // exception's TYPE is logged, never its message (it could quote input).
            deps.Log.Write("ERROR", "filing worker error before the final submit", ("filing_id", current.FilingId), ("error", error.GetType().Name));
            return await run.HandBackAsync("worker_error", null, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<FilingResult> StopBeforeSubmitAsync(Run run, string reason, string? courtSaid, CancellationToken cancellationToken)
    {
        var current = run.Filing!;
        if (!FilingRules.BeforeSubmit.Contains(current.State))
        {
            // Cannot happen by construction; refuse to dress an after-mark state as a hand-back.
            throw new InvalidOperationException($"a hand-back requested in {current.State.ToWire()}");
        }

        return await run.HandBackAsync(reason, courtSaid, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Step 2: a record exists. Never starts a second filing.</summary>
    private static async Task<FilingResult> ResumeAsync(Run run, CancellationToken cancellationToken)
    {
        var filing = run.Filing!;
        if (FilingRules.Terminal.Contains(filing.State))
        {
            return new FilingResult("noop", filing.FilingId, filing.State.ToWire());
        }

        if (filing.State == FilingState.AtFinalSubmit)
        {
            return await run.OutcomeUnknownAsync("interrupted_after_submit_mark", null, cancellationToken).ConfigureAwait(false);
        }

        if (filing.State == FilingState.Submitted)
        {
            return await CaptureAsync(run, cancellationToken).ConfigureAwait(false);
        }

        if (FilingApprovalRecords.Seconds(run.Deps.Clock.GetUtcNow()) < filing.LeaseExpiresAt)
        {
            return new FilingResult("noop", filing.FilingId, "attempt_in_flight");
        }

        return await run.HandBackAsync("interrupted", null, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<FilingResult> FileAsync(Run run, CancellationToken cancellationToken)
    {
        var deps = run.Deps;
        var approval = run.Approval;

        // 4. consume — the shared rule over the shared digest.
        var basis = await run.BasisAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await FilingApprovals.ConsumeAsync(approval.CaseId, approval.ApprovalId, basis, deps.Clock.GetUtcNow(), deps.Approvals, deps.AccessLog, cancellationToken).ConfigureAwait(false);
        }
        catch (ApprovalUnavailableException refused)
        {
            deps.Log.Write("INFO", "approval refused at consume", ("filing_id", approval.FilingId), ("refusal", refused.Reason));
            throw new StopException("approval_unavailable");
        }

        // 5. the kill switch.
        if (!await deps.KillSwitch.SubmissionsEnabledAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new StopException("submissions_disabled");
        }

        // 6. the driver.
        var driver = CourtDrivers.Resolve(approval.Court, deps.Environment, deps.FakeDriver) ?? throw new StopException("court_not_verified");

        // 7. the fence, before anything is decrypted.
        foreach (var origin in driver.BaseUrls)
        {
            deps.Fence.Check(origin);
        }

        // 8. the documents, checked against the approval.
        var (files, followUps) = await run.FilesAsync(basis, cancellationToken).ConfigureAwait(false);
        if (driver.CaseUpload)
        {
            files = [await run.CaseUploadAsync(cancellationToken).ConfigureAwait(false), .. files];
        }

        var joint = (await deps.Debtors.ListForCaseAsync(approval.CaseId, cancellationToken).ConfigureAwait(false)).Count > 1;

        // 9. sign in, open, upload.
        var session = driver.Start(deps.Http());
        await SignInAsync(run, session, cancellationToken).ConfigureAwait(false);
        if (!await run.MoveAsync(FilingState.SignedIn, filing => filing with { Driver = driver.DriverId }, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            return new FilingResult("noop", approval.FilingId, "race");
        }

        var chapter = (await deps.Cases.ReadForWorkerAsync(approval.CaseId, cancellationToken).ConfigureAwait(false))?.Chapter ?? 0;
        await session.OpenCaseAsync(new CaseOpening(approval.Court, approval.Division, chapter, joint), cancellationToken).ConfigureAwait(false);
        if (!await run.MoveAsync(FilingState.Uploading, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            return new FilingResult("noop", approval.FilingId, "race");
        }

        await session.UploadAsync(files, cancellationToken).ConfigureAwait(false);

        // 10. re-check everything, immediately before the final submit.
        if (!await deps.KillSwitch.SubmissionsEnabledAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new StopException("submissions_disabled");
        }

        await run.OpenSecretAsync(PurposeRecheck, cancellationToken).ConfigureAwait(false);
        var stored = await deps.Approvals.GetAsync(approval.CaseId, approval.ApprovalId, cancellationToken).ConfigureAwait(false);
        if (stored is null || stored.Status != ApprovalStatus.Consumed || stored.FilingId != approval.FilingId || stored.Digest != approval.Digest)
        {
            throw new StopException("approval_unavailable");
        }

        if ((await run.BasisAsync(cancellationToken).ConfigureAwait(false)).Digest != approval.Digest)
        {
            throw new StopException("changed_after_approval");
        }

        // 11. the mark, durably, before the click.
        if (!await run.MoveAsync(FilingState.AtFinalSubmit, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            return new FilingResult("noop", approval.FilingId, "race");
        }

        // 12. the click. From here nothing is ever retried.
        CourtConfirmation confirmation;
        try
        {
            confirmation = await session.FinalSubmitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SubmitUncertainException uncertain)
        {
            return await run.OutcomeUnknownAsync(uncertain.Reason, uncertain.CourtSaid, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            deps.Log.Write("ERROR", "final submit failed", ("filing_id", approval.FilingId), ("error", error.GetType().Name));
            return await run.OutcomeUnknownAsync("submit_error", null, cancellationToken).ConfigureAwait(false);
        }

        return await SubmittedAsync(run, confirmation, followUps, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Open the credential and sign in. The password and the seed are locals
    /// of THIS frame: the TOTP is computed inside the court's ask, and both
    /// die when this returns.
    /// </summary>
    private static async Task SignInAsync(Run run, ICourtSession session, CancellationToken cancellationToken)
    {
        var (login, secret) = await run.OpenSecretAsync(PurposeSignIn, cancellationToken).ConfigureAwait(false);
        var wall = run.Deps.WallClock;
        await session.SignInAsync(login, secret.Password, () => Totp.At(secret.TotpSeed, wall.GetUtcNow()), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>13. Record what the court said the moment it was read — then capture.</summary>
    private static async Task<FilingResult> SubmittedAsync(Run run, CourtConfirmation confirmation, IReadOnlyList<string> followUps, CancellationToken cancellationToken)
    {
        var deps = run.Deps;
        var filing = run.Filing!;
        string? pageRef = null;
        try
        {
            var reference = Receipts.ConfirmationRef(filing.CaseId, filing.FilingId);
            await deps.Blobs.PutBytesAsync(reference, confirmation.Page, Receipts.ConfirmationContentType, cancellationToken).ConfigureAwait(false);
            pageRef = reference;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // A page that cannot be stored is not a reason to lose the case number.
            deps.Log.Write("ERROR", "could not store the confirmation page", ("filing_id", filing.FilingId), ("error", error.GetType().Name));
        }

        var captured = new Confirmation(confirmation.CaseNumber, confirmation.FiledAt, [.. confirmation.DocketEntries], confirmation.ReceiptNumber, confirmation.FeeDue, pageRef);
        if (!await run.MoveAsync(FilingState.Submitted, f => f with { Confirmation = captured, FollowUps = [FollowUpFee, .. followUps] }, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            return new FilingResult("noop", filing.FilingId, "race");
        }

        await run.RecordAsync(AccessAction.FilingSubmit, "submitted", cancellationToken).ConfigureAwait(false);
        return await CaptureAsync(run, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// submitted → filed: the receipt stored with the case, and THE CASE FILED
    /// (ADR 0024 PR 8; ADR 0027's lifecycle writer) in the same conditional
    /// write that moves the record. Touches no court, so a redelivery may
    /// finish it, and every step is idempotent. A case already filed by hand is
    /// left as written; one that moved underneath is read again up to
    /// <see cref="CaptureAttempts"/> times; anything that stops the case being
    /// recorded ends <c>outcome_unknown</c> with the confirmation kept.
    /// </summary>
    private static async Task<FilingResult> CaptureAsync(Run run, CancellationToken cancellationToken)
    {
        var deps = run.Deps;
        var filing = run.Filing!;
        Document document;
        try
        {
            document = await StoreReceiptAsync(run, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            deps.Log.Write("ERROR", "could not store the filing receipt", ("filing_id", filing.FilingId), ("error", error.GetType().Name));
            return await run.OutcomeUnknownAsync("capture_interrupted", null, cancellationToken).ConfigureAwait(false);
        }

        for (var attempt = 0; attempt < CaptureAttempts; attempt++)
        {
            LifecycleWrite? filedCase;
            try
            {
                filedCase = await FiledCaseAsync(run, cancellationToken).ConfigureAwait(false);
            }
            catch (CaseNotRecordedException)
            {
                return await run.OutcomeUnknownAsync("case_not_recorded", null, cancellationToken).ConfigureAwait(false);
            }

            if (await run.MoveAsync(FilingState.Filed, f => f with { ReceiptDocumentId = document.Id }, filedCase, cancellationToken).ConfigureAwait(false))
            {
                await run.RecordAsync(AccessAction.FilingSubmit, filedCase is not null ? "case_filed" : "filed", cancellationToken).ConfigureAwait(false);
                return new FilingResult("filed", filing.FilingId);
            }

            var stored = await deps.Filings.GetAsync(filing.CaseId, filing.FilingId, cancellationToken).ConfigureAwait(false);
            if (stored is null || stored.State != run.Filing!.State || stored.AttemptId != run.Filing.AttemptId)
            {
                // The record moved: whoever moved it decided.
                return new FilingResult("noop", filing.FilingId, "race");
            }

            // The record is still ours, so the CASE moved under the write: read it again.
        }

        return await run.OutcomeUnknownAsync("case_not_recorded", null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The receipt PDF and its Document row — once; a redelivery reuses the first run's row.</summary>
    private static async Task<Document> StoreReceiptAsync(Run run, CancellationToken cancellationToken)
    {
        var deps = run.Deps;
        var filing = run.Filing!;
        var content = FilingReceipt.Render(filing, filing.Confirmation!);
        var document = Receipts.ReceiptDocument(filing, content, deps.Clock);
        if (await deps.Documents.GetAsync(filing.CaseId, document.Id, cancellationToken).ConfigureAwait(false) is { } existing)
        {
            return existing;
        }

        await deps.Blobs.PutBytesAsync(document.StorageRef, content, Receipts.ReceiptContentType, cancellationToken).ConfigureAwait(false);
        await deps.Documents.CreateAsync(document, cancellationToken).ConfigureAwait(false);
        return document;
    }

    /// <summary>The case half of the <c>filed</c> write, read now: null when there is no case to move (already filed, or gone).</summary>
    private static async Task<LifecycleWrite?> FiledCaseAsync(Run run, CancellationToken cancellationToken)
    {
        var deps = run.Deps;
        var filing = run.Filing!;
        var confirmation = filing.Confirmation!;
        var @case = await deps.Cases.ReadForWorkerAsync(filing.CaseId, cancellationToken).ConfigureAwait(false);
        if (@case is null || @case.DeletedAt is not null || CaseRules.IsFiled(@case.Status))
        {
            return null;
        }

        var filedOn = CaseNumbers.PetitionDate(confirmation.FiledAt);
        if (filedOn is null || CaseNumbers.Parse(confirmation.CaseNumber, requireOffice: true) is null)
        {
            deps.Log.Write("WARNING", "the court's confirmation cannot be recorded on the case", ("filing_id", filing.FilingId));
            throw new CaseNotRecordedException();
        }

        var history = await deps.Cases.LifecycleAsync(filing.CaseId, cancellationToken).ConfigureAwait(false);
        try
        {
            return CaseLifecycle.FileCase(@case, history, confirmation.CaseNumber, filedOn, CaseRules.FilingWorkerActor, filing.FilingId, clock: deps.Clock);
        }
        catch (ApiException refused)
        {
            throw new CaseNotRecordedException(refused.GetType().Name, refused);
        }
    }

    /// <summary>One message's run: the current record, and every transition through <see cref="MoveAsync"/>.</summary>
    private sealed class Run(FilingDeps deps, FilingApproval approval)
    {
        public FilingDeps Deps { get; } = deps;

        public FilingApproval Approval { get; } = approval;

        public Core.Filings.Filing? Filing { get; set; }

        /// <summary>
        /// Advance the record, conditional on the state and attempt this run
        /// last saw — and, with <paramref name="filedCase"/>, file the case in
        /// the same transaction. False when anything else moved first.
        /// </summary>
        public async Task<bool> MoveAsync(FilingState state, Func<Core.Filings.Filing, Core.Filings.Filing>? change = null, LifecycleWrite? filedCase = null, CancellationToken cancellationToken = default)
        {
            var current = Filing!;
            if (!FilingRules.MayTransition(current.State, state))
            {
                throw new InvalidOperationException($"no transition {current.State.ToWire()} -> {state.ToWire()}");
            }

            var now = Clock.NowMillis(Deps.Clock);
            var moved = current with { State = state, UpdatedAt = now, History = [.. current.History, new FilingStep(state, now)] };
            moved = change?.Invoke(moved) ?? moved;
            if (!await Deps.Filings.TransitionAsync(moved, current.State, filedCase, cancellationToken).ConfigureAwait(false))
            {
                Deps.Log.Write("WARNING", "filing transition lost a race; stopping", ("filing_id", current.FilingId), ("to", state.ToWire()));
                return false;
            }

            Filing = moved;
            Deps.Log.Write("INFO", "filing state", ("filing_id", moved.FilingId), ("case_id", moved.CaseId), ("state", state.ToWire()));
            return true;
        }

        public async Task<FilingResult> HandBackAsync(string reason, string? courtSaid, CancellationToken cancellationToken)
        {
            var current = Filing!;
            if (!await MoveAsync(FilingState.HandedBack, f => f with { HandBack = HandBackReasons.HandBackNote(reason, current.State, courtSaid) }, cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                // Something else moved the record first: it decided, not this run.
                return new FilingResult("noop", current.FilingId, "race");
            }

            await RecordAsync(AccessAction.FilingHandBack, reason, cancellationToken).ConfigureAwait(false);
            return new FilingResult("handed_back", current.FilingId, reason);
        }

        public async Task<FilingResult> OutcomeUnknownAsync(string reason, string? courtSaid, CancellationToken cancellationToken)
        {
            var current = Filing!;
            if (!await MoveAsync(
                    FilingState.OutcomeUnknown,
                    f => f with { UnknownReason = reason, HandBack = HandBackReasons.OutcomeUnknownNote(reason, current.State, courtSaid) },
                    cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                return new FilingResult("noop", current.FilingId, "race");
            }

            await RecordAsync(AccessAction.FilingSubmit, "outcome_unknown:" + reason, cancellationToken).ConfigureAwait(false);
            return new FilingResult("outcome_unknown", current.FilingId, reason);
        }

        public Task RecordAsync(AccessAction action, string purpose, CancellationToken cancellationToken)
        {
            var current = Filing!;
            return Deps.AccessLog.RecordAsync(AccessEvents.Record(current.AttorneyId, action, caseId: current.CaseId, purpose: purpose, filingId: current.FilingId, clock: Deps.Clock), cancellationToken);
        }

        /// <summary>The approval's digest, recomputed from the stores now — with the API's own composition.</summary>
        public async Task<ApprovalBasis> BasisAsync(CancellationToken cancellationToken)
        {
            var @case = await Deps.Cases.ReadForWorkerAsync(Approval.CaseId, cancellationToken).ConfigureAwait(false);
            await Deps.AccessLog.RecordAsync(
                AccessEvents.Record(Approval.AttorneyId, AccessAction.CaseRead, caseId: Approval.CaseId, outcome: @case is not null ? AccessOutcome.Allowed : AccessOutcome.Denied, purpose: PurposeDigest, filingId: Approval.FilingId, clock: Deps.Clock),
                cancellationToken).ConfigureAwait(false);
            if (@case is null)
            {
                throw new StopException("approval_unavailable");
            }

            var (_, basis) = await FilingApprovals.BasisForCaseAsync(@case, Deps.Debtors, Deps.Entities, Deps.Packets, Clock.Today(Deps.Clock), cancellationToken: cancellationToken).ConfigureAwait(false);
            return basis;
        }

        /// <summary>
        /// Debtor.txt, built now from the stores — the records the approval's
        /// digest binds — with the full SSNs from the logged read (one
        /// <c>taxid.read</c> per debtor, principal the approving attorney). In
        /// memory only: returned for the upload, never stored, never logged. A
        /// file that cannot be built or does not validate is a stop, BEFORE the
        /// credential opens; the problems name fields, never values, and only
        /// their codes are logged.
        /// </summary>
        public async Task<FilingFile> CaseUploadAsync(CancellationToken cancellationToken)
        {
            var @case = await Deps.Cases.ReadForWorkerAsync(Approval.CaseId, cancellationToken).ConfigureAwait(false);
            await Deps.AccessLog.RecordAsync(
                AccessEvents.Record(Approval.AttorneyId, AccessAction.CaseRead, caseId: Approval.CaseId, outcome: @case is not null ? AccessOutcome.Allowed : AccessOutcome.Denied, purpose: CaseUploads.TaxIdPurpose, filingId: Approval.FilingId, clock: Deps.Clock),
                cancellationToken).ConfigureAwait(false);
            if (@case is null)
            {
                throw new StopException("approval_unavailable");
            }

            CaseUploadFile built;
            try
            {
                built = await CaseUploads.ForFilingAsync(
                    @case, Approval.AttorneyId, Clock.Today(Deps.Clock), Deps.Debtors, Deps.Entities, Deps.TaxIds, Deps.TaxIdCipher, Deps.AccessLog, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (CaseUploadException refused)
            {
                var codes = refused.Problems.Select(p => $"{p.Record}.{p.Field}:{p.Code}").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
                Deps.Log.Write("INFO", "case upload file refused", ("filing_id", Approval.FilingId), ("problems", string.Join(",", codes)));
                throw new StopException("case_upload_invalid");
            }

            return new FilingFile(0, CaseUploads.DocumentKey, built.FileName, CaseUploads.DocumentKey, built.Content, built.Sha256);
        }

        /// <summary>
        /// (login, secret) — or a stop. The <c>credential.open</c> row is
        /// written by <see cref="FilingCredentialRules.OpenAsync"/> itself, before it reads anything.
        /// </summary>
        public async Task<(string Login, CredentialSecret Secret)> OpenSecretAsync(string purpose, CancellationToken cancellationToken)
        {
            CredentialSecret secret;
            try
            {
                secret = await FilingCredentialRules.OpenAsync(
                    Approval.FirmId, Approval.AttorneyId, Approval.CredentialId, Approval.FilingId, purpose, Deps.Opener, Deps.Credentials, Deps.Authorizations, Deps.AccessLog, Deps.Clock, cancellationToken).ConfigureAwait(false);
            }
            catch (CredentialUnavailableException)
            {
                throw new StopException("credential_unavailable");
            }

            var credential = await Deps.Credentials.GetAsync(Approval.FirmId, Approval.AttorneyId, Approval.CredentialId, cancellationToken).ConfigureAwait(false)
                ?? throw new StopException("credential_unavailable");
            return (credential.Login, secret);
        }

        /// <summary>
        /// The documents to upload, in docket order, their bytes checked against
        /// the approval; and the follow-ups for what the court takes separately.
        /// </summary>
        public async Task<(IReadOnlyList<FilingFile> Files, IReadOnlyList<string> FollowUps)> FilesAsync(ApprovalBasis basis, CancellationToken cancellationToken)
        {
            var packet = basis.FilingSet.Packet;
            if (packet is null || packet.Id != Approval.PacketId)
            {
                throw new StopException("approval_unavailable");
            }

            var followUps = new List<string>();
            var wanted = new List<(int Position, FilingDocument Document)>();
            for (var index = 0; index < basis.FilingSet.Documents.Count; index++)
            {
                var document = basis.FilingSet.Documents[index];
                if (document.Handling == "not_filed")
                {
                    continue;
                }

                if (document.Source == "outside")
                {
                    if (document.Handling == "file")
                    {
                        throw new StopException("outside_document_required");
                    }

                    followUps.Add("file_separately:" + document.Key);
                    continue;
                }

                wanted.Add((index + 1, document));
            }

            var content = await Deps.Blobs.GetBytesAsync(packet.StorageRef, cancellationToken).ConfigureAwait(false);
            if (content is null || Convert.ToHexStringLower(SHA256.HashData(content)) != packet.Sha256)
            {
                throw new StopException("document_mismatch");
            }

            return (Extract(content, wanted), followUps);
        }

        private static List<FilingFile> Extract(byte[] content, List<(int Position, FilingDocument Document)> wanted)
        {
            var files = new List<FilingFile>();
            using var archive = new ZipArchive(new MemoryStream(content), ZipArchiveMode.Read);
            foreach (var (position, document) in wanted)
            {
                var part = document.Part;
                var entry = part is null ? null : archive.GetEntry(part.Name);
                if (part?.Sha256 is null || entry is null)
                {
                    throw new StopException("document_mismatch");
                }

                using var stream = entry.Open();
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                var data = buffer.ToArray();
                if (Convert.ToHexStringLower(SHA256.HashData(data)) != part.Sha256)
                {
                    throw new StopException("document_mismatch");
                }

                files.Add(new FilingFile(position, document.Key, document.FileName, document.Handling, data, part.Sha256));
            }

            return files;
        }
    }

    /// <summary>Internal: a hand-back decided by the worker itself.</summary>
    private sealed class StopException(string reason, string? courtSaid = null) : Exception(reason)
    {
        public string Reason { get; } = reason;

        public string? CourtSaid { get; } = courtSaid;
    }

    /// <summary>The court's confirmation cannot be written onto the case.</summary>
    private sealed class CaseNotRecordedException : Exception
    {
        public CaseNotRecordedException()
        {
        }

        public CaseNotRecordedException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
