using Amazon;
using Amazon.RDS.Util;

namespace Insolvia.Data.Connections;

/// <summary>
/// An RDS IAM authentication token, signed locally (no network call) with the
/// Lambda role's credentials. A token is valid for 15 minutes for opening a
/// connection; a connection already open outlives it.
/// </summary>
public sealed class RdsIamTokenProvider : IDatabaseTokenProvider
{
    private readonly RegionEndpoint _region;

    /// <summary>Signs tokens for the cluster in <paramref name="region"/> (e.g. <c>us-east-1</c>).</summary>
    public RdsIamTokenProvider(string region)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(region);
        _region = RegionEndpoint.GetBySystemName(region);
    }

    /// <inheritdoc />
    public async ValueTask<string> GetTokenAsync(string host, int port, string username, CancellationToken cancellationToken) =>
        await RDSAuthTokenGenerator.GenerateAuthTokenAsync(_region, host, port, username).ConfigureAwait(false);
}
