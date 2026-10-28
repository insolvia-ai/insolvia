using System.Text.Json.Nodes;
using Insolvia.Core.Claims;
using Insolvia.Core.Fields;

namespace Insolvia.Core.LibraryCreditors;

/// <summary>
/// One firm's reusable creditor record (issue 13.9 / #350; from
/// <c>library_creditors.py</c>). NOT a case entity: it belongs to the FIRM,
/// carries no provenance (it is the firm's own authored data), and what
/// travels to a case is a COPY naming this row in its provenance — editing the
/// library later must never rewrite a filed schedule. The wire shape is
/// snake_case, the case domain's, because the address and notice-party shapes
/// are the case domain's own.
/// </summary>
public sealed record LibraryCreditor(
    string FirmId,
    string Id,
    string Name,
    Address Address,
    ValueCollection<NoticeParty> AdditionalNoticeParties,
    bool Preferred,
    string? Notes,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>A validated WHOLE-RECORD save — POST to create, PUT to replace (no "null means unchanged").</summary>
public sealed record LibraryCreditorDraft(
    string Name,
    Address Address,
    ValueCollection<NoticeParty> AdditionalNoticeParties,
    bool Preferred,
    string? Notes);

/// <summary>Parsing and construction of library creditors.</summary>
public static class LibraryCreditorRules
{
    /// <summary>The cap on a creditor name.</summary>
    public const int MaxName = 200;

    /// <summary>The cap on the notes.</summary>
    public const int MaxNotes = 2000;

    /// <summary>
    /// Validate POST/PUT <c>/v1/firm/creditors</c>. <c>name</c> is the one
    /// required field — it is the one fact the picker shows. Unknown keys are ignored.
    /// </summary>
    public static LibraryCreditorDraft Parse(JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var name = FieldParsers.Text(payload["name"], "name", errors, MaxName);
        if (name is null && !errors.ContainsKey("name"))
        {
            errors["name"] = "A creditor name is required.";
        }

        var address = FieldParsers.ParseAddress(payload["address"], "address", errors);
        var additional = ParseAdditionalNoticeParties(payload["additional_notice_parties"], errors);
        var preferred = FieldParsers.Boolean(payload["preferred"], "preferred", errors);
        var notes = FieldParsers.Narrative(payload["notes"], "notes", errors, MaxNotes);

        if (errors.Count > 0 || name is null)
        {
            throw new FieldValidationException(errors);
        }

        return new LibraryCreditorDraft(name, address, additional, preferred ?? false, notes);
    }

    /// <summary>A new library creditor with a minted id.</summary>
    public static LibraryCreditor Create(LibraryCreditorDraft draft, string firmId, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var now = Clock.Now(clock);
        return new LibraryCreditor(firmId, Guid.NewGuid().ToString(), draft.Name, draft.Address, draft.AdditionalNoticeParties, draft.Preferred, draft.Notes, now, now);
    }

    /// <summary>
    /// The draft applied, keeping <c>Id</c>/<c>FirmId</c>/<c>CreatedAt</c> — a
    /// case's copy names this row by id, so the id survives every edit.
    /// </summary>
    public static LibraryCreditor Replace(LibraryCreditor existing, LibraryCreditorDraft draft, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(draft);
        return existing with
        {
            Name = draft.Name,
            Address = draft.Address,
            AdditionalNoticeParties = draft.AdditionalNoticeParties,
            Preferred = draft.Preferred,
            Notes = draft.Notes,
            UpdatedAt = Clock.Now(clock),
        };
    }

    // The shared notice-party parser writes `notice_parties[…]` into every
    // message; this field is `additional_notice_parties`, so the prefix is
    // rewritten before the caller sees a field it never sent.
    private static ValueCollection<NoticeParty> ParseAdditionalNoticeParties(JsonNode? value, Dictionary<string, string> errors)
    {
        var local = new Dictionary<string, string>(StringComparer.Ordinal);
        var parties = NoticeParties.Parse(value, local);
        foreach (var (path, message) in local)
        {
            var at = path.IndexOf("notice_parties", StringComparison.Ordinal);
            var rewritten = at < 0 ? path : string.Concat(path.AsSpan(0, at), "additional_notice_parties", path.AsSpan(at + "notice_parties".Length));
            errors[rewritten] = message;
        }

        return parties;
    }
}
