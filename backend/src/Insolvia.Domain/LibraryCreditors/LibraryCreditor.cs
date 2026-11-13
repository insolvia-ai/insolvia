using System.ComponentModel.DataAnnotations;
using Insolvia.Domain.Claims;
using Insolvia.Domain.Fields;

namespace Insolvia.Domain.LibraryCreditors;

/// <summary>
/// One firm's reusable creditor record (issue 13.9 / #350; from
/// <c>library_creditors.py</c>). NOT a case entity: it belongs to the FIRM,
/// carries no provenance (it is the firm's own authored data), and what
/// travels to a case is a COPY naming this row in its provenance — editing the
/// library later must never rewrite a filed schedule. The address and
/// notice-party shapes are the case domain's own.
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

/// <summary>
/// A library creditor as written — <c>POST /v1/firm/creditors</c> to create,
/// <c>PUT …/{creditorId}</c> to replace: a WHOLE record, so a PUT clears what
/// it omits (no "null means unchanged").
/// </summary>
/// <param name="Name">The creditor's name — the one required field, the one fact the picker shows.</param>
/// <param name="Address">The creditor's address.</param>
/// <param name="AdditionalNoticeParties">Others to notify about a debt to this creditor.</param>
/// <param name="Preferred">Whether to pin it to the top of the picker.</param>
/// <param name="Notes">The firm's notes.</param>
public sealed record LibraryCreditorDraft(
    [property: Required(ErrorMessage = "A creditor name is required."), Text(LibraryCreditorRules.MaxName)] string? Name,
    Address? Address = null,
    ValueCollection<NoticeParty>? AdditionalNoticeParties = null,
    bool Preferred = false,
    [property: Narrative(LibraryCreditorRules.MaxNotes)] string? Notes = null)
{
    /// <summary>The creditor's address; an empty one when none was given.</summary>
    public Address Address { get; init; } = Address ?? new();

    /// <summary>The notice parties, each addressed by its id.</summary>
    [Rows]
    public ValueCollection<NoticeParty> AdditionalNoticeParties { get; init; } = AdditionalNoticeParties ?? [];
}

/// <summary>Construction of library creditors.</summary>
public static class LibraryCreditorRules
{
    /// <summary>The cap on a creditor name.</summary>
    public const int MaxName = 200;

    /// <summary>The cap on the notes.</summary>
    public const int MaxNotes = 2000;

    /// <summary>A new library creditor with a minted id.</summary>
    public static LibraryCreditor Create(LibraryCreditorDraft draft, string firmId, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var now = Clock.Now(clock);
        return new LibraryCreditor(firmId, Guid.NewGuid().ToString(), draft.Name!, draft.Address, draft.AdditionalNoticeParties, draft.Preferred, draft.Notes, now, now);
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
            Name = draft.Name!,
            Address = draft.Address,
            AdditionalNoticeParties = draft.AdditionalNoticeParties,
            Preferred = draft.Preferred,
            Notes = draft.Notes,
            UpdatedAt = Clock.Now(clock),
        };
    }
}
