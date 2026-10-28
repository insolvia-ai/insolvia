using System.Text.Json.Nodes;
using Insolvia.Core;
using Insolvia.Core.Packets;
using Insolvia.Forms;

namespace Insolvia.Worker.Tests;

/// <summary>test_packets.py's stored-item half: the packet record through the store's shape and back.</summary>
public sealed class PacketItemTests
{
    private static readonly string[] OnlyB101 = ["b101"];

    private const string CaseId = "11111111-2222-4333-8444-000000000001";

    private static Packet MakePacket(OutputOptions? options = null) => Packets.NewPacket(
        CaseId,
        7,
        "11111111-2222-4333-8444-00000000a0b1",
        1234,
        new string('a', 64),
        new Dictionary<string, string>(StringComparer.Ordinal) { ["form/b101"] = "form/b101/2024-06-01" },
        "dollar_amounts/2025-04-01",
        3,
        "subject-1",
        options,
        [new PacketPart("01-b101.pdf", 1000, 9, 0, 0, false, new string('b', 64)), new PacketPart("creditor-matrix.txt", 34)]);

    [Fact]
    public void The_item_shape_round_trips_options_and_parts()
    {
        var packet = MakePacket(new OutputOptions { DraftWatermark = true, Forms = OnlyB101.ToValueCollection() });
        var back = Packets.FromItem(Packets.ToItem(packet));
        Assert.Equal(packet, back);
        Assert.Equal(Packets.PacketJson(packet).ToJsonString(), Packets.PacketJson(back).ToJsonString());
    }

    [Fact]
    public void A_packet_written_before_options_reads_as_the_plain_filing_set()
    {
        var item = Packets.ToItem(MakePacket()) with { Options = null, Parts = [] };
        var back = Packets.FromItem(item);
        Assert.Equal(FormOverlay.DefaultOutputOptions, back.Options);
        Assert.Empty(back.Parts);
    }

    [Theory]
    [InlineData("""[{"byteSize": 1}]""")]
    [InlineData("""[{"name": "x"}]""")]
    [InlineData("""[{"name": "x", "byteSize": "lots"}]""")]
    [InlineData("""["not a map"]""")]
    public void A_malformed_item_fails_loudly(string parts) =>
        Assert.Throws<ValidationException>(() => Packets.FromItem(Packets.ToItem(MakePacket()) with { Parts = JsonNode.Parse(parts)!.AsArray() }));

    [Fact]
    public void A_malformed_signature_mode_fails_loudly() =>
        Assert.Throws<ValidationException>(() => Packets.FromItem(Packets.ToItem(MakePacket()) with { Options = JsonNode.Parse("""{"signaturePages": "sideways"}""")!.AsObject() }));

    [Fact]
    public void The_store_lists_newest_first_with_an_id_tiebreak()
    {
        var at = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var item = Packets.ToItem(MakePacket());
        var older = item with { Id = "a", CreatedAt = at };
        var tieA = item with { Id = "b", CreatedAt = at.AddMinutes(1) };
        var tieB = item with { Id = "c", CreatedAt = at.AddMinutes(1) };
        Assert.Equal(["c", "b", "a"], new[] { older, tieA, tieB }.Order(PacketItems.NewestFirst).Select(p => p.Id));
    }
}
