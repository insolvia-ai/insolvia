using System.Text.Json.Nodes;
using Insolvia.Core.Documents;
using Insolvia.Core.Fields;

namespace Insolvia.Core.DocumentRequests;

/// <summary>
/// One document a firm asks for. <see cref="Kind"/> is what an upload against
/// it is filed under (the client never chooses one); <see cref="Description"/>
/// is what the client reads under the title.
/// </summary>
public sealed record ChecklistItem(string Title, DocumentKind Kind, string? Description = null);

/// <summary>
/// A firm's stored checklist — it exists only once the firm has saved one. An
/// EMPTY <see cref="Items"/> is a stored choice ("we ask for nothing"),
/// distinct from no checklist at all (the shipped default).
/// </summary>
public sealed record DocumentChecklist(
    string FirmId,
    ValueCollection<ChecklistItem> Items,
    DateTimeOffset UpdatedAt,
    string UpdatedBy);

/// <summary>
/// The FIRM half of document requests (ADR 0023 PR 5 / #364; from
/// <c>document_requests.py</c>): the checklist a firm asks every client for.
/// Absent means the shipped default, and reset deletes rather than writing a
/// copy, so a later improvement to the default reaches every firm that never
/// customised it. The CASE half — a request, its statuses, applying the
/// checklist to a case — is 18.7's, beside this.
/// </summary>
public static class DocumentChecklists
{
    /// <summary>A save holds at most this many documents.</summary>
    public const int MaxChecklistItems = 40;

    /// <summary>The cap on an entry's title.</summary>
    public const int MaxTitle = 120;

    /// <summary>The cap on an entry's description.</summary>
    public const int MaxDescription = 1000;

    /// <summary>The paperwork every consumer case needs, in the order a debtor usually gathers it.</summary>
    public static readonly ValueCollection<ChecklistItem> DefaultChecklist =
    [
        new(
            "Pay stubs for the last six months",
            DocumentKind.PayStub,
            "Every pay stub from every job you and your spouse have had in the last six months. If you are paid in cash or are self-employed, a record of what you were paid and when."),
        new(
            "Tax returns for the last two years",
            DocumentKind.TaxReturn,
            "Your federal and state tax returns for the last two years, with every schedule and form that was filed with them."),
        new(
            "Bank statements",
            DocumentKind.BankStatement,
            "The last six months of statements for every checking, savings and other account you have, including accounts held jointly."),
        new(
            "Vehicle titles",
            DocumentKind.Other,
            "The title for each car, truck, motorcycle or other vehicle you own, and the latest statement for any loan on it."),
        new(
            "Mortgage statements",
            DocumentKind.Other,
            "The most recent statement for each mortgage or home-equity loan on property you own. If you rent, a copy of your lease."),
        new(
            "Identification",
            DocumentKind.Identification,
            "A government-issued photo ID, and a document showing your Social Security number."),
    ];

    /// <summary>The checklist in force: the stored one, else the default.</summary>
    public static ValueCollection<ChecklistItem> Resolve(DocumentChecklist? config) => config is null ? DefaultChecklist : config.Items;

    /// <summary>
    /// The identity two entries (or two requests) share when they are the same
    /// document: the title, case- and whitespace-insensitive.
    /// </summary>
    public static string TitleKey(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        return PyText.Casefold(string.Join(" ", PyText.SplitWhitespace(title)));
    }

    /// <summary>
    /// Validate PUT <c>/v1/firm/document-checklist</c>: <c>{"items": [...]}</c>,
    /// the WHOLE list. Errors keyed <c>items.&lt;index&gt;.&lt;field&gt;</c>.
    /// Two entries with the same title are refused — applying the checklist
    /// dedupes by title, so the second could never be requested.
    /// </summary>
    public static ValueCollection<ChecklistItem> ParseUpdate(JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload["items"] is not JsonArray raw)
        {
            throw new FieldValidationException(new Dictionary<string, string> { ["items"] = "Must be a list of documents." });
        }

        if (raw.Count > MaxChecklistItems)
        {
            throw new FieldValidationException(new Dictionary<string, string> { ["items"] = $"A checklist has at most {MaxChecklistItems} documents." });
        }

        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var items = new List<ChecklistItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < raw.Count; index++)
        {
            var item = ParseItem(raw[index], $"items.{index}", errors);
            if (item is null)
            {
                continue;
            }

            if (!seen.Add(TitleKey(item.Title)))
            {
                errors[$"items.{index}.title"] = "Another document has this title.";
                continue;
            }

            items.Add(item);
        }

        if (errors.Count > 0)
        {
            throw new FieldValidationException(errors);
        }

        return items.ToValueCollection();
    }

    /// <summary>
    /// Validate one checklist-shaped entry: a required single-line title, a
    /// known document kind, an optional description. Errors are keyed under
    /// <paramref name="path"/> (the case half's ad-hoc request parser reuses this).
    /// </summary>
    public static ChecklistItem? ParseItem(JsonNode? entry, string path, IDictionary<string, string> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (entry is not JsonObject raw)
        {
            errors[path] = "Must be an object.";
            return null;
        }

        var title = FieldParsers.Text(raw["title"], $"{path}.title", errors, MaxTitle);
        if (title is null && !errors.ContainsKey($"{path}.title"))
        {
            errors[$"{path}.title"] = "A title is required.";
        }

        DocumentKind? kind = null;
        if (Wire.TryParse<DocumentKind>(Json.AsString(raw["kind"]), out var parsedKind))
        {
            kind = parsedKind;
        }
        else
        {
            errors[$"{path}.kind"] = "Must be one of " + string.Join(", ", Wire.Names<DocumentKind>()) + ".";
        }

        var description = FieldParsers.Narrative(raw["description"], $"{path}.description", errors, MaxDescription);
        if (title is null || kind is null || errors.ContainsKey($"{path}.description"))
        {
            return null;
        }

        return new ChecklistItem(title, kind.Value, description);
    }

    /// <summary>The checklist a save writes.</summary>
    public static DocumentChecklist Build(IEnumerable<ChecklistItem> items, string firmId, string updatedBy, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        return new DocumentChecklist(firmId, items.ToValueCollection(), Clock.Now(clock), updatedBy);
    }
}
