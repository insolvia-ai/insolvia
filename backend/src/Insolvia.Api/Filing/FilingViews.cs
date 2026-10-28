using System.Text.Json.Nodes;
using Insolvia.Api.Http;
using Insolvia.Core;
using Insolvia.Core.FilingAuthorizations;
using Insolvia.Core.FilingCredentials;
using Insolvia.Core.Filings;
using Insolvia.Forms;

namespace Insolvia.Api.Filing;

/// <summary>
/// The electronic-filing wire shapes, as the Python serializers wrote them:
/// <c>credential_json</c>, <c>text_json</c> and <c>authorization_json</c>
/// (insolvia_core), <c>filing_json</c> (insolvia_core.filings), and
/// <c>approval_json</c> / <c>basis_json</c> (core/filing_approval.py).
/// Optional keys are absent, never null, wherever Python left them out.
/// </summary>
public static class FilingViews
{
    /// <summary>THE STATUS VIEW of a credential — the only shape a client receives. No password, no seed, no envelope.</summary>
    public static JsonObject Credential(FilingCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        return new JsonObject
        {
            ["id"] = credential.CredentialId,
            ["login"] = credential.Login,
            ["courts"] = new JsonArray([.. credential.Courts.Select(court => (JsonNode)court)]),
            ["status"] = credential.Status.ToWire(),
            ["created_at"] = FlaskJson.Micros(credential.CreatedAt),
            ["updated_at"] = FlaskJson.Micros(credential.UpdatedAt),
        };
    }

    /// <summary>The text an attorney is asked to sign, with what they must post back.</summary>
    public static JsonObject Text()
    {
        var text = FilingAuthorizationRules.AuthorizationText();
        return new JsonObject
        {
            ["version"] = FilingAuthorizationRules.TextVersion,
            ["digest"] = FilingAuthorizationRules.TextDigest(text),
            ["text"] = text,
        };
    }

    /// <summary>The authorization status view: the signature in force (or the stale one), and the version a new signature would be over — with the text.</summary>
    public static JsonObject Authorization(FilingAuthorization? authorization, string firmId, string attorneyId) => new()
    {
        ["text"] = Text(),
        ["current_version"] = FilingAuthorizationRules.TextVersion,
        ["current"] = FilingAuthorizationRules.IsCurrent(authorization, firmId, attorneyId),
        ["signature"] = authorization is null
            ? null
            : new JsonObject
            {
                ["id"] = authorization.AuthorizationId,
                ["text_version"] = authorization.TextVersion,
                ["text_digest"] = authorization.TextDigest,
                ["signed_at"] = FlaskJson.Micros(authorization.SignedAt),
            },
    };

    /// <summary>A filing record as a reader sees it: ids, states, the court's facts and the resolution — never the page's storage key.</summary>
    public static JsonObject Filing(Core.Filings.Filing filing)
    {
        ArgumentNullException.ThrowIfNull(filing);
        var body = new JsonObject
        {
            ["filingId"] = filing.FilingId,
            ["approvalId"] = filing.ApprovalId,
            ["attorneyId"] = filing.AttorneyId,
            ["state"] = filing.State.ToWire(),
            ["court"] = filing.Court,
            ["claimedAt"] = FilingApprovalRecords.Iso(filing.ClaimedAt),
            ["updatedAt"] = FilingApprovalRecords.Iso(filing.UpdatedAt),
            ["history"] = new JsonArray([.. filing.History.Select(step => (JsonNode)new JsonObject { ["state"] = step.State.ToWire(), ["at"] = FilingApprovalRecords.Iso(step.At) })]),
            ["resolvable"] = FilingRules.IsResolvable(filing),
        };
        if (filing.Driver is not null)
        {
            body["driver"] = filing.Driver;
        }

        if (filing.HandBack is { } note)
        {
            var handBack = new JsonObject { ["reason"] = note.Reason, ["stage"] = note.Stage, ["title"] = note.Title, ["action"] = note.Action, ["link"] = note.Link };
            if (note.CourtSaid is not null)
            {
                handBack["courtSaid"] = note.CourtSaid;
            }

            body["handBack"] = handBack;
        }

        if (filing.UnknownReason is not null)
        {
            body["unknownReason"] = filing.UnknownReason;
        }

        if (filing.Confirmation is { } confirmation)
        {
            var captured = new JsonObject
            {
                ["caseNumber"] = confirmation.CaseNumber,
                ["filedAt"] = confirmation.FiledAt,
                ["docketEntries"] = new JsonArray([.. confirmation.DocketEntries.Select(entry => (JsonNode)entry)]),
            };
            if (confirmation.ReceiptNumber is not null)
            {
                captured["receiptNumber"] = confirmation.ReceiptNumber;
            }

            if (confirmation.FeeDue is not null)
            {
                captured["feeDue"] = confirmation.FeeDue;
            }

            body["confirmation"] = captured;
        }

        if (filing.ReceiptDocumentId is not null)
        {
            body["receiptDocumentId"] = filing.ReceiptDocumentId;
        }

        if (filing.FollowUps.Count > 0)
        {
            body["followUps"] = new JsonArray([.. filing.FollowUps.Select(item => (JsonNode)item)]);
        }

        if (filing.Resolution is { } resolution)
        {
            var resolved = new JsonObject
            {
                ["outcome"] = resolution.Outcome.ToWire(),
                ["resolvedBy"] = resolution.ResolvedBy,
                ["resolvedAt"] = FilingApprovalRecords.Iso(resolution.ResolvedAt),
                ["docketCheckedAt"] = FilingApprovalRecords.Iso(resolution.DocketCheckedAt),
            };
            if (resolution.CaseNumber is not null)
            {
                resolved["caseNumber"] = resolution.CaseNumber;
            }

            if (resolution.FiledAt is { } filedAt)
            {
                resolved["filedAt"] = FlaskJson.Date(filedAt);
            }

            if (resolution.ConfirmationDocumentId is not null)
            {
                resolved["confirmationDocumentId"] = resolution.ConfirmationDocumentId;
            }

            body["resolution"] = resolved;
        }

        return body;
    }

    /// <summary>What a client sees of an approval.</summary>
    public static JsonObject Approval(FilingApproval approval, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(approval);
        var body = new JsonObject
        {
            ["id"] = approval.ApprovalId,
            ["filingId"] = approval.FilingId,
            ["status"] = FilingApprovalRecords.EffectiveStatus(approval, now),
            ["digest"] = approval.Digest,
            ["approvedBy"] = approval.AttorneyId,
            ["approvedAt"] = FilingApprovalRecords.Iso(approval.ApprovedAt),
            ["expiresAt"] = FilingApprovalRecords.Iso(DateTimeOffset.FromUnixTimeSeconds(approval.ExpiresAt)),
            ["credentialId"] = approval.CredentialId,
            ["court"] = approval.Court,
            ["division"] = approval.Division,
            ["packetId"] = approval.PacketId,
        };
        if (approval.ConsumedAt is { } consumed)
        {
            body["consumedAt"] = FilingApprovalRecords.Iso(consumed);
        }

        if (approval.VoidedAt is { } voided)
        {
            body["voidedAt"] = FilingApprovalRecords.Iso(voided);
        }

        if (approval.VoidReason is not null)
        {
            body["voidReason"] = approval.VoidReason;
        }

        return body;
    }

    /// <summary>
    /// WHAT THE ATTORNEY IS SHOWN: the court, the documents in docket order
    /// with their sizes and digests, the checklist, the fee handling and the
    /// digest over all of it. Never the debtor's data, never a tax identifier.
    /// </summary>
    public static JsonObject Basis(ApprovalBasis basis)
    {
        ArgumentNullException.ThrowIfNull(basis);
        var set = basis.FilingSet;
        var documents = new JsonArray();
        for (var index = 0; index < set.Documents.Count; index++)
        {
            var document = set.Documents[index];
            var entry = new JsonObject
            {
                ["position"] = index + 1,
                ["key"] = document.Key,
                ["title"] = document.Title,
                ["fileName"] = document.FileName,
                ["source"] = document.Source,
                ["handling"] = document.Handling,
            };
            if (document.Part is { } part)
            {
                var file = new JsonObject { ["byteSize"] = part.ByteSize };
                if (part.PageCount is { } pages)
                {
                    file["pageCount"] = pages;
                }

                if (part.Sha256 is { } digest)
                {
                    file["sha256"] = digest;
                }

                entry["file"] = file;
            }

            if (document.Note.Length > 0)
            {
                entry["note"] = document.Note;
            }

            documents.Add(entry);
        }

        var checklist = new JsonArray([.. set.Checklist.Select(item =>
        {
            var row = new JsonObject { ["id"] = item.Id, ["status"] = item.Status, ["title"] = item.Title, ["detail"] = item.Detail };
            if (item.Link is not null)
            {
                row["link"] = item.Link;
            }

            return (JsonNode)row;
        })]);
        var fee = new JsonObject
        {
            ["handling"] = basis.Fee.Handling,
            ["verified"] = basis.Fee.Verified,
            ["detail"] = "Insolvia stores and enters no card details: the filing stops at the court's payment step and is handed back to you to pay in your own CM/ECF session, within the court's window.",
        };
        if (basis.Fee.Deadline is not null)
        {
            fee["deadline"] = basis.Fee.Deadline;
        }

        var body = new JsonObject
        {
            ["scheme"] = ApprovalDigest.Scheme,
            ["digest"] = basis.Digest,
            ["ready"] = basis.Ready,
            ["blockers"] = new JsonArray([.. basis.Blockers.Select(blocker => (JsonNode)blocker)]),
            ["registryRelease"] = set.RegistryRelease,
            ["documents"] = documents,
            ["checklist"] = checklist,
            ["fee"] = fee,
            ["signInMaxAgeSeconds"] = FilingApprovals.SignInMaxAgeSeconds,
            ["approvalTtlSeconds"] = FilingApprovals.TtlSeconds,
        };
        if (set.CourtCode is not null && set.CourtName is not null)
        {
            var court = new JsonObject { ["code"] = set.CourtCode, ["name"] = set.CourtName };
            if (set.DivisionName is not null)
            {
                court["divisionName"] = set.DivisionName;
            }

            body["court"] = court;
        }

        if (set.Packet is { } packet)
        {
            body["packet"] = new JsonObject { ["id"] = packet.Id, ["createdAt"] = packet.CreatedAt, ["sha256"] = packet.Sha256, ["byteSize"] = packet.ByteSize };
        }

        return body;
    }
}
