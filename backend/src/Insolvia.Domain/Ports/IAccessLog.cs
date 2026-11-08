using Insolvia.Core.AccessLog;

namespace Insolvia.Core.Ports;

/// <summary>
/// Append-only record of who read or changed which case, client, credential
/// or authorization. WRITE-ONLY BY DESIGN on both sides of the boundary: this
/// port has no read method, and the database role may only INSERT (ADR 0028).
/// </summary>
public interface IAccessLog
{
    /// <summary>Append one row.</summary>
    Task RecordAsync(AccessEvent accessEvent, CancellationToken cancellationToken = default);
}
