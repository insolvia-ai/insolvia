namespace Insolvia.Domain.FirmClients;

/// <summary>
/// The transitions and the directory order of firm clients (from
/// <c>firm_clients.py</c>). The copy onto a case and back
/// (<c>debtor_from_client</c>, <c>differs_from_client</c>,
/// <c>recopy_from_client</c>, <c>client_updated_from_debtor</c>) needs the
/// debtor record and is the case half's (18.7).
/// </summary>
public static class FirmClientRules
{
    /// <summary>What to say when a link raced a change on the client row that has since changed back.</summary>
    public const string LinkRaced = "That client changed while this was being saved — reload and try again.";

    /// <summary>A new, active client.</summary>
    public static FirmClient Create(FirmClientDraft draft, string firmId, string createdBy, TimeProvider? clock = null)
    {
        RefuseFutureBirth(draft, clock);
        var now = Clock.Now(clock);
        return Apply(new FirmClient(Guid.NewGuid().ToString(), firmId, FirmClientStatus.Active, now, now, createdBy), draft);
    }

    /// <summary>
    /// The draft applied over <paramref name="existing"/> — a whole record,
    /// so what the draft leaves out is cleared — keeping what the caller does
    /// not own: id, firm, creation stamps, status, the tax-id pointer, the
    /// merge attributes and the stage.
    /// </summary>
    public static FirmClient Replace(FirmClient existing, FirmClientDraft draft, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(existing);
        RefuseFutureBirth(draft, clock);
        return Apply(existing, draft) with { UpdatedAt = Clock.Now(clock) };
    }

    /// <summary>The status write, and nothing else.</summary>
    public static FirmClient SetStatus(FirmClient existing, FirmClientStatus status, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(existing);
        return existing with { Status = status, UpdatedAt = Clock.Now(clock) };
    }

    /// <summary>
    /// THE directory order — surname, given, middle, then id — case-folded so
    /// <c>de la Cruz</c> does not sort after every capitalised name. Every
    /// store sorts with this, so two stores cannot disagree.
    /// </summary>
    public static ValueCollection<FirmClient> Sorted(IEnumerable<FirmClient> clients)
    {
        ArgumentNullException.ThrowIfNull(clients);
        return clients
            .OrderBy(client => TextRules.Casefold(client.Name.Surname ?? string.Empty), StringComparer.Ordinal)
            .ThenBy(client => TextRules.Casefold(client.Name.Given ?? string.Empty), StringComparer.Ordinal)
            .ThenBy(client => TextRules.Casefold(client.Name.Middle ?? string.Empty), StringComparer.Ordinal)
            .ThenBy(client => client.Id, StringComparer.Ordinal)
            .ToValueCollection();
    }

    /// <summary>
    /// What to tell a caller whose link write was refused by its condition on
    /// the client row (re-read after the refusal): the read check's own words,
    /// so a merge that raced the link reads like one that landed first.
    /// </summary>
    public static string RefusalForRefusedLink(FirmClient? client) =>
        client is null ? "No such client." : client.RefusalForNewCase() ?? LinkRaced;

    private static FirmClient Apply(FirmClient client, FirmClientDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return client with
        {
            Name = draft.Name,
            OtherNamesUsed = draft.OtherNamesUsed,
            DateOfBirth = draft.DateOfBirth,
            ResidenceAddress = draft.ResidenceAddress,
            MailingAddress = draft.MailingAddress,
            Phone = draft.Phone,
            Mobile = draft.Mobile,
            Email = draft.Email,
            LeadSource = draft.LeadSource,
            ReferredBy = draft.ReferredBy,
            FirstRetainedAt = draft.FirstRetainedAt,
        };
    }

    /// <summary>The caller-editable half of <paramref name="client"/>, as a save would write it.</summary>
    public static FirmClientDraft DraftOf(FirmClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        return new(client.Name, client.OtherNamesUsed, client.DateOfBirth, client.ResidenceAddress, client.MailingAddress, client.Phone, client.Mobile, client.Email, client.LeadSource, client.ReferredBy, client.FirstRetainedAt);
    }

    // The one rule that needs the clock, so the record cannot check it.
    private static void RefuseFutureBirth(FirmClientDraft draft, TimeProvider? clock)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.DateOfBirth > Clock.Today(clock))
        {
            throw new FieldValidationException(new Dictionary<string, string> { ["dateOfBirth"] = "A date of birth cannot be in the future." });
        }
    }
}
