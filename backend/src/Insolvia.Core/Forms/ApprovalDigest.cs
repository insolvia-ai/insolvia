using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Insolvia.Core;
using Insolvia.Core.CaseEntities;
using Insolvia.Core.Cases;
using Insolvia.Core.Courts;
using Insolvia.Core.Debtors;

namespace Insolvia.Forms;

/// <summary>The fee handling as it stands: handed back at the court's payment step, with the court's rule as recorded.</summary>
public sealed record FeeHandling(string Handling, string? Deadline, string Note, bool Verified);

/// <summary>What an approval would be bound to, computed now: the digest, the filing set, the fee handling, and what blocks approving.</summary>
public sealed record ApprovalBasis(string Digest, FilingSet FilingSet, FeeHandling Fee, IReadOnlyList<string> Blockers)
{
    /// <summary>Nothing blocks the approval.</summary>
    public bool Ready => Blockers.Count == 0;
}

/// <summary>
/// The attorney's per-filing approval's basis and its digest (from
/// <c>core/filing_approval.py</c>, ADR 0024 build PR 6) — the code the API's
/// approve route and the filing worker share, which is why it lives here and
/// not in a host. The approval's store, routes and queue are not here.
/// </summary>
/// <remarks>
/// <para>
/// <b>The digest</b> is the SHA-256 (lowercase hex) of ONE canonical JSON
/// document — the definition ported unchanged, computed over the .NET packet's
/// own bytes from cut-over on (ADR 0028; there are no live approvals to
/// invalidate):
/// </para>
/// <list type="bullet">
/// <item><c>scheme</c>: <see cref="Scheme"/>.</item>
/// <item><c>court</c>: the case's court and division codes and the court-registry release.</item>
/// <item><c>packet</c>: the filing-set packet's id, zip sha256 and byte size, or null.</item>
/// <item><c>documents</c>: every document in docket order — position, key, file name, handling,
/// source, and for a packet file its name, sha256, byte size and page count.</item>
/// <item><c>record</c>: the case (id, firm, chapter, court, division, district, exemption set), every
/// debtor (filing role, updated-at, pruned body, tax-id reference — kind, last four, ref, never the
/// number) and every collection's records (id, updated-at, amended, body).</item>
/// <item><c>fee</c>: handling, deadline, note, verified.</item>
/// </list>
/// <para>
/// Canonical means Python's <c>json.dumps(sort_keys=True, separators=(",", ":"),
/// ensure_ascii=False)</c> in UTF-8: keys sorted by code point, no whitespace,
/// non-ASCII written raw, decimals as their exact text, dates ISO, timestamps
/// as the stored <c>YYYY-MM-DDTHH:MM:SS.mmmZ</c>. <see cref="CanonicalBytes"/>
/// is that encoder; FilingSetTests pins its bytes.
/// </para>
/// </remarks>
public static class ApprovalDigest
{
    /// <summary>What the digest covers, by name: a change to the document is a new scheme.</summary>
    public const string Scheme = "insolvia-filing-approval/1";

    /// <summary>Insolvia stores and enters no card details: the filing is handed back at the court's payment step.</summary>
    public const string FeeHandlingMode = "hand_back_at_payment";

    /// <summary>The filing set as it stands, its fee handling, its blockers and the digest — what the approve route, the status read and the worker's consume all compute.</summary>
    public static ApprovalBasis Basis(CaseData data, IReadOnlyList<Packet> packets, CourtsRelease release, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(release);
        var filingSet = FilingSets.Build(data, packets, release, asOf);
        var district = data.Case.Court is { } court ? release.District(court) : null;
        var fee = Fee(district);
        return new ApprovalBasis(DigestOf(BasisDocument(data, filingSet, fee)), filingSet, fee, Blockers(data, filingSet));
    }

    /// <summary>The fee handling for a district (or none).</summary>
    public static FeeHandling Fee(CourtDistrict? district)
    {
        var rule = district?.Opening.FeeRule;
        var value = rule?.Value;
        return new FeeHandling(FeeHandlingMode, value?.Deadline, value?.Note ?? string.Empty, rule?.Verified ?? false);
    }

    /// <summary>The document the digest is taken over. Holds PII: hash it, never store or return it.</summary>
    public static JsonObject BasisDocument(CaseData data, FilingSet filingSet, FeeHandling fee)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(filingSet);
        ArgumentNullException.ThrowIfNull(fee);
        return new JsonObject
        {
            ["scheme"] = Scheme,
            ["court"] = new JsonObject
            {
                ["code"] = data.Case.Court,
                ["division"] = data.Case.Division,
                ["registryRelease"] = filingSet.RegistryRelease,
            },
            ["packet"] = filingSet.Packet is { } packet
                ? new JsonObject { ["id"] = packet.Id, ["sha256"] = packet.Sha256, ["byteSize"] = packet.ByteSize }
                : null,
            ["documents"] = new JsonArray([.. filingSet.Documents.Select((document, index) => (JsonNode)new JsonObject
            {
                ["position"] = index + 1,
                ["key"] = document.Key,
                ["fileName"] = document.FileName,
                ["handling"] = document.Handling,
                ["source"] = document.Source,
                ["file"] = document.Part is { } part
                    ? new JsonObject { ["name"] = part.Name, ["sha256"] = part.Sha256, ["byteSize"] = part.ByteSize, ["pageCount"] = part.PageCount }
                    : null,
            })]),
            ["record"] = Record(data),
            ["fee"] = new JsonObject
            {
                ["handling"] = fee.Handling,
                ["deadline"] = fee.Deadline,
                ["note"] = fee.Note,
                ["verified"] = fee.Verified,
            },
        };
    }

    /// <summary>The SHA-256 of the canonical bytes, lowercase hex.</summary>
    public static string DigestOf(JsonObject document) => Convert.ToHexStringLower(SHA256.HashData(CanonicalBytes(document)));

    /// <summary>THE canonical encoding (the class remarks).</summary>
    public static byte[] CanonicalBytes(JsonNode? document)
    {
        var builder = new StringBuilder();
        Write(builder, document);
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    /// <summary>The stored timestamp format: UTC, milliseconds, a literal Z (Python's <c>isoformat(timespec="milliseconds")</c>).</summary>
    public static string Timestamp(DateTimeOffset moment) =>
        moment.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static JsonObject Record(CaseData data)
    {
        var @case = data.Case;
        var collections = new JsonObject();
        foreach (var (member, entities) in data.Collections())
        {
            collections[member] = new JsonArray([.. entities.Select(entity => (JsonNode)new JsonObject
            {
                ["id"] = entity.Id,
                ["updatedAt"] = Timestamp(entity.UpdatedAt),
                ["amended"] = entity.Amended,
                ["body"] = entity.BodyNode(),
            })]);
        }

        return new JsonObject
        {
            ["case"] = new JsonObject
            {
                ["id"] = @case.Id,
                ["firmId"] = @case.FirmId,
                ["chapter"] = @case.Chapter,
                ["court"] = @case.Court,
                ["division"] = @case.Division,
                ["district"] = @case.District,
                ["exemptionSet"] = @case.ExemptionSet is { } set ? set.ToWire() : null,
            },
            ["debtors"] = new JsonArray([.. data.Debtors.Select(debtor => (JsonNode)new JsonObject
            {
                ["filingRole"] = debtor.FilingRole.ToWire(),
                ["updatedAt"] = Timestamp(debtor.UpdatedAt),
                ["body"] = BodyJson.Prune(DebtorRules.BodyNode(debtor.Body)),
                ["taxId"] = debtor.TaxId is { } tax
                    ? new JsonObject { ["kind"] = tax.Kind.ToWire(), ["lastFour"] = tax.LastFour, ["ref"] = tax.Ref }
                    : null,
            })]),
            ["collections"] = collections,
        };
    }

    private static IReadOnlyList<string> Blockers(CaseData data, FilingSet filingSet)
    {
        if (CaseRules.IsFiled(data.Case.Status))
        {
            return ["filed"];
        }

        var blockers = filingSet.Checklist.Where(item => item.Status == "missing").Select(item => item.Id).ToList();
        if (filingSet.Packet is not null && filingSet.Documents.Any(d => d.Source == "packet" && d.Part?.Sha256 is null))
        {
            blockers.Add("file_digests");
        }

        return [.. blockers.Distinct(StringComparer.Ordinal)];
    }

    // ── json.dumps(sort_keys=True, separators=(",", ":"), ensure_ascii=False) ──

    private static readonly JsonSerializerOptions Raw = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static void Write(StringBuilder builder, JsonNode? node)
    {
        switch (node)
        {
            case null:
                builder.Append("null");
                break;
            case JsonObject map:
                builder.Append('{');
                var first = true;
                foreach (var (key, value) in map.OrderBy(pair => pair.Key, CodePointComparer.Instance))
                {
                    if (!first)
                    {
                        builder.Append(',');
                    }

                    first = false;
                    WriteString(builder, key);
                    builder.Append(':');
                    Write(builder, value);
                }

                builder.Append('}');
                break;
            case JsonArray list:
                builder.Append('[');
                for (var i = 0; i < list.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(',');
                    }

                    Write(builder, list[i]);
                }

                builder.Append(']');
                break;
            case JsonValue value when value.TryGetValue<string>(out var text):
                WriteString(builder, text);
                break;
            case JsonValue value when value.TryGetValue<bool>(out var flag):
                builder.Append(flag ? "true" : "false");
                break;
            default:
                // Numbers: the bodies carry money as text, so only counts,
                // chapters, ages and years reach here — Python ints.
                builder.Append(node.ToJsonString(Raw));
                break;
        }
    }

    /// <summary>Python's string encoder with <c>ensure_ascii=False</c>: only <c>"</c>, <c>\</c> and control characters escaped.</summary>
    private static void WriteString(StringBuilder builder, string text)
    {
        builder.Append('"');
        foreach (var c in text)
        {
            switch (c)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                default:
                    if (c < 0x20)
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        builder.Append('"');
    }

    /// <summary>Python's <c>sorted()</c> on str: by code point (UTF-16 ordinal differs only for astral characters).</summary>
    private sealed class CodePointComparer : IComparer<string>
    {
        public static readonly CodePointComparer Instance = new();

        public int Compare(string? x, string? y) => string.CompareOrdinal(x, y) is var ordinal && (x is null || y is null)
            ? ordinal
            : CompareRunes(x, y);

        private static int CompareRunes(string x, string y)
        {
            using var a = x.EnumerateRunes().GetEnumerator();
            using var b = y.EnumerateRunes().GetEnumerator();
            while (true)
            {
                var hasA = a.MoveNext();
                var hasB = b.MoveNext();
                if (!hasA || !hasB)
                {
                    return hasA.CompareTo(hasB);
                }

                var compare = a.Current.Value.CompareTo(b.Current.Value);
                if (compare != 0)
                {
                    return compare;
                }
            }
        }
    }
}
