using System.Text.Json.Nodes;

namespace Insolvia.Core.Packets;

/// <summary>
/// An assembled packet's metadata as the packet store holds it (Python's
/// <c>packet_item</c>) — the bytes are in the bucket. The domain record with
/// its typed output options is <c>Insolvia.Forms.Packet</c>; this is its
/// storage shape, so the port can live here without Core knowing the forms
/// engine. <see cref="Options"/> is the stored <c>output_options_json</c>
/// (null on a packet written before options existed — the plain filing set);
/// <see cref="Parts"/> the stored part list (<c>packet_part_item</c> each).
/// </summary>
public sealed record PacketItem(
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
    DateTimeOffset CreatedAt,
    JsonObject? Options,
    JsonArray Parts);

/// <summary>The packet store's rules.</summary>
public static class PacketItems
{
    /// <summary>Creation order with an id tie-break; the store answers its REVERSE (newest first).</summary>
    public static IComparer<PacketItem> ListOrder { get; } = Comparer<PacketItem>.Create((left, right) =>
    {
        var byTime = left!.CreatedAt.CompareTo(right!.CreatedAt);
        return byTime != 0 ? byTime : string.CompareOrdinal(left.Id, right.Id);
    });

    /// <summary>Newest first — what <see cref="Ports.IPacketStore.ListForCaseAsync"/> answers.</summary>
    public static IComparer<PacketItem> NewestFirst { get; } = Comparer<PacketItem>.Create((left, right) => ListOrder.Compare(right, left));
}
