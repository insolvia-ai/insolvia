using System.Globalization;
using System.Text.Json.Nodes;
using Insolvia.Core.AccessLog;
using Insolvia.Core.Cases;
using Insolvia.Core.Documents;
using Insolvia.Core.Ports;

namespace Insolvia.Core.Filings;

/// <summary>A parsed resolution body.</summary>
/// <param name="Outcome">What the docket shows.</param>
/// <param name="CaseNumber">The court's number, for <c>filed</c>.</param>
/// <param name="FiledAt">The petition date, for <c>filed</c>.</param>
/// <param name="ConfirmationDocumentId">The court's notice uploaded as a case document, optionally.</param>
public sealed record ResolutionRequest(ResolutionOutcome Outcome, string? CaseNumber = null, DateOnly? FiledAt = null, string? ConfirmationDocumentId = null);

/// <summary>The filing cannot be resolved this way (409), with the reason.</summary>
public sealed class ResolutionRefusedException : ConflictException
{
    /// <summary>Creates the exception with no message.</summary>
    public ResolutionRefusedException()
    {
        Reason = string.Empty;
    }

    /// <summary>Creates the exception with a message.</summary>
    public ResolutionRefusedException(string message)
        : base(message)
    {
        Reason = string.Empty;
    }

    /// <summary>Creates the exception with a message and its cause.</summary>
    public ResolutionRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
        Reason = string.Empty;
    }

    /// <summary>Creates the exception naming its reason.</summary>
    public ResolutionRefusedException(string reason, string message)
        : base(message)
    {
        Reason = reason;
    }

    /// <summary>Why (<c>not_resolvable</c>, <c>court_confirmed</c>, <c>attempt_may_be_live</c>, <c>case_filed</c>).</summary>
    public string Reason { get; }
}

/// <summary>Only the attorney whose login the filing used may resolve it.</summary>
public sealed class NotTheFilingAttorneyException : ForbiddenException
{
    /// <summary>Creates the exception with no message.</summary>
    public NotTheFilingAttorneyException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public NotTheFilingAttorneyException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and its cause.</summary>
    public NotTheFilingAttorneyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The attorney's answer to a hand-back (ADR 0024 PR 8; from
/// <c>core/filing_outcome.py</c>) — the ONE way out of a handed-back or
/// unknown-outcome filing, and the attorney's word about the court's own
/// docket. <c>filed</c> files the case in the same transaction as the
/// resolution; <c>not_filed</c> frees the case for a new approval.
/// </summary>
/// <remarks>
/// Why <c>not_filed</c> cannot cause a double filing: a captured
/// confirmation refuses it outright; a live attempt (lease not run out)
/// refuses it; the attorney must say they checked the docket; and a new
/// approval is still a new approval, under a filing id no worker has seen.
/// </remarks>
public static class FilingOutcomes
{
    /// <summary>The body's keys.</summary>
    public static readonly IReadOnlySet<string> AllowedKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "outcome", "docket_checked", "case_number", "filed_at", "confirmation_document_id",
    };

    // The petition date may be a day either side of the clock's: the court's
    // local date and UTC differ by up to one.
    private static readonly TimeSpan DateSlack = TimeSpan.FromDays(1);

    /// <summary>
    /// <c>POST …/filings/&lt;id&gt;/resolution</c>'s body: <c>{outcome,
    /// docket_checked, case_number?, filed_at?, confirmation_document_id?}</c>.
    /// </summary>
    public static ResolutionRequest Parse(JsonNode? payload)
    {
        if (payload is not JsonObject body)
        {
            throw new ValidationException("request body must be a JSON object");
        }

        var unknown = body.Select(pair => pair.Key).Where(key => !AllowedKeys.Contains(key)).Order(StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
        {
            throw new ValidationException("unsupported fields: " + string.Join(", ", unknown));
        }

        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var outcomeText = Json.AsString(body["outcome"]);
        ResolutionOutcome? outcome = outcomeText switch
        {
            "filed" => ResolutionOutcome.Filed,
            "not_filed" => ResolutionOutcome.NotFiled,
            _ => null,
        };
        if (outcome is null)
        {
            errors["outcome"] = "Outcome must be filed or not_filed.";
        }

        if (!(body["docket_checked"] is JsonValue checkedValue && checkedValue.TryGetValue<bool>(out var docketChecked) && docketChecked))
        {
            errors["docket_checked"] = "Confirm you checked the court's own docket for this debtor.";
        }

        string? caseNumber = null;
        DateOnly? filedAt = null;
        string? documentId = null;
        if (outcome == ResolutionOutcome.Filed)
        {
            var rawNumber = Json.AsString(body["case_number"]);
            if (rawNumber is null || PyText.Strip(rawNumber).Length == 0)
            {
                errors["case_number"] = "Enter the case number the court's docket shows.";
            }
            else if (PyText.Length(rawNumber) > 40 || CaseNumbers.Parse(rawNumber, requireOffice: true) is null)
            {
                errors["case_number"] = "Enter the case number as the docket prints it, e.g. 6:26-bk-10000.";
            }
            else
            {
                caseNumber = PyText.Strip(rawNumber);
            }

            filedAt = FormDate(body["filed_at"]);
            if (filedAt is null)
            {
                errors["filed_at"] = "Enter the petition date in YYYY-MM-DD form.";
            }

            if (body.ContainsKey("confirmation_document_id") && body["confirmation_document_id"] is { } rawDocument)
            {
                if (Json.AsString(rawDocument) is { Length: > 0 } id)
                {
                    documentId = id;
                }
                else
                {
                    errors["confirmation_document_id"] = "Must be a document id.";
                }
            }
        }
        else if (outcome == ResolutionOutcome.NotFiled)
        {
            foreach (var key in new[] { "case_number", "filed_at", "confirmation_document_id" })
            {
                if (body[key] is not null)
                {
                    errors[key] = "A filing that is not on the docket has no " + (key == "case_number" ? "case number." : "filing details.");
                }
            }
        }

        if (errors.Count > 0)
        {
            throw new FieldValidationException(errors);
        }

        return new ResolutionRequest(outcome!.Value, caseNumber, filedAt, documentId);
    }

    /// <summary>
    /// Record the attorney's resolution of <paramref name="filing"/>, and file
    /// <paramref name="case"/> with it when the outcome is <c>filed</c>.
    /// Refuses, in order — each a denied <c>filing.resolve</c> row naming the
    /// refusal: not the filing's attorney (403); not resolvable (409);
    /// <c>not_filed</c> over a captured confirmation, a live attempt or a filed
    /// case (409); <c>filed</c> with another number than the court's, an
    /// impossible petition date, or a document that is not this case's (400).
    /// </summary>
    public static async Task<Filing> ResolveAsync(
        ResolutionRequest request,
        Filing filing,
        Case @case,
        string principal,
        DateTimeOffset now,
        IFilingStore filings,
        ICaseStore cases,
        IDocumentStore documents,
        IAccessLog accessLog,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(filing);
        ArgumentNullException.ThrowIfNull(@case);
        ArgumentNullException.ThrowIfNull(filings);
        ArgumentNullException.ThrowIfNull(cases);
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(accessLog);

        Task Refuse(string reason) => accessLog.RecordAsync(
            AccessEvents.Record(principal, AccessAction.FilingResolve, caseId: @case.Id, outcome: AccessOutcome.Denied, purpose: reason, filingId: filing.FilingId),
            cancellationToken);

        if (principal != filing.AttorneyId)
        {
            await Refuse("not_the_attorney").ConfigureAwait(false);
            throw new NotTheFilingAttorneyException("Only the attorney whose court login this filing used can record what became of it.");
        }

        if (!FilingRules.IsResolvable(filing))
        {
            await Refuse("not_resolvable").ConfigureAwait(false);
            throw new ResolutionRefusedException(
                "not_resolvable",
                filing.Resolution is not null
                    ? "This filing's outcome is already recorded."
                    : "Only a filing that was handed back, or whose outcome is unknown, can be resolved.");
        }

        var confirmation = filing.Confirmation;
        if (request.Outcome == ResolutionOutcome.NotFiled)
        {
            if (confirmation is not null)
            {
                await Refuse("court_confirmed").ConfigureAwait(false);
                throw new ResolutionRefusedException("court_confirmed", $"The court confirmed this filing as case {confirmation.CaseNumber}. Record it as filed.");
            }

            if (filing.State == FilingState.OutcomeUnknown && FilingApprovalRecords.Seconds(now) < filing.LeaseExpiresAt)
            {
                await Refuse("attempt_may_be_live").ConfigureAwait(false);
                throw new ResolutionRefusedException(
                    "attempt_may_be_live",
                    "The filing attempt may still be waiting on the court. Check the docket again after " + FilingApprovalRecords.Iso(DateTimeOffset.FromUnixTimeSeconds(filing.LeaseExpiresAt)) + ".");
            }

            if (CaseRules.IsFiled(@case.Status))
            {
                await Refuse("case_filed").ConfigureAwait(false);
                throw new ResolutionRefusedException("case_filed", "This case is already recorded as filed.");
            }
        }

        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        if (request.Outcome == ResolutionOutcome.Filed)
        {
            var number = request.CaseNumber!;
            if (confirmation is not null
                && CaseNumbers.Key(filing.Court, confirmation.CaseNumber) is { } confirmed
                && confirmed != CaseNumbers.Key(filing.Court, number))
            {
                errors["case_number"] = $"The court confirmed this filing as case {confirmation.CaseNumber}; enter that number.";
            }

            var petition = request.FiledAt!.Value;
            var today = DateOnly.FromDateTime(now.UtcDateTime);
            var claimed = DateOnly.FromDateTime(filing.ClaimedAt.UtcDateTime);
            if (petition > today.AddDays(DateSlack.Days))
            {
                errors["filed_at"] = "The petition date cannot be in the future.";
            }
            else if (petition < claimed.AddDays(-DateSlack.Days))
            {
                errors["filed_at"] = "The petition date cannot be before this filing was approved.";
            }

            if (request.ConfirmationDocumentId is { } documentId)
            {
                var document = await documents.GetAsync(@case.Id, documentId, cancellationToken).ConfigureAwait(false);
                if (document is null || document.Status != DocumentStatus.Stored)
                {
                    errors["confirmation_document_id"] = "That is not an uploaded document of this case.";
                }
            }
        }

        if (errors.Count > 0)
        {
            await Refuse("invalid").ConfigureAwait(false);
            throw new FieldValidationException(errors);
        }

        var at = Clock.NowMillis(new FixedTime(now));
        var resolved = filing with
        {
            UpdatedAt = at,
            Resolution = new Resolution(request.Outcome, principal, at, at, request.CaseNumber, request.FiledAt, request.ConfirmationDocumentId),
        };
        LifecycleWrite? filedCase = null;
        if (request.Outcome == ResolutionOutcome.Filed && !CaseRules.IsFiled(@case.Status))
        {
            var history = await cases.LifecycleAsync(@case.Id, cancellationToken).ConfigureAwait(false);
            filedCase = CaseLifecycle.FileCase(
                @case,
                history,
                request.CaseNumber!,
                request.FiledAt!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                principal,
                filing.FilingId,
                LifecycleProvenance.Manual,
                new FixedTime(now));
        }

        if (!await filings.ResolveAsync(resolved, filing.State, filedCase, cancellationToken).ConfigureAwait(false))
        {
            await Refuse("changed").ConfigureAwait(false);
            throw new ConflictException("The filing or the case changed while this was being recorded — reload it and try again.");
        }

        await accessLog.RecordAsync(
            AccessEvents.Record(principal, AccessAction.FilingResolve, caseId: @case.Id, purpose: request.Outcome.ToWire(), filingId: filing.FilingId),
            cancellationToken).ConfigureAwait(false);
        return resolved;
    }

    private static DateOnly? FormDate(JsonNode? value)
    {
        var text = Json.AsString(value);
        if (text is null)
        {
            return null;
        }

        var stripped = PyText.Strip(text);
        return DateOnly.TryParseExact(stripped, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            && parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) == stripped
            ? parsed
            : null;
    }

    /// <summary>A clock that reads one instant — the caller's "now", passed down.</summary>
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
