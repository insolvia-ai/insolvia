using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Insolvia.Domain.Courts;

namespace Insolvia.Domain.Cases;

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

    /// <summary>The refusal of a chapter that is not one of <see cref="Chapters"/>.</summary>
    public const string ChapterMessage = "Chapter must be one of 7, 11, 12, 13.";

    /// <summary>The refusal of a missing court.</summary>
    public const string CourtRequired = "Choose the bankruptcy court this case will be filed in.";

    /// <summary>The refusal of a missing division.</summary>
    public const string DivisionRequired = "Choose the court's division.";

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

    /// <summary>Why <paramref name="court"/> and <paramref name="division"/> are not a pair the registry knows, keyed to the half at fault; null when they are.</summary>
    public static System.ComponentModel.DataAnnotations.ValidationResult? CourtProblem(string court, string division, CourtRegistry? courts = null)
    {
        var registry = courts ?? CourtRegistry.Shipped;
        return registry.Division(court, division) is not null ? null
            : registry.District(court) is null ? new("That court is not in the registry.", [nameof(NewCase.Court)])
            : new("That division is not one of this court's.", [nameof(NewCase.Division)]);
    }

    /// <summary>The registry's reference for a pair <see cref="CourtProblem"/> passed.</summary>
    public static CourtReference Court(string court, string division)
    {
        var (record, chosen) = CourtRegistry.Shipped.Division(court, division)
            ?? throw new FieldValidationException(new Dictionary<string, string> { ["court"] = "That court is not in the registry." });
        return new CourtReference(record.Code, chosen.Code, record.Name);
    }

    /// <summary>
    /// Stamp a draft with server-generated identity, and link its creator. The
    /// assignment is returned for the store to write TOGETHER with the case: a
    /// case whose creator is not linked is invisible to them.
    /// </summary>
    public static (Case Case, CaseAssignment Assignment) Create(NewCase draft, string firmId, string createdBy, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var now = Clock.Now(clock);
        var court = Court(draft.Court!, draft.Division!);
        var created = new Case(Guid.NewGuid().ToString(), firmId, createdBy, draft.Chapter, court.District, CaseStatus.Intake, now, now)
        {
            Court = court.Court,
            Division = court.Division,
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
            FiledAt = changes.Names(nameof(CaseChanges.FiledAt)) ? changes.FiledAt : current.FiledAt,
            Meeting341At = changes.Names(nameof(CaseChanges.Meeting341At)) ? changes.Meeting341At : current.Meeting341At,
            CaseNumber = changes.Names(nameof(CaseChanges.CaseNumber)) ? changes.CaseNumber : current.CaseNumber,
            Judge = changes.Names(nameof(CaseChanges.Judge)) ? changes.Judge : current.Judge,
            Trustee = changes.Names(nameof(CaseChanges.Trustee)) ? changes.Trustee : current.Trustee,
            OfficeFileNumber = changes.Names(nameof(CaseChanges.OfficeFileNumber)) ? changes.OfficeFileNumber : current.OfficeFileNumber,
        };
        if (changes.Court is not null && changes.Division is not null)
        {
            var court = Court(changes.Court, changes.Division);
            updated = updated with { Court = court.Court, Division = court.Division, District = court.District };
        }

        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        if (IsFiled(status))
        {
            // Checked on the way IN, and against a clear once there — a case
            // filed before these rules existed can still be edited.
            var entering = !IsFiled(current.Status);
            if (updated.FiledAt is null && (entering || changes.Names(nameof(CaseChanges.FiledAt))))
            {
                errors["filedAt"] = "A filed case needs its filed date.";
            }

            if (string.IsNullOrWhiteSpace(updated.CaseNumber) && (entering || changes.Names(nameof(CaseChanges.CaseNumber))))
            {
                errors["caseNumber"] = "A filed case needs its case number.";
            }
        }

        if (errors.Count > 0)
        {
            throw new FieldValidationException(errors);
        }

        return updated with { UpdatedAt = Clock.Now(clock) };
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
        var raw = new JsonObject { ["i"] = index, ["k"] = sorted }.ToJsonString(CompactJson);
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

    private static readonly JsonSerializerOptions CompactJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}
