using Insolvia.Core.Cases;
using Insolvia.Core.Packets;

namespace Insolvia.Core.Ports;

/// <summary>
/// Persists assembled-packet records (Python's <c>PacketStore</c>; issue #96)
/// AND writes the case's effective-dating pins in the same operation — which
/// is why it is its own port. Reached only through its case; written by the
/// worker, read by the API (and the filing worker).
/// </summary>
public interface IPacketStore
{
    /// <summary>
    /// Store the packet AND the pinned case, atomically — both or neither.
    /// The case write is conditional on its stored <c>updated_at</c> still being
    /// <paramref name="expectedUpdatedAt"/> (what the worker READ) and, unless
    /// <paramref name="allowFiled"/> (the amendedOnly exception, whose caller
    /// passes the case UNCHANGED), on it not being filed. False means the case
    /// moved — edited, filed or deleted mid-assembly — and nothing was written.
    /// The packet insert refuses an existing (case, id) by throwing.
    /// </summary>
    Task<bool> CreateAsync(PacketItem packet, Case pinnedCase, DateTimeOffset expectedUpdatedAt, bool allowFiled = false, CancellationToken cancellationToken = default);

    /// <summary>One packet, or null.</summary>
    Task<PacketItem?> GetAsync(string caseId, string packetId, CancellationToken cancellationToken = default);

    /// <summary>Every packet of one case, NEWEST FIRST (<see cref="Packets.PacketItems.NewestFirst"/>).</summary>
    Task<IReadOnlyList<PacketItem>> ListForCaseAsync(string caseId, CancellationToken cancellationToken = default);
}
