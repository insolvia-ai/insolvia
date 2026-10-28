using System.Text.Json.Nodes;
using Insolvia.Core.CaseEntities;
using Insolvia.Core.Cases;
using Insolvia.Core.Debtors;
using Insolvia.Core.Fields;
using Insolvia.Core.Provenance;

namespace Insolvia.Core.FirmClients;

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
    /// The identity a case copies from a client, and the ONLY fields
    /// <see cref="DiffersFromClient"/> compares. Date of birth is not here (no
    /// form prints it), nor the tax id (entered on the case for now).
    /// </summary>
    public static readonly IReadOnlyList<string> CopiedFields =
        ["name", "other_names_used", "residence_address", "mailing_address", "phone", "mobile", "email"];

    /// <summary>
    /// A NEW debtor of <paramref name="target"/> carrying the client's identity,
    /// every copied field with provenance <c>{source: client, client_id}</c> —
    /// round-tripped through the debtor parser with invariant 1 ENFORCED, so a
    /// value the debtor parser would refuse fails here, at the copy.
    /// </summary>
    public static Debtor DebtorFromClient(FirmClient client, Case target, FilingRole filingRole, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(target);
        var body = CopiedBody(ClientCopied(client));
        var entry = new ProvenanceEntry(ProvenanceSource.Client) { ClientId = client.Id };
        var provenance = ProvenanceRules.PopulatedPaths(body).ToDictionary(path => path, _ => entry, StringComparer.Ordinal);
        body["provenance"] = ProvenanceRules.ToJson(provenance);
        var draft = DebtorRules.Parse(body);
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
        var ours = Leaves(CopiedBody(DebtorCopied(debtor.Body)));
        var theirs = Leaves(CopiedBody(ClientCopied(client)));
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
        var body = DebtorRules.BodyNode(debtor.Body);
        var copied = BodyJson.ToNode(ClientCopied(client));
        foreach (var key in CopiedFields)
        {
            body[key] = copied[key]?.DeepClone();
        }

        var entry = new ProvenanceEntry(ProvenanceSource.Client) { ClientId = client.Id };
        var provenance = new Dictionary<string, ProvenanceEntry>(StringComparer.Ordinal);
        foreach (var (path, kept) in debtor.Provenance)
        {
            if (!IsCopiedRoot(path))
            {
                provenance[path] = kept;
            }
        }

        foreach (var path in ProvenanceRules.PopulatedPaths(CopiedBody(ClientCopied(client))))
        {
            provenance[path] = entry;
        }

        var payload = BodyJson.Prune(body);
        payload["provenance"] = ProvenanceRules.ToJson(provenance);
        return DebtorRules.Replace(debtor, DebtorRules.Parse(payload), debtor.TaxId, clock);
    }

    /// <summary>
    /// ADR 0022's <em>update client from this case</em>: the client with every
    /// copied field replaced by what the case says — the whole-record client
    /// write. The client's own fields and everything server-owned are kept;
    /// re-parsed through the client parser, so an emptied name is refused with
    /// the client's own message. The debtor is not written.
    /// </summary>
    public static FirmClient ClientUpdatedFromDebtor(FirmClient client, Debtor debtor, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(debtor);
        var whole = BodyJson.ToNode(new FirmClientDraft(
            client.Name,
            client.OtherNamesUsed,
            client.DateOfBirth,
            client.ResidenceAddress,
            client.MailingAddress,
            client.Phone,
            client.Mobile,
            client.Email,
            client.LeadSource,
            client.ReferredBy,
            client.FirstRetainedAt));
        var ours = BodyJson.ToNode(DebtorCopied(debtor.Body));
        foreach (var key in CopiedFields)
        {
            whole[key] = ours[key]?.DeepClone();
        }

        return FirmClientRules.Replace(client, FirmClientRules.Parse(BodyJson.Prune(whole), clock: clock), clock);
    }

    /// <summary>Whether a provenance path addresses one of <see cref="CopiedFields"/>.</summary>
    private static bool IsCopiedRoot(string path)
    {
        var root = path.Split('.', 2)[0].Split('[', 2)[0];
        return CopiedFields.Contains(root, StringComparer.Ordinal);
    }

    private static CopiedIdentity ClientCopied(FirmClient client) =>
        new(client.Name, client.OtherNamesUsed, client.ResidenceAddress, client.MailingAddress, client.Phone, client.Mobile, client.Email);

    private static CopiedIdentity DebtorCopied(DebtorBody body) =>
        new(body.Name, body.OtherNamesUsed, body.ResidenceAddress, body.MailingAddress, body.Phone, body.Mobile, body.Email);

    private static JsonObject CopiedBody(CopiedIdentity identity) => BodyJson.Prune(BodyJson.ToNode(identity));

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

    /// <summary>The seven copied members, as one record, so both sides serialize alike.</summary>
    private sealed record CopiedIdentity(
        PersonName Name,
        ValueCollection<OtherName> OtherNamesUsed,
        Address ResidenceAddress,
        Address MailingAddress,
        string? Phone,
        string? Mobile,
        string? Email);
}
