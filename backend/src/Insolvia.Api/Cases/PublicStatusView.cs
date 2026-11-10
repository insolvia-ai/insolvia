using Insolvia.Domain.Cases;
using Insolvia.Domain.Clients;

namespace Insolvia.Api.Cases;

/// <summary>What a client may read of their case: its chapter and stage, and no id.</summary>
/// <param name="Chapter">The current chapter.</param>
/// <param name="Stage">The case's lifecycle status.</param>
public sealed record PublicStatusView(int Chapter, CaseStatus Stage)
{
    /// <summary>The view of <paramref name="status"/>.</summary>
    /// <param name="status">The bound case's projection.</param>
    /// <returns>The view.</returns>
    public static PublicStatusView Of(CasePublicStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return new(status.Chapter, status.Stage);
    }
}
