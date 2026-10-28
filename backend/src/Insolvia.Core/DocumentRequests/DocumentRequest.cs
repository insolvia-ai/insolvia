using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Insolvia.Core.Documents;

namespace Insolvia.Core.DocumentRequests;

/// <summary>
/// One document asked of a case's client (ADR 0023 PR 5 / #364; the CASE half
/// of <c>document_requests.py</c>). <see cref="DocumentIds"/> are the
/// COMPLETED uploads against it, in completion order; <see cref="ReceivedAt"/>
/// is when the first completed, and stays even if staff reopen the request.
/// </summary>
/// <param name="Id">The server-minted uuid.</param>
/// <param name="CaseId">The case.</param>
/// <param name="Title">What is asked for.</param>
/// <param name="Kind">The document kind an upload against it is filed under.</param>
/// <param name="Description">What the client reads under the title.</param>
/// <param name="Status">Where it stands.</param>
/// <param name="DocumentIds">The completed uploads.</param>
/// <param name="CreatedAt">When it was asked — the list order.</param>
/// <param name="CreatedBy">Who asked.</param>
/// <param name="UpdatedAt">When it was last written.</param>
/// <param name="ReceivedAt">When the first upload completed.</param>
public sealed record DocumentRequest(
    string Id,
    string CaseId,
    string Title,
    DocumentKind Kind,
    string? Description,
    DocumentRequestStatus Status,
    ValueCollection<string> DocumentIds,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ReceivedAt = null);

/// <summary>Arrived against outstanding, over the requests the case still needs. <see cref="Total"/> excludes waived.</summary>
public sealed record DocumentRequestProgress(int Total, int Received, int Outstanding, int Waived);

/// <summary>The request rules.</summary>
public static class DocumentRequestRules
{
    /// <summary>The statuses staff may set by hand.</summary>
    public static readonly IReadOnlyList<DocumentRequestStatus> SettableStatuses = [DocumentRequestStatus.Requested, DocumentRequestStatus.Waived];

    /// <summary>
    /// Validate POST /v1/cases/&lt;case_id&gt;/document-requests — one ad-hoc
    /// request, the same three fields as a checklist entry, errors keyed bare.
    /// </summary>
    public static ChecklistItem ParseCreation(JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var item = DocumentChecklists.ParseItem(payload, "request", errors);
        if (item is null || errors.Count > 0)
        {
            throw new FieldValidationException(errors.ToDictionary(
                pair => pair.Key.StartsWith("request.", StringComparison.Ordinal) ? pair.Key["request.".Length..] : pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal));
        }

        return item;
    }

    /// <summary>Validate PATCH …/document-requests/&lt;id&gt;: <c>{"status": "requested" | "waived"}</c>.</summary>
    public static DocumentRequestStatus ParseUpdate(JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var raw = Json.AsString(payload["status"]);
        if (raw == DocumentRequestStatus.Received.ToWire())
        {
            throw new FieldValidationException(new Dictionary<string, string>
            {
                ["status"] = "A request is received when a document is uploaded against it, not by setting it.",
            });
        }

        if (!Wire.TryParse<DocumentRequestStatus>(raw, out var status) || !SettableStatuses.Contains(status))
        {
            throw new FieldValidationException(new Dictionary<string, string>
            {
                ["status"] = "Must be one of " + string.Join(", ", SettableStatuses.Select(s => s.ToWire())) + ".",
            });
        }

        return status;
    }

    /// <summary>A fresh request for <paramref name="item"/>.</summary>
    public static DocumentRequest Create(ChecklistItem item, string caseId, string createdBy, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        var now = Clock.Now(clock);
        return new DocumentRequest(Guid.NewGuid().ToString(), caseId, item.Title, item.Kind, item.Description, DocumentRequestStatus.Requested, [], now, createdBy, now);
    }

    /// <summary>
    /// The requests applying <paramref name="checklist"/> ADDS — every entry
    /// whose title the case has not already requested, in any status. Waived
    /// counts as requested: re-applying must not quietly ask again.
    /// </summary>
    public static ValueCollection<DocumentRequest> FromChecklist(
        IEnumerable<ChecklistItem> checklist,
        IEnumerable<DocumentRequest> existing,
        string caseId,
        string createdBy,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(checklist);
        ArgumentNullException.ThrowIfNull(existing);
        var have = existing.Select(request => DocumentChecklists.TitleKey(request.Title)).ToHashSet(StringComparer.Ordinal);
        var added = new List<DocumentRequest>();
        foreach (var item in checklist)
        {
            if (!have.Add(DocumentChecklists.TitleKey(item.Title)))
            {
                continue;
            }

            added.Add(Create(item, caseId, createdBy, clock));
        }

        return added.ToValueCollection();
    }

    /// <summary>
    /// Staff's hand: waive, or reopen. Reopening keeps the documents; the next
    /// completed upload receives it again.
    /// </summary>
    public static DocumentRequest SetStatus(DocumentRequest request, DocumentRequestStatus status, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!SettableStatuses.Contains(status))
        {
            throw new ArgumentException($"'{status.ToWire()}' is not settable by hand", nameof(status));
        }

        return request with { Status = status, UpdatedAt = Clock.Now(clock) };
    }

    /// <summary>
    /// A completed upload against the request — idempotent. A WAIVED request
    /// stays waived and still records the document: staff's decision is not
    /// overturned by an upload racing it.
    /// </summary>
    public static DocumentRequest Satisfy(DocumentRequest request, string documentId, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.DocumentIds.Contains(documentId))
        {
            return request;
        }

        var now = Clock.Now(clock);
        return request with
        {
            Status = request.Status == DocumentRequestStatus.Waived ? DocumentRequestStatus.Waived : DocumentRequestStatus.Received,
            DocumentIds = [.. request.DocumentIds, documentId],
            ReceivedAt = request.ReceivedAt ?? now,
            UpdatedAt = now,
        };
    }

    /// <summary>Whether a new upload may be authorised against it: a waived request is closed, a received one is not.</summary>
    public static bool AcceptsUploads(DocumentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Status != DocumentRequestStatus.Waived;
    }

    /// <summary>The case overview's progress over <paramref name="requests"/>.</summary>
    public static DocumentRequestProgress Progress(IEnumerable<DocumentRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        int received = 0, outstanding = 0, waived = 0;
        foreach (var request in requests)
        {
            switch (request.Status)
            {
                case DocumentRequestStatus.Received:
                    received++;
                    break;
                case DocumentRequestStatus.Waived:
                    waived++;
                    break;
                default:
                    outstanding++;
                    break;
            }
        }

        return new DocumentRequestProgress(received + outstanding, received, outstanding, waived);
    }

    /// <summary>Creation order, tie-broken by id — the order the checklist listed them in.</summary>
    public static IComparer<DocumentRequest> ListOrder { get; } = Comparer<DocumentRequest>.Create((left, right) =>
    {
        var byTime = left.CreatedAt.CompareTo(right.CreatedAt);
        return byTime != 0 ? byTime : string.CompareOrdinal(left.Id, right.Id);
    });
}

/// <summary>Where a document request stands (Python's <c>STATUSES</c>).</summary>
public enum DocumentRequestStatus
{
    /// <summary>Asked, nothing arrived (or reopened).</summary>
    [JsonStringEnumMemberName("requested")]
    Requested,

    /// <summary>An upload against it completed.</summary>
    [JsonStringEnumMemberName("received")]
    Received,

    /// <summary>Staff decided against it.</summary>
    [JsonStringEnumMemberName("waived")]
    Waived,
}
