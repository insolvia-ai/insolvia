using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Insolvia.Core;
using Insolvia.Core.AccessLog;
using Insolvia.Core.CaseEntities;
using Insolvia.Core.Cases;
using Insolvia.Core.Debtors;
using Insolvia.Core.Jobs;
using Insolvia.Core.Ports;
using Insolvia.Engines.CreditorMatrix;
using Insolvia.Engines.Projections;
using Insolvia.Forms;

namespace Insolvia.Worker.Workers;

/// <summary>One advisory finding, already validated against the vocabulary.</summary>
public sealed record ReviewFinding(string Severity, string Category, string Form, string Line, string Message);

/// <summary>What the review worker composes. <see cref="Model"/> is null where the key is not configured.</summary>
public sealed record PetitionReviewDeps(
    ICaseStore Cases,
    IDebtorStore Debtors,
    ICaseEntityStore Entities,
    IPacketStore Packets,
    IAccessLog AccessLog,
    ITaxIdStore TaxIds,
    ITaxIdCipher TaxIdCipher,
    IReviewModel? Model)
{
    /// <summary>The clock — "today" for the re-assembly.</summary>
    public TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>The log.</summary>
    public JobLog Log { get; init; } = JobLog.Console;
}

/// <summary>
/// AI petition review (core/petition_review.py; issue #97, ADR 0019): AFTER
/// deterministic assembly, Claude reads the packet's actual content and flags
/// what rules cannot see. Review ONLY — a finding is advisory prose on the job
/// result, never case data. The review describes an ASSEMBLED packet: the
/// assembly re-runs and must hash to the newest stored packet. Tax identifiers
/// never leave: B121 is dropped from the document wholesale (structural), and
/// <see cref="Scrub"/> removes tax-id keys and SSN-shaped strings (defence in depth).
/// </summary>
public static partial class PetitionReview
{
    /// <summary>What a finding may be graded.</summary>
    public static readonly IReadOnlyList<string> Severities = ["high", "medium", "low"];

    /// <summary>The issue's check families, plus <c>other</c>.</summary>
    public static readonly IReadOnlyList<string> Categories = ["consistency", "arithmetic", "missing_creditor", "exemption", "transfer", "incomplete", "other"];

    /// <summary>The cap on findings stored on a job row.</summary>
    public const int MaxFindings = 50;

    /// <summary>The cap on one finding's message.</summary>
    public const int MaxMessageChars = 500;

    /// <summary>The model's instructions — byte-identical to Python's <c>REVIEW_SYSTEM_PROMPT</c> (pinned by the parity test).</summary>
    public const string SystemPrompt = """
        You are reviewing a completed U.S. Chapter 7 individual bankruptcy petition
        packet before filing: form B101, the B106 schedules and summary, the B107
        statement of financial affairs, and the creditor mailing matrix. You are the
        second set of eyes AFTER deterministic assembly — the packet is structurally
        complete and every form filled from confirmed case data. Your job is to find
        substantive problems that rules-based checks cannot see.

        Look for, in rough order of importance:
        - Internal contradictions: schedules disagreeing with each other or with the
          statement of financial affairs (income on 106I vs employment answers vs
          SOFA income; property on 106A/B vs secured claims on 106D; expenses on
          106J inconsistent with dependents or household answers).
        - Arithmetic that does not hold: totals, subtotals and derived lines that do
          not follow from their inputs.
        - Missing creditors: a creditor in the case records with no claim on any
          schedule, a claim whose creditor is absent from the mailing matrix, or
          debts the other answers imply (a mortgage on scheduled real property, a
          lender behind a vehicle) that no schedule lists.
        - Exemption red flags: exemptions claimed against property that does not
          appear, amounts that look inconsistent with the property's stated value.
        - Undisclosed-transfer signals: SOFA answers (payments, transfers, closed
          accounts, property held for another) that look incomplete or inconsistent
          with the schedules.
        - Answers that look missing rather than genuinely empty: a filled section
          whose content implies a companion answer that is blank.

        Rules for every finding:
        - Advisory only. Never propose a value to enter; describe the problem and
          where it shows.
        - Cite the specific form and the line or field key it points at, exactly as
          they appear in the packet content you are given.
        - One finding per distinct problem; do not restate one defect on every form
          it touches — cite the primary location and mention the others in the
          message.
        - Report only defects you are confident an attorney preparing this filing
          would want to see. Do not report style, formatting, absent optional facts,
          or anything the deterministic gate already guarantees (completeness of the
          form set, dangling record references, matrix generation).
        - A consistent, complete petition yields an EMPTY findings list. That is the
          expected outcome for most packets, not a failure to do your job.

        """;

    // The tax-id key names dropped wholesale, and the one form whose content IS the identifier.
    private static readonly HashSet<string> TaxIdKeys = new(StringComparer.Ordinal) { "tax_id", "ssn", "itin", "social_security_number" };
    private static readonly HashSet<string> FormsNotReviewed = new(StringComparer.Ordinal) { "form/b121" };

    /// <summary>The structured-output contract (Python's <c>REVIEW_OUTPUT_SCHEMA</c>) — a fresh copy each call.</summary>
    public static JsonObject OutputSchema() => (JsonObject)JsonNode.Parse(SchemaJson)!;

    private const string SchemaJson = """
        {
          "type": "object",
          "properties": {
            "findings": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "severity": {"type": "string", "enum": ["high", "medium", "low"]},
                  "category": {"type": "string", "enum": ["consistency", "arithmetic", "missing_creditor", "exemption", "transfer", "incomplete", "other"]},
                  "form": {"type": "string", "description": "The form the finding points at, as its series id in the packet content (e.g. form/b106i), or 'packet' for a cross-form finding."},
                  "line": {"type": "string", "description": "The line or field key on that form (e.g. 4_combined_monthly_income), or '' when the finding has no single line."},
                  "message": {"type": "string", "description": "One or two sentences a petition preparer can act on."}
                },
                "required": ["severity", "category", "form", "line", "message"],
                "additionalProperties": false
              }
            }
          },
          "required": ["findings"],
          "additionalProperties": false
        }
        """;

    /// <summary>The registry entry.</summary>
    public static JobWorker For(PetitionReviewDeps deps) => (job, cancellationToken) => RunAsync(job, deps, cancellationToken);

    /// <summary>A finding's wire shape — every key always present.</summary>
    public static JsonObject FindingJson(ReviewFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        return new JsonObject
        {
            ["severity"] = finding.Severity,
            ["category"] = finding.Category,
            ["form"] = finding.Form,
            ["line"] = finding.Line,
            ["message"] = finding.Message,
        };
    }

    /// <summary>
    /// Validate the model's structured output. Malformed output throws
    /// <see cref="FormatException"/> — infrastructure to the dispatch loop, so a
    /// retry gets a fresh generation.
    /// </summary>
    public static IReadOnlyList<ReviewFinding> ParseFindings(JsonObject raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (raw["findings"] is not JsonArray entries)
        {
            throw new FormatException("review model output has no findings list");
        }

        var findings = new List<ReviewFinding>();
        foreach (var node in entries.Take(MaxFindings))
        {
            if (node is not JsonObject entry)
            {
                throw new FormatException("review model finding is not an object");
            }

            var severity = Json.AsString(entry["severity"]);
            var category = Json.AsString(entry["category"]);
            var form = Json.AsString(entry["form"]);
            var line = Json.AsString(entry["line"]);
            var message = Json.AsString(entry["message"]);
            if (severity is null || !Severities.Contains(severity) || category is null || !Categories.Contains(category)
                || form is null || line is null || message is null || PyText.Strip(message).Length == 0)
            {
                throw new FormatException("review model finding is malformed");
            }

            findings.Add(new ReviewFinding(severity, category, Cap(form, 100), Cap(line, 100), Cap(message, MaxMessageChars)));
        }

        return findings;
    }

    /// <summary>Recursively remove tax identifiers: keys named for one dropped, values shaped like one replaced.</summary>
    public static JsonNode? Scrub(JsonNode? value) => value switch
    {
        JsonObject map => new JsonObject([.. map.Where(pair => !TaxIdKeys.Contains(pair.Key)).Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, Scrub(pair.Value)))]),
        JsonArray list => new JsonArray([.. list.Select(Scrub)]),
        JsonValue text when Json.AsString(text) is { } s => JsonValue.Create(TaxIdShape().Replace(s, "[tax id removed]")),
        _ => value?.DeepClone(),
    };

    /// <summary>
    /// The one user message: the packet's rendered content and the confirmed
    /// records behind it, as deterministic JSON (sorted keys, the stores' order),
    /// scrubbed LAST so nothing added later can dodge it.
    /// </summary>
    public static string ReviewDocument(CaseData data, AssembledPacket packet)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(packet);
        var matrix = Encoding.ASCII.GetString(packet.Parts.First(part => part.Name == CreditorMatrices.MatrixFileName).Content);

        var debtors = new JsonArray();
        foreach (var debtor in data.Debtors)
        {
            var record = new JsonObject { ["filing_role"] = debtor.FilingRole.ToWire() };
            foreach (var (key, member) in Prune(DebtorRules.BodyNode(debtor.Body)) as JsonObject ?? [])
            {
                record[key] = member?.DeepClone();
            }

            if (debtor.ClientId is not null)
            {
                record["client_id"] = debtor.ClientId;
            }

            if (debtor.CaseCreatedAt is { } caseCreatedAt)
            {
                record["case_created_at"] = CaseRules.Iso(caseCreatedAt);
            }

            debtors.Add(record);
        }

        static JsonArray Bodies(IEnumerable<ICaseEntity> entities) => new([.. entities.Select(entity => Prune(entity.BodyNode()))]);

        var forms = new JsonArray();
        foreach (var (seriesId, values) in packet.Projections)
        {
            if (FormsNotReviewed.Contains(seriesId))
            {
                continue;
            }

            forms.Add(new JsonObject
            {
                ["form"] = seriesId,
                ["revision"] = packet.FormRevisions[seriesId],
                ["fields"] = RenderedForm(values),
            });
        }

        var document = new JsonObject
        {
            ["case"] = new JsonObject { ["chapter"] = data.Case.Chapter, ["district"] = data.Case.District },
            ["forms"] = forms,
            ["creditor_matrix"] = matrix,
            ["confirmed_records"] = new JsonObject
            {
                ["debtors"] = debtors,
                ["petition"] = Bodies(data.Petitions),
                ["employments"] = Bodies(data.Employments),
                ["income_summaries"] = Bodies(data.IncomeSummaries),
                ["assets"] = Bodies(data.Assets),
                ["exemptions"] = Bodies(data.Exemptions),
                ["creditors"] = Bodies(data.Creditors),
                ["claims"] = Bodies(data.Claims),
                ["contract_leases"] = Bodies(data.ContractLeases),
                ["codebtors"] = Bodies(data.Codebtors),
                ["households"] = Bodies(data.Households),
                ["expenses"] = Bodies(data.Expenses),
                ["dependents"] = Bodies(data.Dependents),
                ["sofa_entries"] = Bodies(data.SofaEntries),
            },
        };
        return PythonJson.Dumps(Scrub(document), sortKeys: true);
    }

    /// <summary>
    /// The worker. SUCCEEDED shapes: <c>blocked</c> (no packet yet, the case
    /// changed since it, or the gate refuses) and <c>reviewed</c> (the report).
    /// A <see cref="JobException"/> for no key or a vanished case; an API
    /// failure throws through and takes the retry path.
    /// </summary>
    public static async Task<JsonObject> RunAsync(Job job, PetitionReviewDeps deps, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(deps);
        var model = deps.Model ?? throw new JobException("AI review is not configured in this environment yet.", "not_configured");
        var found = await deps.Cases.ReadForWorkerAsync(job.CaseId, cancellationToken).ConfigureAwait(false)
            ?? throw new JobException("The case this job was accepted against no longer exists.", "case_not_found");
        await deps.AccessLog.RecordAsync(AccessEvents.Record(job.CreatedBy, AccessAction.PetitionReview, caseId: found.Id, clock: deps.Clock), cancellationToken).ConfigureAwait(false);

        var data = await CaseDataReads.ReadAsync(found, deps.Debtors, deps.Entities, cancellationToken).ConfigureAwait(false);

        // The stored packet printed B121 with the number, so reproducing its
        // bytes needs it too — and review_document drops B121 before anything leaves.
        data = await CaseDataReads.DiscloseTaxIdsAsync(data, job.CreatedBy, "petition_review", deps.TaxIds, deps.TaxIdCipher, deps.AccessLog, cancellationToken).ConfigureAwait(false);
        var now = deps.Clock.GetUtcNow();
        var (assembled, problems) = PacketAssembly.Assemble(data, DateOnly.FromDateTime(now.UtcDateTime), now);
        if (assembled is null)
        {
            deps.Log.Info("petition review blocked by the gate", ("case_id", found.Id), ("job_id", job.Id), ("problems", problems.Count));
            return PacketAssemblyWorker.Blocked(problems);
        }

        var packets = await deps.Packets.ListForCaseAsync(found.Id, cancellationToken).ConfigureAwait(false);
        if (packets.Count == 0)
        {
            return Blocked("No packet has been assembled yet — assemble the filing packet, then run the review.");
        }

        var latest = packets[0];
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(PacketAssembly.PacketZip(assembled.Parts)));
        if (sha256 != latest.Sha256)
        {
            return Blocked("The case has changed since its packet was assembled — assemble the packet again, then run the review.");
        }

        var result = await model.ReviewAsync(ReviewDocument(data, assembled), cancellationToken).ConfigureAwait(false);
        var findings = ParseFindings(result.Raw);
        deps.Log.Info("petition reviewed", ("case_id", found.Id), ("job_id", job.Id), ("packet_id", latest.Id), ("model", result.Model), ("findings", findings.Count));
        return new JsonObject
        {
            ["outcome"] = "reviewed",
            ["report"] = new JsonObject
            {
                ["packetId"] = latest.Id,
                ["packetSha256"] = latest.Sha256,
                ["model"] = result.Model,
                ["findings"] = new JsonArray([.. findings.Select(finding => (JsonNode)FindingJson(finding))]),
            },
        };
    }

    private static JsonObject Blocked(string message) =>
        PacketAssemblyWorker.Blocked([new PacketProblem("packets", null, string.Empty, message)]);

    // A projection fill as the document spells it — the printed value.
    private static JsonNode? RenderedFill(FieldFill fill) => fill switch
    {
        TextFill text => text.Value,
        CheckFill check => check.On,
        OptionFill option => option.Export,
        WidgetStatesFill states => states.States.Count > 0
            ? new JsonArray([.. states.States.Select(state => (JsonNode)state)])
            : new JsonArray([.. states.Indexes.Select(index => (JsonNode)index)]),
        _ => fill.ToString(),
    };

    private static JsonObject RenderedForm(FieldValues values)
    {
        var rendered = new JsonObject();
        foreach (var (fieldId, entry) in values)
        {
            rendered[fieldId] = entry switch
            {
                RowFills rows => new JsonObject([.. rows.Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, RenderedFill(pair.Value)))]),
                FieldFill fill => RenderedFill(fill),
                _ => entry.ToString(),
            };
        }

        return rendered;
    }

    // Python's _prune: drop nulls and empty containers, everywhere (lists included).
    private static JsonNode? Prune(JsonNode? value)
    {
        switch (value)
        {
            case JsonObject map:
                var pruned = new JsonObject();
                foreach (var (key, member) in map)
                {
                    var cleaned = Prune(member);
                    if (!IsBlank(cleaned))
                    {
                        pruned[key] = cleaned;
                    }
                }

                return pruned;
            case JsonArray list:
                return new JsonArray([.. list.Select(Prune).Where(cleaned => !IsBlank(cleaned))]);
            default:
                return value?.DeepClone();
        }
    }

    private static bool IsBlank(JsonNode? node) => node is null || node is JsonObject { Count: 0 } || node is JsonArray { Count: 0 };

    private static string Cap(string text, int limit) => PyText.Length(text) <= limit ? text : new StringInfoCap(text).Take(limit);

    [GeneratedRegex(@"\b\d{3}-\d{2}-\d{4}\b")]
    private static partial Regex TaxIdShape();

    // Python slices by code point; a surrogate pair is one character.
    private readonly struct StringInfoCap(string text)
    {
        public string Take(int limit)
        {
            var builder = new StringBuilder();
            var count = 0;
            for (var index = 0; index < text.Length && count < limit; index++, count++)
            {
                builder.Append(text[index]);
                if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length)
                {
                    builder.Append(text[++index]);
                }
            }

            return builder.ToString();
        }
    }
}
