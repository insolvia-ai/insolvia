using Insolvia.Core.AccessLog;
using Insolvia.Core.Ports;

namespace Insolvia.Core.Memory;

/// <summary>
/// The in-memory <see cref="IAccessLog"/>. Unlike the real one it KEEPS what
/// it recorded, so a test can assert a read was logged — a test affordance,
/// not a capability the service has anywhere else.
/// </summary>
public sealed class MemoryAccessLog : IAccessLog
{
    private readonly Lock _gate = new();
    private readonly List<AccessEvent> _events = [];

    /// <summary>Every row recorded, in order.</summary>
    public IReadOnlyList<AccessEvent> Events
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
    public Task RecordAsync(AccessEvent accessEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accessEvent);
        lock (_gate)
        {
            _events.Add(accessEvent);
        }

        return Task.CompletedTask;
    }
}
