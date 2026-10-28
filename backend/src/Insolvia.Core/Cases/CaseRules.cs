using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Insolvia.Core.Courts;
using Insolvia.Core.Fields;

namespace Insolvia.Core.Cases;

/// <summary>
/// Case validation, identity and the lifecycle's rules (from <c>cases.py</c>).
/// Pure: no store, no clock beyond the <see cref="TimeProvider"/> argument.
/// </summary>
public static class CaseRules
{
    /// <summary>The chapters an individual debtor can file under (Python's <c>CHAPTERS</c>).</summary>
    public static readonly IReadOnlyList<int> Chapters = [7, 11, 12, 13];

    /// <summary>Every status at or after filing — the petition is a court record.</summary>
    public static readonly IReadOnlySet<CaseStatus> FiledStatuses =
        new HashSet<CaseStatus> { CaseStatus.Filed, CaseStatus.Discharged, CaseStatus.Dismissed, CaseStatus.Closed };

    /// <summary>The statuses in which the firm holds the engagement before filing.</summary>
    public static readonly IReadOnlySet<CaseStatus> RetainedStatuses =
        new HashSet<CaseStatus> { CaseStatus.Intake, CaseStatus.ReadyToFile };

    /// <summary>
    /// THE ALLOWED MOVES: forward only, with exactly two ways back — intake and
    /// ready_to_file trade places, and a court reopens a closed case
    /// (§ 350(b)). A status set to itself is not a move and is accepted.
    /// </summary>
    public static readonly FrozenDictionary<CaseStatus, IReadOnlySet<CaseStatus>> Transitions =
        new Dictionary<CaseStatus, IReadOnlySet<CaseStatus>>
        {
            [CaseStatus.Intake] = new HashSet<CaseStatus> { CaseStatus.ReadyToFile, CaseStatus.Filed },
            [CaseStatus.ReadyToFile] = new HashSet<CaseStatus> { CaseStatus.Intake, CaseStatus.Filed },
            [CaseStatus.Filed] = new HashSet<CaseStatus> { CaseStatus.Discharged, CaseStatus.Dismissed, CaseStatus.Closed },
            [CaseStatus.Discharged] = new HashSet<CaseStatus> { CaseStatus.Closed },
            [CaseStatus.Dismissed] = new HashSet<CaseStatus> { CaseStatus.Closed },
            [CaseStatus.Closed] = new HashSet<CaseStatus> { CaseStatus.Filed },
        }.ToFrozenDictionary();

    /// <summary>The post-filing facts the docket states, each free text, with its length cap.</summary>
    public static readonly IReadOnlyList<(string Name, int Limit)> PostFilingTextFields =
        [("case_number", 40), ("judge", 120), ("trustee", 120), ("office_file_number", 40)];

    /// <summary>The largest page a listing may ask for.</summary>
    public const int MaxListLimit = 100;

    /// <summary>The page size when none is asked for.</summary>
    public const int DefaultListLimit = 50;

    /// <summary>A case is opened for one client, or two filing jointly.</summary>
    public const int MaxCaseClients = 2;

    /// <summary>Who <c>changed_by</c> names when the filing worker moved the case.</summary>
    public const string FilingWorkerActor = "filing-worker";

    /// <summary>The by-firm listing index.</summary>
    public const string IndexByFirm = "by-firm";

    /// <summary>The by-assignee listing index.</summary>
    public const string IndexByAssignee = "by-assignee";

    /// <summary>The by-client index — never paginated through a cursor.</summary>
    public const string IndexByClient = "by-client";

    /// <summary>A whole-record write raced a status move. 409: the request was fine, the case moved underneath it.</summary>
    public const string StatusMoved = "This case's status changed since it was read — reload it and try again.";

    private const string DateMessage = "Must be a date in YYYY-MM-DD form.";

    /// <summary>Whether a case in <paramref name="status"/> has a petition on the court's docket.</summary>
    public static bool IsFiled(CaseStatus status) => FiledStatuses.Contains(status);

    /// <summary>
    /// Microsecond-precision UTC with a literal Z — fixed width, so the string
    /// sorts as the instant does (Python's <c>_timestamp</c> format).
    /// </summary>
    public static string Iso(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);

    /// <summary>The value BOTH listing indexes sort on, defined once: <c>&lt;createdAt&gt;#&lt;caseId&gt;</c>.</summary>
    public static string ListingSortKey(DateTimeOffset createdAt, string caseId) => $"{Iso(createdAt)}#{caseId}";

    /// <summary>
    /// Validate POST /v1/cases; unknown keys are ignored. Status is NOT
    /// accepted — every case starts at intake. <c>client_ids</c> is REQUIRED
    /// (ADR 0022); <paramref name="requireClients"/> false is for callers with
    /// no request behind them (the seed loader, store tests), never a route.
    /// </summary>
    public static CaseDraft ParseCreation(JsonObject payload, bool requireClients = true, CourtRegistry? courts = null)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var chapter = ParseChapter(payload["chapter"], errors);
        RefuseTypedDistrict(payload, errors);
        var court = ParseCourt(payload, errors, courts ?? CourtRegistry.Shipped);
        ValueCollection<string> clientIds = requireClients ? ParseClientIds(payload["client_ids"], errors) : [];
        if (errors.Count > 0 || chapter is null || court is null)
        {
            throw new FieldValidationException(errors);
        }

        return new CaseDraft(chapter.Value, court, clientIds);
    }

    /// <summary>
    /// Validate PATCH /v1/cases/&lt;id&gt;. Only supplied keys are validated; an
    /// empty body is refused rather than treated as a no-op.
    /// </summary>
    public static CaseChanges ParseUpdate(JsonObject payload, CourtRegistry? courts = null)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var changes = new CaseChanges();
        var any = false;

        if (payload.ContainsKey("chapter") && ParseChapter(payload["chapter"], errors) is { } chapter)
        {
            changes = changes with { Chapter = chapter };
            any = true;
        }

        RefuseTypedDistrict(payload, errors);
        if ((payload.ContainsKey("court") || payload.ContainsKey("division"))
            && ParseCourt(payload, errors, courts ?? CourtRegistry.Shipped) is { } court)
        {
            changes = changes with { Court = court };
            any = true;
        }

        if (payload.ContainsKey("status") && ParseStatus(payload["status"], errors) is { } status)
        {
            changes = changes with { Status = status };
            any = true;
        }

        var texts = ParsePostFilingTexts(payload, errors);
        if (texts.Count > 0)
        {
            changes = changes with { Texts = texts.ToValueDictionary() };
            any = true;
        }

        if (payload.ContainsKey("exemption_set") && ParseExemptionSet(payload["exemption_set"], errors) is { } exemptionSet)
        {
            changes = changes with { ExemptionSet = exemptionSet };
            any = true;
        }

        DateOnly? filedAt = null;
        DateOnly? meetingAt = null;
        if (payload.TryGetPropertyValue("filed_at", out var rawFiled))
        {
            if (rawFiled is null)
            {
                changes = changes with { ClearFiledAt = true };
                any = true;
            }
            else if (ParseFormDate(rawFiled, "filed_at", errors) is { } parsed)
            {
                filedAt = parsed;
                changes = changes with { FiledAt = parsed };
                any = true;
            }
        }

        if (payload.TryGetPropertyValue("meeting_341_at", out var rawMeeting))
        {
            if (rawMeeting is null)
            {
                changes = changes with { ClearMeeting341At = true };
                any = true;
            }
            else if (ParseFormDate(rawMeeting, "meeting_341_at", errors) is { } parsed)
            {
                meetingAt = parsed;
                changes = changes with { Meeting341At = parsed };
                any = true;
            }
        }

        // Only checkable when both arrive together; against a stored date it
        // is a slip the deadline engine tolerates rather than a refused save.
        if (filedAt is not null && meetingAt is not null && meetingAt < filedAt)
        {
            errors["meeting_341_at"] = "The § 341 meeting cannot precede the petition.";
        }

        if (errors.Count > 0)
        {
            throw new FieldValidationException(errors);
        }

        return any ? changes : throw new ValidationException("no supported fields to update");
    }

    /// <summary>
    /// Stamp a draft with server-generated identity, and link its creator. The
    /// assignment is returned for the store to write TOGETHER with the case: a
    /// case whose creator is not linked is invisible to them.
    /// </summary>
    public static (Case Case, CaseAssignment Assignment) Create(CaseDraft draft, string firmId, string createdBy, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var now = Clock.Now(clock);
        var created = new Case(Guid.NewGuid().ToString(), firmId, createdBy, draft.Chapter, draft.Court.District, CaseStatus.Intake, now, now)
        {
            Court = draft.Court.Court,
            Division = draft.Court.Division,
        };
        return (created, Assign(created, createdBy, createdBy, clock));
    }

    /// <summary>Link <paramref name="subject"/> to <paramref name="target"/>.</summary>
    public static CaseAssignment Assign(Case target, string subject, string assignedBy, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new CaseAssignment(target.Id, subject, target.CreatedAt, Clock.Now(clock), assignedBy);
    }

    /// <summary>
    /// The case with <paramref name="changes"/> applied and <c>updated_at</c>
    /// refreshed — or a refusal. A court change writes court, division and
    /// district in one step. THE LIFECYCLE IS CHECKED HERE: a move outside
    /// <see cref="Transitions"/> is a 409; reaching filed needs a filed date and
    /// a case number (400, keyed to the missing field); a filed case keeps both.
    /// </summary>
    public static Case ApplyChanges(Case current, CaseChanges changes, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(changes);
        var status = changes.Status ?? current.Status;
        if (status != current.Status && !(Transitions.TryGetValue(current.Status, out var allowed) && allowed.Contains(status)))
        {
            throw new ConflictException(
                $"A case cannot move from '{current.Status.ToWire()}' to '{status.ToWire()}'."
                + (IsFiled(current.Status) ? " A filed petition is changed by amendment, not by moving it back." : string.Empty));
        }

        var updated = current with
        {
            Chapter = changes.Chapter ?? current.Chapter,
            Status = status,
            ExemptionSet = changes.ExemptionSet ?? current.ExemptionSet,
            FiledAt = changes.ClearFiledAt ? null : changes.FiledAt ?? current.FiledAt,
            Meeting341At = changes.ClearMeeting341At ? null : changes.Meeting341At ?? current.Meeting341At,
        };
        if (changes.Court is not null)
        {
            updated = updated with { Court = changes.Court.Court, Division = changes.Court.Division, District = changes.Court.District };
        }

        foreach (var (name, value) in changes.Texts)
        {
            updated = name switch
            {
                "case_number" => updated with { CaseNumber = value },
                "judge" => updated with { Judge = value },
                "trustee" => updated with { Trustee = value },
                "office_file_number" => updated with { OfficeFileNumber = value },
                _ => throw new ArgumentException($"{name} is not a post-filing text field", nameof(changes)),
            };
        }

        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        if (IsFiled(status))
        {
            // Checked on the way IN, and against a clear once there — a case
            // filed before these rules existed can still be edited.
            var entering = !IsFiled(current.Status);
            if (updated.FiledAt is null && (entering || changes.ClearFiledAt || changes.FiledAt is not null))
            {
                errors["filed_at"] = "A filed case needs its filed date.";
            }

            if (updated.CaseNumber is null && (entering || changes.Texts.ContainsKey("case_number")))
            {
                errors["case_number"] = "A filed case needs its case number.";
            }
        }

        if (errors.Count > 0)
        {
            throw new FieldValidationException(errors);
        }

        return updated with { UpdatedAt = Clock.Now(clock) };
    }

    /// <summary>
    /// The history row for a write that moved <paramref name="before"/> to
    /// <paramref name="after"/>, or null when the status did not move — stamped
    /// with the case's own <c>updated_at</c>, so the two agree on when.
    /// </summary>
    public static StatusChange? StatusChangeFor(Case before, Case after, string changedBy, string? filingId = null)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        return before.Status == after.Status
            ? null
            : new StatusChange(after.Id, after.UpdatedAt, changedBy, before.Status, after.Status, filingId);
    }

    /// <summary>
    /// The case moved to filed by an electronic filing (ADR 0024 PR 8), through
    /// <see cref="ApplyChanges"/> — the same move a person's PATCH makes, refused
    /// where theirs would be. The CALLER checks <see cref="IsFiled"/> first.
    /// </summary>
    public static (Case Case, StatusChange Change) FileCase(Case current, string caseNumber, string filedAt, string changedBy, string filingId, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(caseNumber);
        if (IsFiled(current.Status))
        {
            throw new ConflictException("This case is already filed.");
        }

        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var date = ParseFormDate(JsonValue.Create(filedAt), "filed_at", errors);
        if (PyText.Strip(caseNumber).Length == 0)
        {
            errors["case_number"] = "A filed case needs its case number.";
        }

        if (errors.Count > 0)
        {
            throw new FieldValidationException(errors);
        }

        var after = ApplyChanges(
            current,
            new CaseChanges
            {
                Status = CaseStatus.Filed,
                FiledAt = date,
                Texts = new Dictionary<string, string?> { ["case_number"] = PyText.Strip(caseNumber) }.ToValueDictionary(),
            },
            clock);
        var change = StatusChangeFor(current, after, changedBy, filingId)
            ?? throw new InvalidOperationException("the status moved: not filed -> filed");
        return (after, change);
    }

    /// <summary><c>PUT /v1/cases/&lt;id&gt;/archived</c>'s body: <c>{"archived": true|false}</c>.</summary>
    public static bool ParseArchive(JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return Json.AsBool(payload["archived"])
            ?? throw new FieldValidationException(new Dictionary<string, string> { ["archived"] = "Must be true or false." });
    }

    /// <summary>
    /// The case archived (stamped with who and when) or restored. Archiving an
    /// archived case keeps its first stamp — "since when" has one answer.
    /// </summary>
    public static Case SetArchived(Case current, bool archived, string by, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (archived == current.Archived)
        {
            return current;
        }

        var now = Clock.Now(clock);
        return archived
            ? current with { ArchivedAt = now, ArchivedBy = by, UpdatedAt = now }
            : current with { ArchivedAt = null, ArchivedBy = null, UpdatedAt = now };
    }

    /// <summary>
    /// Why this case may not be deleted, or null. A FILED case is refused,
    /// whatever happened since: its petition is a court record. Archive it.
    /// </summary>
    public static string? DeletionRefusal(Case current)
    {
        ArgumentNullException.ThrowIfNull(current);
        return IsFiled(current.Status)
            ? "A filed case cannot be deleted — its petition is a court record. Archive it instead."
            : null;
    }

    /// <summary>The case soft-deleted, stamped with who and when.</summary>
    public static Case MarkDeleted(Case current, string by, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        var now = Clock.Now(clock);
        return current with { DeletedAt = now, DeletedBy = by, UpdatedAt = now };
    }

    /// <summary>
    /// The pinned case packet assembly writes — both pins, replacing any
    /// earlier ones (re-assembly before filing re-pins).
    /// </summary>
    public static Case Pin(Case current, IReadOnlyDictionary<string, string> formRevisions, string constantsSetId, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        return current with
        {
            FormRevisions = formRevisions.ToValueDictionary(),
            ConstantsSetId = constantsSetId,
            UpdatedAt = Clock.Now(clock),
        };
    }

    /// <summary>A listing's <c>limit</c> query value: the default when absent, else 1 to <see cref="MaxListLimit"/>.</summary>
    public static int ParseListLimit(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return DefaultListLimit;
        }

        // Python's int(): surrounding whitespace and a sign are allowed.
        if (!int.TryParse(raw.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var limit))
        {
            throw new ValidationException("limit must be a number");
        }

        return limit is < 1 or > MaxListLimit
            ? throw new ValidationException($"limit must be between 1 and {MaxListLimit}")
            : limit;
    }

    /// <summary>The tag a listing cursor carries: its index AND its view (#355).</summary>
    public static string ListingCursorTag(string index, bool archived) => archived ? $"{index}:archived" : index;

    /// <summary>
    /// An opaque pagination cursor bound to the index (and view) it was minted
    /// against. Opaque, NOT signed: the query itself is pinned to the caller's
    /// firm or subject, so a rewritten cursor buys nothing.
    /// </summary>
    public static string EncodeCursor(IReadOnlyDictionary<string, string> key, string index)
    {
        ArgumentNullException.ThrowIfNull(key);
        var sorted = new JsonObject();
        foreach (var (name, value) in key.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            sorted[name] = value;
        }

        // Python's json.dumps(sort_keys=True, separators=(",", ":")): "i" sorts before "k".
        var raw = new JsonObject { ["i"] = index, ["k"] = sorted }.ToJsonString(PythonJson);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)).Replace('+', '-').Replace('/', '_');
    }

    /// <summary>
    /// The inverse of <see cref="EncodeCursor"/>, refusing anything this
    /// service did not mint and anything minted against another index — one
    /// message for both, so the caller learns nothing about their permissions.
    /// </summary>
    public static Dictionary<string, string> DecodeCursor(string cursor, string index)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        JsonNode? decoded;
        try
        {
            var bytes = Convert.FromBase64String(cursor.Replace('-', '+').Replace('_', '/'));
            decoded = JsonNode.Parse(Encoding.UTF8.GetString(bytes));
        }
        catch (Exception error) when (error is FormatException or JsonException or DecoderFallbackException)
        {
            throw new ValidationException("cursor is not valid", error);
        }

        if (decoded is not JsonObject obj || Json.AsString(obj["i"]) != index || obj["k"] is not JsonObject key)
        {
            throw new ValidationException("cursor is not valid");
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in key)
        {
            result[name] = Json.AsString(value) ?? throw new ValidationException("cursor is not valid");
        }

        return result;
    }

    private static readonly JsonSerializerOptions PythonJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static int? ParseChapter(JsonNode? value, Dictionary<string, string> errors)
    {
        // JSON true is not chapter 1.
        if (!Json.TryGetWholeNumber(value, out var chapter))
        {
            errors["chapter"] = "Chapter must be a number.";
            return null;
        }

        if (!Chapters.Contains((int)Math.Clamp(chapter, int.MinValue, int.MaxValue)))
        {
            errors["chapter"] = "Chapter must be one of " + string.Join(", ", Chapters) + ".";
            return null;
        }

        return (int)chapter;
    }

    /// <summary>
    /// The court + division pair, validated against the registry. THE PAIR
    /// TRAVELS TOGETHER; the message lands on the half that is missing.
    /// </summary>
    private static CourtReference? ParseCourt(JsonObject payload, Dictionary<string, string> errors, CourtRegistry courts)
    {
        var court = Json.AsString(payload["court"]);
        var division = Json.AsString(payload["division"]);
        if (court is null || PyText.Strip(court).Length == 0)
        {
            errors["court"] = "Choose the bankruptcy court this case will be filed in.";
            return null;
        }

        if (division is null || PyText.Strip(division).Length == 0)
        {
            errors["division"] = "Choose the court's division.";
            return null;
        }

        var found = courts.Division(PyText.Strip(court), PyText.Strip(division));
        if (found is null)
        {
            if (courts.District(PyText.Strip(court)) is null)
            {
                errors["court"] = "That court is not in the registry.";
            }
            else
            {
                errors["division"] = "That division is not one of this court's.";
            }

            return null;
        }

        var (record, chosen) = found.Value;
        return new CourtReference(record.Code, chosen.Code, record.Name);
    }

    private static void RefuseTypedDistrict(JsonObject payload, Dictionary<string, string> errors)
    {
        if (payload.ContainsKey("district"))
        {
            errors["district"] = "The district is no longer typed — send `court` and `division` from the court registry (GET /v1/courts) and the district name is derived.";
        }
    }

    private static CaseStatus? ParseStatus(JsonNode? value, Dictionary<string, string> errors)
    {
        if (!Wire.TryParse<CaseStatus>(Json.AsString(value), out var status))
        {
            errors["status"] = "Status must be one of " + string.Join(", ", Wire.Names<CaseStatus>()) + ".";
            return null;
        }

        return status;
    }

    private static Dictionary<string, string?> ParsePostFilingTexts(JsonObject payload, Dictionary<string, string> errors)
    {
        var texts = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (name, limit) in PostFilingTextFields)
        {
            if (!payload.ContainsKey(name))
            {
                continue;
            }

            var before = errors.Count;
            var parsed = FieldParsers.Text(payload[name], name, errors, limit);
            if (errors.Count == before)
            {
                texts[name] = parsed;
            }
        }

        return texts;
    }

    private static ExemptionSet? ParseExemptionSet(JsonNode? value, Dictionary<string, string> errors)
    {
        if (!Wire.TryParse<ExemptionSet>(Json.AsString(value), out var set))
        {
            errors["exemption_set"] = "Exemption set must be one of " + string.Join(", ", Wire.Names<ExemptionSet>()) + ".";
            return null;
        }

        return set;
    }

    /// <summary>
    /// <c>YYYY-MM-DD</c>, parsed rather than pattern-matched. Unlike
    /// <see cref="FieldParsers.FormDate"/>, a blank string is an error here.
    /// </summary>
    private static DateOnly? ParseFormDate(JsonNode? value, string field, Dictionary<string, string> errors)
    {
        var raw = Json.AsString(value);
        if (raw is null || PyText.Strip(raw).Length == 0)
        {
            errors[field] = DateMessage;
            return null;
        }

        var stripped = PyText.Strip(raw);
        if (!DateOnly.TryParseExact(stripped, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            || parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) != stripped)
        {
            errors[field] = DateMessage;
            return null;
        }

        return parsed;
    }

    /// <summary>
    /// <c>client_ids</c>: one or two distinct client ids, Debtor 1 first. The
    /// same client twice is refused — one client, one role per case.
    /// </summary>
    private static ValueCollection<string> ParseClientIds(JsonNode? value, Dictionary<string, string> errors)
    {
        if (value is not JsonArray list || list.Count == 0)
        {
            errors["client_ids"] = "Choose the client this case is for.";
            return [];
        }

        if (list.Count > MaxCaseClients)
        {
            errors["client_ids"] = "A case is opened for one client, or two filing jointly.";
            return [];
        }

        var ids = list.Select(item => Json.AsString(item)).ToList();
        if (ids.Any(id => id is null || PyText.Strip(id).Length == 0))
        {
            errors["client_ids"] = "Each client must be a client id.";
            return [];
        }

        var stripped = ids.Select(id => PyText.Strip(id!)).ToList();
        if (stripped.Distinct(StringComparer.Ordinal).Count() != stripped.Count)
        {
            errors["client_ids"] = "The same client cannot be both debtors on one case.";
            return [];
        }

        return stripped.ToValueCollection();
    }
}
