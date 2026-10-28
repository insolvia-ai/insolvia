using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Insolvia.Core;
using Insolvia.Core.Courts;

namespace Insolvia.Forms.Tests.Support;

/// <summary>The .NET half of a packet parity case: dump.py's input in, the parts and the outcome (in dump.py's shape) out.</summary>
internal static class Parity
{
    public static void WritePacket(JsonObject input, string directory)
    {
        System.IO.Directory.CreateDirectory(directory);
        var data = CaseDatas.Named((string)input["caseFile"]!);
        if ((bool)input["amended"]!)
        {
            data = CaseDatas.Amended(data);
        }

        var asOf = DateOnly.Parse((string)input["asOf"]!, CultureInfo.InvariantCulture);
        var printedAt = DateTimeOffset.Parse((string)input["printedAt"]!, CultureInfo.InvariantCulture);
        var options = FormOverlay.ParseOutputOptions(input["options"], allowForms: true);
        var (packet, problems) = PacketAssembly.Assemble(data, asOf, printedAt, options);
        JsonObject outcome;
        if (packet is null)
        {
            outcome = new JsonObject { ["problems"] = new JsonArray([.. problems.Select(p => (JsonNode)PacketAssembly.ProblemJson(p))]) };
        }
        else
        {
            foreach (var (name, content) in packet.Parts)
            {
                File.WriteAllBytes(Path.Combine(directory, name), content);
            }

            var zip = PacketAssembly.PacketZip(packet.Parts);
            File.WriteAllBytes(Path.Combine(directory, "packet.zip"), zip);
            var measured = PdfMeasure.MeasureParts(packet.Parts);
            var revisions = new JsonObject();
            foreach (var (series, pin) in packet.FormRevisions)
            {
                revisions[series] = pin;
            }

            outcome = new JsonObject
            {
                ["parts"] = new JsonArray([.. packet.Parts.Select(p => (JsonNode)p.Name)]),
                ["formRevisions"] = revisions,
                ["constantsSetId"] = packet.ConstantsSetId,
                ["creditorCount"] = packet.CreditorCount,
                ["measured"] = new JsonArray([.. measured.Select(part =>
                {
                    var json = Packets.PacketPartJson(part);
                    json.Remove("byteSize");
                    json.Remove("sha256");
                    return (JsonNode)json;
                })]),
                ["digest"] = Digest(data, Path.Combine(Path.GetDirectoryName(directory)!, "..", "python", Path.GetFileName(directory), "packet.json"), asOf),
            };
        }

        File.WriteAllText(Path.Combine(directory, "outcome.json"), outcome.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));
    }

    /// <summary>The approval digest over the packet record dump.py wrote (pypdf's measured parts): the canonical document must be Python's, byte for byte.</summary>
    private static JsonObject Digest(CaseData data, string pythonPacket, DateOnly asOf)
    {
        var record = JsonNode.Parse(File.ReadAllText(pythonPacket))!.AsObject();
        var packet = PacketFromJson(record);
        var release = CourtRegistry.Shipped.Resolve(asOf);
        var filingSet = FilingSets.Build(data, [packet], release, asOf);
        var district = data.Case.Court is { } court ? release.District(court) : null;
        var document = ApprovalDigest.BasisDocument(data, filingSet, ApprovalDigest.Fee(district));
        return new JsonObject
        {
            ["canonical"] = Encoding.UTF8.GetString(ApprovalDigest.CanonicalBytes(document)),
            ["sha256"] = ApprovalDigest.DigestOf(document),
        };
    }

    public static Packet PacketFromJson(JsonObject record)
    {
        var revisions = record["formRevisions"]!.AsObject().Select(pair => new KeyValuePair<string, string>(pair.Key, (string)pair.Value!)).ToValueDictionary();
        return new Packet(
            (string)record["id"]!,
            (string)record["caseId"]!,
            (string)record["jobId"]!,
            (string)record["fileName"]!,
            Packets.PacketContentType,
            (long)record["byteSize"]!,
            (string)record["sha256"]!,
            "cases/parity",
            revisions,
            (string)record["constantsSetId"]!,
            (int)record["creditorCount"]!,
            (string)record["createdBy"]!,
            (string)record["createdAt"]!)
        {
            Options = FormOverlay.ParseOutputOptions(record["options"], allowForms: true),
            Parts = record["parts"]!.AsArray().Select(node =>
            {
                var part = node!.AsObject();
                return new PacketPart(
                    (string)part["name"]!,
                    (long)part["byteSize"]!,
                    (int?)part["pageCount"],
                    (int?)part["nonLetterPages"],
                    (int?)part["pagesWithoutText"],
                    (bool?)part["unreadable"] ?? false,
                    (string?)part["sha256"]);
            }).ToValueCollection(),
        };
    }
}
