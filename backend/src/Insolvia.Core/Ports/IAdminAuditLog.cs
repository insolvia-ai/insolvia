using Insolvia.Core.AdminAudit;

namespace Insolvia.Core.Ports;

/// <summary>
/// The admin service's append-only record of staff mutations (ADR 0011;
/// Python's <c>AuditLog</c>). WRITE-ONLY BY DESIGN on both sides of the
/// boundary: no read method here, and the admin role may only INSERT.
/// </summary>
public interface IAdminAuditLog
{
    /// <summary>Append one row. Every staff mutation writes one.</summary>
    Task RecordAsync(AdminEvent adminEvent, CancellationToken cancellationToken = default);
}
