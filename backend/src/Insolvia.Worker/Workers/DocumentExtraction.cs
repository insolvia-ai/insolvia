using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Insolvia.Core;
using Insolvia.Core.AccessLog;
using Insolvia.Core.Candidates;
using Insolvia.Core.CaseEntities;
using Insolvia.Core.Claims;
using Insolvia.Core.Creditors;
using Insolvia.Core.Documents;
using Insolvia.Core.Income;
using Insolvia.Core.Jobs;
using Insolvia.Core.Ports;

namespace Insolvia.Worker.Workers;

/// <summary>One record the coercion distilled, before identity: the validated payload and the extraction stream's extras.</summary>
public sealed record CandidateSpec(string EntityType, JsonObject Payload, double? Confidence, int? Page, string? Note = null);

/// <summary>The specs, and how many records were dropped as structurally wrong.</summary>
public sealed record CoercionOutcome(IReadOnlyList<CandidateSpec> Specs, int Skipped);

/// <summary>What the extraction worker composes. <see cref="Model"/> is null where the key is not configured.</summary>
public sealed record DocumentExtractionDeps(
    ICaseStore Cases,
    IDocumentStore Documents,
    IDocumentBlobStore Blobs,
    ICandidateStore Candidates,
    IAccessLog AccessLog,
    IExtractionModel? Model)
{
    /// <summary>The clock.</summary>
    public TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>The log.</summary>
    public JobLog Log { get; init; } = JobLog.Console;
}

/// <summary>
/// AI extraction (core/extraction.py; issues 8.7/8.8): a credit report or pay
/// stub into CANDIDATE records — never case data. Every candidate enters the
/// case only when a person accepts it (the review routes). Re-validated through
/// the same parsers every write uses; a record the model got structurally
/// wrong is dropped and counted. Cross-record links use candidate-id
/// indirection: a claim's <c>creditor_id</c> names the creditor CANDIDATE.
/// What leaves for the model is the document itself plus a fixed instruction —
/// no case data; every payload is scrubbed before it is stored, and account
/// numbers are coerced to last four structurally.
/// </summary>
public static partial class DocumentExtraction
{
    /// <summary>What the model API accepts as document input.</summary>
    public static readonly IReadOnlyList<DocumentContentType> ModelInputContentTypes = [DocumentContentType.Pdf, DocumentContentType.Jpeg, DocumentContentType.Png];

    /// <summary>The raw-byte ceiling for one extraction (base64 under the 32 MB request cap).</summary>
    public const long MaxDocumentBytes = 20 * 1024 * 1024;

    /// <summary>A runaway generation must not flood a review queue.</summary>
    public const int MaxCandidatesPerRun = 200;

    private const string SharedPromptRules = """
        Rules, all of them binding:
        - Extract only what the document actually shows. Never infer, estimate, or
          fill a value the page does not state; use null for anything absent.
        - Amounts are plain decimal strings with two places, like "1234.56" — no
          currency symbols, no thousands separators.
        - Dates are YYYY-MM-DD exactly as the document states them.
        - NEVER output a Social Security number, taxpayer identification number, or
          full account number — account numbers are last four digits only, even when
          the document prints more.
        - Give every record a page anchor (1-based) and a confidence between 0 and 1.
        - If the document is not actually what it was described as, answer with the
          empty result rather than forcing records out of the wrong document.

        """;

    /// <summary>The credit report's instructions — byte-identical to Python's (pinned by the parity test).</summary>
    public const string CreditReportSystemPrompt = """
        You are reading a consumer credit report uploaded as source material for a
        U.S. consumer bankruptcy filing. Extract every tradeline into the schema you
        were given: one creditor entry per distinct creditor (name and mailing
        address as the report shows them — collections and servicers count as the
        creditor currently holding the account), each carrying its claims (one per
        account/tradeline). Include open, delinquent, charged-off and collection
        accounts; skip inquiries, employment history and score commentary — they are
        not debts. Do not merge creditors that merely look similar; a human reviews
        duplicates.


        """ + SharedPromptRules;

    /// <summary>The pay stub's instructions.</summary>
    public const string PayStubSystemPrompt = """
        You are reading one or more pay stubs uploaded as income evidence for a U.S.
        consumer bankruptcy filing. Extract the employer (name and address as
        printed) and one pay-period record per stub in the document: the period's
        start and end, the pay date, gross and net pay for THE PERIOD (never the
        year-to-date column), and each itemised deduction line mapped onto the
        category list in the schema — put a line that fits no category under
        "other" and carry the stub's own wording in its description.


        """ + SharedPromptRules;

    private const string NullableString = """{"type": ["string", "null"]}""";
    private const string Page = """{"type": ["integer", "null"], "description": "1-based page of the document this record was read from, or null when unclear."}""";
    private const string Confidence = """{"type": "number", "description": "How certain you are this record is read correctly, 0-1."}""";
    private const string AddressSchema = """
        {"type": "object", "properties": {"line1": {"type": ["string", "null"]}, "line2": {"type": ["string", "null"]}, "city": {"type": ["string", "null"]}, "state": {"type": ["string", "null"]}, "postal_code": {"type": ["string", "null"]}, "raw": {"type": ["string", "null"], "description": "The address exactly as printed, when it does not split cleanly into the parts."}}, "required": ["line1", "line2", "city", "state", "postal_code", "raw"], "additionalProperties": false}
        """;

    /// <summary>The credit report's structured-output contract — a fresh copy each call.</summary>
    public static JsonObject CreditReportSchema() => (JsonObject)JsonNode.Parse(Fill("""
        {"type": "object", "properties": {"creditors": {"type": "array", "items": {"type": "object", "properties": {
          "name": {"type": "string", "description": "The creditor as the mailing matrix should print it — issuer or current servicer name."},
          "address": @AddressSchema@,
          "page": @Page@,
          "confidence": @Confidence@,
          "claims": {"type": "array", "items": {"type": "object", "properties": {
            "account_last4": {"type": ["string", "null"], "description": "Last four digits of the account number ONLY — never more."},
            "amount": {"type": ["string", "null"], "description": "Balance owed as a plain decimal string, like \"1234.56\"."},
            "date_incurred": {"type": ["string", "null"], "description": "Date the account was opened, YYYY-MM-DD."},
            "claim_class": {"type": "string", "enum": @ClaimClasses@, "description": "secured for collateralised debts (auto, mortgage); nonpriority_unsecured for ordinary consumer debt; priority_unsecured only for taxes or support obligations."},
            "disputed": {"type": ["boolean", "null"], "description": "True only when the report marks the account disputed."},
            "page": @Page@,
            "confidence": @Confidence@},
            "required": ["account_last4", "amount", "date_incurred", "claim_class", "disputed", "page", "confidence"], "additionalProperties": false}}},
          "required": ["name", "address", "page", "confidence", "claims"], "additionalProperties": false}}},
          "required": ["creditors"], "additionalProperties": false}
        """))!;

    /// <summary>The pay stub's structured-output contract — a fresh copy each call.</summary>
    public static JsonObject PayStubSchema() => (JsonObject)JsonNode.Parse(Fill("""
        {"type": "object", "properties": {
          "employer": {"type": "object", "properties": {"name": @NullableString@, "address": @AddressSchema@, "page": @Page@, "confidence": @Confidence@}, "required": ["name", "address", "page", "confidence"], "additionalProperties": false},
          "pay_periods": {"type": "array", "items": {"type": "object", "properties": {
            "period_start": {"type": ["string", "null"], "description": "First day of the pay period, YYYY-MM-DD."},
            "period_end": {"type": ["string", "null"], "description": "Last day of the pay period, YYYY-MM-DD."},
            "pay_date": {"type": ["string", "null"], "description": "The date the check was issued, YYYY-MM-DD — distinct from the period; copy it exactly, it drives a statutory lookback window."},
            "gross": @NullableString@,
            "net": @NullableString@,
            "frequency": {"type": ["string", "null"], "enum": @PayFrequencies@},
            "deductions": {"type": "array", "items": {"type": "object", "properties": {
              "category": {"type": "string", "enum": @DeductionCategories@},
              "amount": @NullableString@,
              "description": {"type": ["string", "null"], "description": "The stub's own wording for this line."}},
              "required": ["category", "amount", "description"], "additionalProperties": false}},
            "page": @Page@,
            "confidence": @Confidence@},
            "required": ["period_start", "period_end", "pay_date", "gross", "net", "frequency", "deductions", "page", "confidence"], "additionalProperties": false}}},
          "required": ["employer", "pay_periods"], "additionalProperties": false}
        """))!;

    // The schema templates' shared fragments, spliced in by name.
    private static string Fill(string template) => template
        .Replace("@AddressSchema@", AddressSchema, StringComparison.Ordinal)
        .Replace("@Page@", Page, StringComparison.Ordinal)
        .Replace("@Confidence@", Confidence, StringComparison.Ordinal)
        .Replace("@NullableString@", NullableString, StringComparison.Ordinal)
        .Replace("@ClaimClasses@", EnumList(Wire.Names<ClaimClass>()), StringComparison.Ordinal)
        .Replace("@PayFrequencies@", EnumList(Wire.Names<PayFrequency>(), withNull: true), StringComparison.Ordinal)
        .Replace("@DeductionCategories@", EnumList(Wire.Names<DeductionCategory>()), StringComparison.Ordinal);

    /// <summary>Which prompt, schema and parser serve each extractable kind — one row per <see cref="JobRules.ExtractableDocumentKinds"/> member.</summary>
    public static readonly IReadOnlyDictionary<DocumentKind, (string Prompt, Func<JsonObject> Schema, Func<JsonObject, CoercionOutcome> Parse)> Extractors =
        new Dictionary<DocumentKind, (string, Func<JsonObject>, Func<JsonObject, CoercionOutcome>)>
        {
            [DocumentKind.CreditReport] = (CreditReportSystemPrompt, CreditReportSchema, ParseCreditReportOutput),
            [DocumentKind.PayStub] = (PayStubSystemPrompt, PayStubSchema, ParsePayStubOutput),
        };

    private static readonly Dictionary<string, string> LinkField = new(StringComparer.Ordinal)
    {
        [ClaimRules.Kind.Collection] = "creditor_id",
        [IncomeRules.PayPeriodRecord.Collection] = "employment_id",
    };

    private static readonly HashSet<string> LinkLeaders = new(StringComparer.Ordinal) { CreditorRules.Kind.Collection, IncomeRules.Employment.Collection };

    /// <summary>The registry entry.</summary>
    public static JobWorker For(DocumentExtractionDeps deps) => (job, cancellationToken) => RunAsync(job, deps, cancellationToken);

    /// <summary>Creditor + claim specs from the credit report's output, creditor-first so claims link by position.</summary>
    public static CoercionOutcome ParseCreditReportOutput(JsonObject raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var specs = new List<CandidateSpec>();
        var skipped = 0;
        foreach (var entry in Entries(raw["creditors"]))
        {
            var name = Text(entry["name"]);
            var payload = Validated(CreditorRules.Kind, new JsonObject { ["name"] = name, ["address"] = Address(entry["address"]) });
            if (payload is null || name is null)
            {
                skipped += 1 + Entries(entry["claims"]).Count;
                continue;
            }

            specs.Add(new CandidateSpec(CreditorRules.Kind.Collection, payload, ConfidenceOf(entry["confidence"]), PageOf(entry["page"])));
            foreach (var claim in Entries(entry["claims"]))
            {
                var claimPayload = Validated(ClaimRules.Kind, new JsonObject
                {
                    ["claim_class"] = Text(claim["claim_class"]),
                    ["account_last4"] = Last4(claim["account_last4"]),
                    ["amount"] = MoneyString(claim["amount"]),
                    ["date_incurred"] = Text(claim["date_incurred"]),
                    ["disputed"] = Json.AsBool(claim["disputed"]),
                });
                if (claimPayload is null)
                {
                    skipped++;
                    continue;
                }

                specs.Add(new CandidateSpec(ClaimRules.Kind.Collection, claimPayload, ConfidenceOf(claim["confidence"]), PageOf(claim["page"]), $"Claim of extracted creditor: {name}"));
            }
        }

        return new CoercionOutcome(specs, skipped);
    }

    /// <summary>Employment + pay-period specs from the pay stub's output — the employment leads the group.</summary>
    public static CoercionOutcome ParsePayStubOutput(JsonObject raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var employer = raw["employer"] as JsonObject ?? [];
        var employerName = Text(employer["name"]);
        var employment = employerName is null
            ? null
            : Validated(IncomeRules.Employment, new JsonObject
            {
                ["employer_name"] = employerName,
                ["employer_address"] = Address(employer["address"]),
                ["status"] = "employed",
            });
        var periods = Entries(raw["pay_periods"]);
        if (employment is null)
        {
            // A nameless employer is not a record a human can review a period against.
            return new CoercionOutcome([], 1 + periods.Count);
        }

        var specs = new List<CandidateSpec> { new(IncomeRules.Employment.Collection, employment, ConfidenceOf(employer["confidence"]), PageOf(employer["page"])) };
        var skipped = 0;
        foreach (var period in periods)
        {
            var deductions = new JsonArray();
            var index = 0;
            foreach (var deduction in Entries(period["deductions"]))
            {
                deductions.Add(new JsonObject
                {
                    // Worker-minted, satisfying the addressable-id contract.
                    ["id"] = $"d{++index}",
                    ["category"] = Text(deduction["category"]),
                    ["amount"] = MoneyString(deduction["amount"]),
                    ["description"] = Text(deduction["description"]),
                });
            }

            var payload = Validated(IncomeRules.PayPeriodRecord, new JsonObject
            {
                ["period_start"] = Text(period["period_start"]),
                ["period_end"] = Text(period["period_end"]),
                ["pay_date"] = Text(period["pay_date"]),
                ["gross"] = MoneyString(period["gross"]),
                ["net"] = MoneyString(period["net"]),
                ["frequency"] = Text(period["frequency"]),
                ["deductions"] = deductions,
            });
            if (payload is null)
            {
                skipped++;
                continue;
            }

            specs.Add(new CandidateSpec(IncomeRules.PayPeriodRecord.Collection, payload, ConfidenceOf(period["confidence"]), PageOf(period["page"]), $"Pay period of extracted employer: {employerName}"));
        }

        return new CoercionOutcome(specs, skipped);
    }

    /// <summary>
    /// The worker. The result carries IDENTIFIERS AND COUNTS only — never a
    /// payload. Every deterministic refusal is a <see cref="JobException"/>; an
    /// API failure throws through and takes the retry path.
    /// </summary>
    public static async Task<JsonObject> RunAsync(Job job, DocumentExtractionDeps deps, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(deps);
        var model = deps.Model ?? throw new JobException("AI extraction is not configured in this environment yet.", "not_configured");
        var documentId = job.DocumentId ?? throw new JobException("This extraction job names no document.", "document_not_found");
        var found = await deps.Cases.ReadForWorkerAsync(job.CaseId, cancellationToken).ConfigureAwait(false)
            ?? throw new JobException("The case this job was accepted against no longer exists.", "case_not_found");
        await deps.AccessLog.RecordAsync(AccessEvents.Record(job.CreatedBy, AccessAction.DocumentExtract, caseId: found.Id, clock: deps.Clock), cancellationToken).ConfigureAwait(false);
        var document = await deps.Documents.GetAsync(found.Id, documentId, cancellationToken).ConfigureAwait(false)
            ?? throw new JobException("The document this job was accepted against no longer exists.", "document_not_found");

        var outcome = await ExtractAsync(document, deps, model, job.CreatedBy, cancellationToken).ConfigureAwait(false);
        deps.Log.Info(
            "document extracted",
            ("case_id", found.Id),
            ("job_id", job.Id),
            ("document_id", document.Id),
            ("document_kind", document.Kind.ToWire()),
            ("model", Json.AsString(outcome["model"])),
            ("candidates", ((JsonArray)outcome["candidates"]!).Count),
            ("skipped", (int)outcome["skipped"]!));
        return outcome;
    }

    private static async Task<JsonObject> ExtractAsync(Document document, DocumentExtractionDeps deps, IExtractionModel model, string acceptedBy, CancellationToken cancellationToken)
    {
        if (!Extractors.TryGetValue(document.Kind, out var extractor))
        {
            throw new JobException($"This document kind has no extractor. Extraction reads: {string.Join(", ", JobRules.ExtractableDocumentKinds.Select(kind => kind.ToWire()))}.", "not_extractable");
        }

        if (document.Status != DocumentStatus.Stored)
        {
            throw new JobException("This document's upload has not completed; finish the upload and try again.", "not_uploaded");
        }

        if (!ModelInputContentTypes.Contains(document.ContentType))
        {
            throw new JobException("This file format cannot be read by extraction yet — upload the document as a PDF, JPEG or PNG.", "unsupported_format");
        }

        if (document.ByteSize > MaxDocumentBytes)
        {
            throw new JobException($"This document is too large for extraction (over {MaxDocumentBytes / (1024 * 1024)} MB).", "too_large");
        }

        var data = await deps.Blobs.GetBytesAsync(document.StorageRef, cancellationToken).ConfigureAwait(false)
            ?? throw new JobException("The document's bytes are not in the store; upload it again.", "bytes_missing");

        var result = await model.ExtractAsync(new ExtractionRequest(document.ContentType.ToWire(), data, extractor.Prompt, extractor.Schema()), cancellationToken).ConfigureAwait(false);
        var coerced = extractor.Parse(result.Raw);
        var specs = coerced.Specs.Take(MaxCandidatesPerRun).ToList();
        var truncated = coerced.Specs.Count - specs.Count;

        // The model that produced the batch is its attribution; the preparer whose accept caused it, its subject.
        var origin = new CandidateOrigin(OriginChannel.Extraction, result.Model, acceptedBy);
        var stored = new JsonArray();
        string? leaderId = null;
        foreach (var spec in specs)
        {
            var payload = spec.Payload;
            if (LinkField.TryGetValue(spec.EntityType, out var field) && leaderId is not null)
            {
                payload = (JsonObject)payload.DeepClone();
                payload[field] = leaderId;
            }

            var locator = new JsonObject { ["document_id"] = document.Id };
            if (spec.Page is { } page)
            {
                locator["page"] = page;
            }

            var candidate = CandidateRules.Create(
                new ProposalDraft(spec.EntityType, payload, null, spec.Note),
                document.CaseId,
                origin,
                document.Id,
                spec.Confidence,
                locator,
                deps.Clock);
            await deps.Candidates.CreateAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (LinkLeaders.Contains(spec.EntityType))
            {
                leaderId = candidate.Id;
            }

            stored.Add(new JsonObject { ["id"] = candidate.Id, ["entityType"] = candidate.EntityType });
        }

        return new JsonObject
        {
            ["outcome"] = "extracted",
            ["documentId"] = document.Id,
            ["documentKind"] = document.Kind.ToWire(),
            ["model"] = result.Model,
            ["candidates"] = stored,
            ["skipped"] = coerced.Skipped + truncated,
        };
    }

    // ── Coercion: defensive by construction ───────────────────────────

    private static string? Text(JsonNode? value) => Json.AsString(value) is { } text && TextRules.Strip(text).Length > 0 ? TextRules.Strip(text) : null;

    private static string? MoneyString(JsonNode? value)
    {
        var text = Text(value);
        if (text is null)
        {
            return null;
        }

        var stripped = MoneyFurniture().Replace(text, string.Empty);
        return stripped.Length > 0 ? stripped : null;
    }

    private static string? Last4(JsonNode? value)
    {
        var text = Text(value);
        if (text is null)
        {
            return null;
        }

        var digits = Digit().Matches(text).Select(match => match.Value).ToList();
        return digits.Count == 0 ? null : string.Concat(digits.TakeLast(4));
    }

    private static double? ConfidenceOf(JsonNode? value) =>
        Json.TryGetNumber(value, out var number) ? Math.Max(0.0, Math.Min(1.0, number)) : null;

    private static int? PageOf(JsonNode? value) =>
        Json.TryGetWholeNumber(value, out var number) && number >= 1 && number <= int.MaxValue ? (int)number : null;

    private static JsonObject Address(JsonNode? value)
    {
        var address = new JsonObject();
        if (value is not JsonObject raw)
        {
            return address;
        }

        foreach (var key in new[] { "line1", "line2", "city", "state", "postal_code", "raw" })
        {
            if (Text(raw[key]) is { } text)
            {
                address[key] = text;
            }
        }

        return address;
    }

    private static List<JsonObject> Entries(JsonNode? value) => value is JsonArray list ? [.. list.OfType<JsonObject>()] : [];

    // The payload as it will be stored, or null when the entity parser refuses
    // it — the SAME parser acceptance runs. Scrubbed FIRST.
    private static JsonObject? Validated(IEntityKind kind, JsonObject payload)
    {
        var cleaned = (JsonObject)PetitionReview.Scrub(payload)!;
        var pruned = new JsonObject();
        foreach (var (key, member) in cleaned)
        {
            if (member is not null && member is not JsonObject { Count: 0 })
            {
                pruned[key] = member.DeepClone();
            }
        }

        if (pruned.Count == 0)
        {
            return null;
        }

        try
        {
            _ = kind.ParseDraft(pruned, enforceProvenance: false);
        }
        catch (FieldValidationException)
        {
            return null;
        }

        return pruned;
    }

    private static string EnumList(IReadOnlyList<string> names, bool withNull = false) =>
        "[" + string.Join(", ", names.Select(name => $"\"{name}\"")) + (withNull ? ", null" : string.Empty) + "]";

    [GeneratedRegex(@"[$,\s]")]
    private static partial Regex MoneyFurniture();

    [GeneratedRegex(@"\d")]
    private static partial Regex Digit();
}
