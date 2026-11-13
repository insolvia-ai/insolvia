using System.Text.Json.Nodes;
using Insolvia.Domain.CaseEntities;
using Insolvia.Domain.Cases;
using Insolvia.Domain.Debtors;
using Insolvia.Domain.Fields;
using Insolvia.Domain.Provenance;

namespace Insolvia.Domain.FirmClients;

/// <summary>
/// The copy of a client's identity onto a case, and back (ADR 0022; the four
/// copy functions of <c>firm_clients.py</c>). A debtor is the client's
/// identity COPIED into one case: divergence is shown, never silently
/// resolved, and the two acts that resolve it — re-copy, update the client —
/// are explicit writes.
/// </summary>
public static class ClientCopies
{
    /// <summary>
    /// The identity a case copies from a client, as the debtor body names it,
    /// and the ONLY fields <see cref="DiffersFromClient"/> compares. Date of
    /// birth is not here (no form prints it), nor the tax id (entered on the
    /// case for now).
    /// </summary>
    public static readonly IReadOnlyList<string> CopiedFields =
        ["name", "otherNamesUsed", "residenceAddress", "mailingAddress", "phone", "mobile", "email"];

    /// <summary>
    /// A NEW debtor of <paramref name="target"/> carrying the client's identity,
    /// every copied field with provenance <c>{source: client, client_id}</c> —
    /// checked as a debtor write is, so a value a debtor save would refuse
    /// fails here, at the copy.
    /// </summary>
    public static Debtor DebtorFromClient(FirmClient client, Case target, FilingRole filingRole, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(target);
        var body = Copied(DebtorBody.Empty, client);
        RecordValidator.ThrowIfInvalid(body, BodyJson.Options);
        var entry = new ProvenanceEntry(ProvenanceSource.Client) { ClientId = client.Id };
        var provenance = ProvenanceRules.PopulatedPaths(DebtorRules.BodyNode(body)).ToDictionary(path => path, _ => entry, StringComparer.Ordinal);
        var draft = DebtorRules.Checked(new DebtorDraft(body, Provenance: provenance));
        return DebtorRules.Create(draft, target.Id, filingRole, clientId: client.Id, caseCreatedAt: target.CreatedAt, clock: clock);
    }

    /// <summary>
    /// The copied field paths where <paramref name="debtor"/> and
    /// <paramref name="client"/> now disagree, sorted — a value only one of
    /// them holds differs as much as a changed one. Nothing is written.
    /// </summary>
    public static IReadOnlyList<string> DiffersFromClient(Debtor debtor, FirmClient client)
    {
        ArgumentNullException.ThrowIfNull(debtor);
        ArgumentNullException.ThrowIfNull(client);
        var ours = Leaves(CopiedBody(debtor.Body));
        var theirs = Leaves(CopiedBody(Copied(DebtorBody.Empty, client)));
        return [.. ours.Keys.Union(theirs.Keys, StringComparer.Ordinal)
            .Where(path => !JsonNode.DeepEquals(ours.GetValueOrDefault(path), theirs.GetValueOrDefault(path)))
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// ADR 0022's <em>re-copy from client</em>: every copied field replaced by
    /// the client's, each carrying <c>{source: client, client_id}</c> again. ONLY
    /// the copied fields move — venue, counselling, the signature date,
    /// employer ids and the tax id keep their values and provenance; a copied
    /// field the client leaves empty becomes empty (a copy, not a merge). The
    /// caller refuses a filed case.
    /// </summary>
    public static Debtor RecopyFromClient(Debtor debtor, FirmClient client, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(debtor);
        ArgumentNullException.ThrowIfNull(client);
        var body = Copied(debtor.Body, client);
        var entry = new ProvenanceEntry(ProvenanceSource.Client) { ClientId = client.Id };
        var provenance = new Dictionary<string, ProvenanceEntry>(StringComparer.Ordinal);
        foreach (var (path, kept) in debtor.Provenance)
        {
            if (!IsCopiedRoot(path))
            {
                provenance[path] = kept;
            }
        }

        foreach (var path in ProvenanceRules.PopulatedPaths(CopiedBody(body)))
        {
            provenance[path] = entry;
        }

        return DebtorRules.Replace(debtor, DebtorRules.Checked(new DebtorDraft(body, Provenance: provenance)), debtor.TaxId, clock);
    }

    /// <summary>
    /// ADR 0022's <em>update client from this case</em>: the client with every
    /// copied field replaced by what the case says — the whole-record client
    /// write. The client's own fields and everything server-owned are kept;
    /// checked as a client save is, so an emptied name is refused with the
    /// client's own message. The debtor is not written.
    /// </summary>
    public static FirmClient ClientUpdatedFromDebtor(FirmClient client, Debtor debtor, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(debtor);
        var draft = FirmClientRules.DraftOf(client) with
        {
            Name = debtor.Body.Name,
            OtherNamesUsed = debtor.Body.OtherNamesUsed,
            ResidenceAddress = debtor.Body.ResidenceAddress,
            MailingAddress = debtor.Body.MailingAddress,
            Phone = debtor.Body.Phone,
            Mobile = debtor.Body.Mobile,
            Email = debtor.Body.Email,
        };
        RecordValidator.ThrowIfInvalid(draft, BodyJson.Options);
        return FirmClientRules.Replace(client, draft, clock);
    }

    /// <summary>Whether a provenance path addresses one of <see cref="CopiedFields"/>.</summary>
    private static bool IsCopiedRoot(string path)
    {
        var root = path.Split('.', 2)[0].Split('[', 2)[0];
        return CopiedFields.Contains(root, StringComparer.Ordinal);
    }

    // The debtor body with the client's identity in place of its own.
    private static DebtorBody Copied(DebtorBody body, FirmClient client) => body with
    {
        Name = client.Name,
        OtherNamesUsed = client.OtherNamesUsed,
        ResidenceAddress = client.ResidenceAddress,
        MailingAddress = client.MailingAddress,
        Phone = client.Phone,
        Mobile = client.Mobile,
        Email = client.Email,
    };

    // The copied members of a debtor body, as provenance paths address them.
    private static JsonObject CopiedBody(DebtorBody body)
    {
        var whole = DebtorRules.BodyNode(body);
        return new JsonObject([.. whole.Where(pair => CopiedFields.Contains(pair.Key)).Select(pair => KeyValuePair.Create(pair.Key, pair.Value?.DeepClone()))]);
    }

    /// <summary>
    /// Field path to value for every populated leaf, list elements addressed by
    /// their <c>id</c> — the provenance grammar, so a path here is one the
    /// debtor screen can look provenance up for.
    /// </summary>
    private static Dictionary<string, JsonNode?> Leaves(JsonNode? record, string prefix = "")
    {
        var output = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        switch (record)
        {
            case JsonObject map:
                foreach (var (key, value) in map)
                {
                    if (key == "id" && prefix.EndsWith(']'))
                    {
                        continue;
                    }

                    foreach (var pair in Leaves(value, prefix.Length > 0 ? $"{prefix}.{key}" : key))
                    {
                        output[pair.Key] = pair.Value;
                    }
                }

                return output;

            case JsonArray list:
                foreach (var element in list)
                {
                    var elementId = element is JsonObject obj ? Json.AsString(obj["id"]) ?? obj["id"]?.ToJsonString() : null;
                    foreach (var pair in Leaves(element, $"{prefix}[{elementId ?? "None"}]"))
                    {
                        output[pair.Key] = pair.Value;
                    }
                }

                return output;

            default:
                if (prefix.Length > 0)
                {
                    output[prefix] = record;
                }

                return output;
        }
    }
}
