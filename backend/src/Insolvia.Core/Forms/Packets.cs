using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Insolvia.Core;
using Insolvia.Core.Packets;

namespace Insolvia.Forms;

/// <summary>
/// One file inside the packet zip as <see cref="PdfMeasure"/> measured it at
/// assembly (from <c>core/packets.py</c>): counts and sizes only, never
/// content (B121 is a part). The page facts are null for a part that is not
/// a PDF (the creditor matrix's <c>.txt</c>). <see cref="Sha256"/> is the
/// digest of the file's bytes — what the attorney's approval binds to.
/// </summary>
public sealed record PacketPart(
    string Name,
    long ByteSize,
    int? PageCount = null,
    int? NonLetterPages = null,
    int? PagesWithoutText = null,
    bool Unreadable = false,
    string? Sha256 = null);

/// <summary>One assembled packet of a case — metadata only; the bytes are the worker's to store.</summary>
public sealed record Packet(
    string Id,
    string CaseId,
    string JobId,
    string FileName,
    string ContentType,
    long ByteSize,
    string Sha256,
    string StorageRef,
    ValueDictionary<string, string> FormRevisions,
    string ConstantsSetId,
    int CreditorCount,
    string CreatedBy,
    string CreatedAt)
{
    /// <summary>The output options it was rendered with.</summary>
    public OutputOptions Options { get; init; } = FormOverlay.DefaultOutputOptions;

    /// <summary>What each file inside the zip measured, in zip order.</summary>
    public ValueCollection<PacketPart> Parts { get; init; } = ValueCollections.Create<PacketPart>([]);
}

/// <summary>The packet record's pure rules (from <c>core/packets.py</c>).</summary>
public static partial class Packets
{
    /// <summary>What a packet zip is, on the wire and in the download.</summary>
    public const string PacketContentType = "application/zip";

    [GeneratedRegex(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\z")]
    internal static partial Regex Uuid();

    /// <summary>The download name: the case's chapter and nothing else.</summary>
    public static string PacketFileName(int chapter) => $"chapter{chapter}-packet.zip";

    /// <summary>
    /// Whether a packet rendered with these options is a set a court could be
    /// handed: every form, every signature page, no watermark, not an
    /// amendment. A printed date and <c>/s/</c> signatures do not disqualify it.
    /// </summary>
    public static bool IsFilingSet(OutputOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return !options.DraftWatermark && options.Forms is null && options.SignaturePages == SignaturePagesMode.All && !options.AmendedOnly;
    }

    /// <summary>Where the bytes live: <c>cases/&lt;case&gt;/packets/&lt;packet&gt;</c>, from server-minted uuids only.</summary>
    public static string PacketObjectKey(string caseId, string packetId)
    {
        ArgumentNullException.ThrowIfNull(caseId);
        ArgumentNullException.ThrowIfNull(packetId);
        if (!Uuid().IsMatch(caseId) || !Uuid().IsMatch(packetId))
        {
            throw new ValidationException("object keys are built from server-minted uuids only");
        }

        return $"cases/{caseId}/packets/{packetId}";
    }

    /// <summary>Stamp an assembled packet with server-generated identity and its storage location.</summary>
    public static Packet NewPacket(
        string caseId,
        int chapter,
        string jobId,
        long byteSize,
        string sha256,
        IReadOnlyDictionary<string, string> formRevisions,
        string constantsSetId,
        int creditorCount,
        string createdBy,
        OutputOptions? options = null,
        IReadOnlyList<PacketPart>? parts = null,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(formRevisions);
        var packetId = Guid.NewGuid().ToString();
        return new Packet(
            packetId,
            caseId,
            jobId,
            PacketFileName(chapter),
            PacketContentType,
            byteSize,
            sha256,
            PacketObjectKey(caseId, packetId),
            formRevisions.ToValueDictionary(),
            constantsSetId,
            creditorCount,
            createdBy,
            Clock.NowMillis(clock).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture))
        {
            Options = options ?? FormOverlay.DefaultOutputOptions,
            Parts = (parts ?? []).ToValueCollection(),
        };
    }

    /// <summary>The stored (and wire) shape of one part — optional keys absent, never null.</summary>
    public static JsonObject PacketPartJson(PacketPart part)
    {
        ArgumentNullException.ThrowIfNull(part);
        var body = new JsonObject { ["name"] = part.Name, ["byteSize"] = part.ByteSize };
        if (part.PageCount is { } pages)
        {
            body["pageCount"] = pages;
        }

        if (part.NonLetterPages is { } nonLetter)
        {
            body["nonLetterPages"] = nonLetter;
        }

        if (part.PagesWithoutText is { } withoutText)
        {
            body["pagesWithoutText"] = withoutText;
        }

        if (part.Unreadable)
        {
            body["unreadable"] = true;
        }

        if (part.Sha256 is { } digest)
        {
            body["sha256"] = digest;
        }

        return body;
    }

    /// <summary>The API representation (no storage reference).</summary>
    public static JsonObject PacketJson(Packet packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        var revisions = new JsonObject();
        foreach (var (series, pin) in packet.FormRevisions)
        {
            revisions[series] = pin;
        }

        return new JsonObject
        {
            ["id"] = packet.Id,
            ["caseId"] = packet.CaseId,
            ["jobId"] = packet.JobId,
            ["fileName"] = packet.FileName,
            ["contentType"] = packet.ContentType,
            ["byteSize"] = packet.ByteSize,
            ["sha256"] = packet.Sha256,
            ["formRevisions"] = revisions,
            ["constantsSetId"] = packet.ConstantsSetId,
            ["creditorCount"] = packet.CreditorCount,
            ["createdBy"] = packet.CreatedBy,
            ["createdAt"] = packet.CreatedAt,
            ["options"] = FormOverlay.OutputOptionsJson(packet.Options),
        };
    }

    /// <summary>The stored shape (<c>packet_item</c>) the packet store holds.</summary>
    public static PacketItem ToItem(Packet packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        return new PacketItem(
            packet.Id,
            packet.CaseId,
            packet.JobId,
            packet.FileName,
            packet.ContentType,
            packet.ByteSize,
            packet.Sha256,
            packet.StorageRef,
            packet.FormRevisions,
            packet.ConstantsSetId,
            packet.CreditorCount,
            packet.CreatedBy,
            DateTimeOffset.Parse(packet.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
            FormOverlay.OutputOptionsJson(packet.Options),
            new JsonArray([.. packet.Parts.Select(part => (JsonNode)PacketPartJson(part))]));
    }

    /// <summary>
    /// Inverse of <see cref="ToItem"/> (<c>packet_from_item</c>), tolerant the
    /// stored item's way: absent options are the plain filing set. A row this
    /// service did not write is a loud <see cref="ValidationException"/>.
    /// </summary>
    public static Packet FromItem(PacketItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        try
        {
            return new Packet(
                item.Id,
                item.CaseId,
                item.JobId,
                item.FileName,
                item.ContentType,
                item.ByteSize,
                item.Sha256,
                item.StorageRef,
                item.FormRevisions,
                item.ConstantsSetId,
                item.CreditorCount,
                item.CreatedBy,
                item.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture))
            {
                Options = OptionsFromItem(item.Options),
                Parts = PartsFromItem(item.Parts).ToValueCollection(),
            };
        }
        catch (FormatException error)
        {
            throw new ValidationException($"stored packet item is malformed: {error.Message}", error);
        }
    }

    private static OutputOptions OptionsFromItem(JsonObject? raw)
    {
        if (raw is null)
        {
            return FormOverlay.DefaultOutputOptions;
        }

        var mode = Json.AsString(raw["signaturePages"]) ?? "all";
        var signaturePages = mode switch
        {
            "all" => SignaturePagesMode.All,
            "only" => SignaturePagesMode.Only,
            "omit" => SignaturePagesMode.Omit,
            _ => throw new FormatException($"options.signaturePages '{mode}' is invalid"),
        };
        var forms = raw["forms"] is JsonArray list && list.Count > 0 ? list.Select(form => form?.ToString() ?? string.Empty).ToValueCollection() : null;
        return new OutputOptions
        {
            DraftWatermark = Json.AsBool(raw["draftWatermark"]) ?? false,
            PrintDate = Json.AsBool(raw["printDate"]) ?? false,
            SignaturePages = signaturePages,
            SignElectronically = Json.AsBool(raw["signElectronically"]) ?? false,
            Forms = forms,
            AmendedOnly = Json.AsBool(raw["amendedOnly"]) ?? false,
        };
    }

    private static List<PacketPart> PartsFromItem(JsonArray raw)
    {
        var parts = new List<PacketPart>();
        foreach (var entry in raw)
        {
            if (entry is not JsonObject part)
            {
                throw new FormatException("a part is not a map");
            }

            var byteSize = Count(part, "byteSize") ?? throw new FormatException("a part has no byteSize");
            var digest = part["sha256"];
            if (digest is not null && Json.AsString(digest) is null)
            {
                throw new FormatException("a part's sha256 is not a string");
            }

            parts.Add(new PacketPart(
                Json.AsString(part["name"]) ?? throw new FormatException("a part has no name"),
                byteSize,
                (int?)Count(part, "pageCount"),
                (int?)Count(part, "nonLetterPages"),
                (int?)Count(part, "pagesWithoutText"),
                Json.AsBool(part["unreadable"]) ?? false,
                Json.AsString(digest)));
        }

        return parts;
    }

    private static long? Count(JsonObject raw, string key)
    {
        var value = raw[key];
        if (value is null)
        {
            return null;
        }

        if (Json.TryGetWholeNumber(value, out var number))
        {
            return number;
        }

        return Json.AsString(value) is { } text && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
            ? number
            : throw new FormatException($"part {key} has a non-numeric type");
    }
}
