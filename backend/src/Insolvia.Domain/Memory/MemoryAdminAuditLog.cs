using Insolvia.Core.AdminAudit;
using Insolvia.Core.Ports;

namespace Insolvia.Core.Memory;

/// <summary>
/// The in-memory <see cref="IAdminAuditLog"/>. <see cref="Events"/> is public
/// so a test can assert a mutating route wrote its row — the audit write is
/// part of every mutation's contract, and a suite that could not see it would
/// be testing half the route. A test affordance, not a capability the service
/// has anywhere else.
/// </summary>
public sealed class MemoryAdminAuditLog : IAdminAuditLog
{
    private readonly Lock _gate = new();
    private readonly List<AdminEvent> _events = [];

    /// <summary>Every row recorded, in order.</summary>
    public IReadOnlyList<AdminEvent> Events
    {
        get
        {
            lock (_gate)
            {
                return [.. _events];
            }
        }
    }

    /// <inheritdoc />
    public Task RecordAsync(AdminEvent adminEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adminEvent);
        lock (_gate)
        {
            _events.Add(adminEvent);
        }

        return Task.CompletedTask;
    }
}
